using System.Buffers;

namespace Tomur.Providers.Glm;

internal sealed class ManagedForwardContext : IDisposable
{
    private readonly ManagedGlmModel model;
    private readonly ExpertCache? expertCache;
    private readonly KvCache kvCache;
    private readonly AttentionWorkspace attentionWorkspace;
    private readonly TensorWorkspace tensorWorkspace;
    private readonly MoeWorkspace? moeWorkspace;
    private readonly RouterLookaheadPrefetcher? routerLookahead;
    private readonly SequenceState[] sequences;
    private float[]? hiddenStates;
    private float[]? normalizedStates;
    private float[]? sublayerOutputs;
    private float[]? residualStates;
    private bool faulted;

    public ForwardTiming Timing { get; } = new();

    public ManagedForwardContext(
        ManagedGlmModel model,
        ExpertCache? expertCache,
        int contextLimit)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (contextLimit <= 0 || contextLimit > model.MemoryPlan.ContextSize)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLimit));
        }

        if (model.ExpertLayout.MoeLayerCount > 0 && expertCache is null)
        {
            throw new ArgumentNullException(nameof(expertCache));
        }

        this.model = model;
        this.expertCache = expertCache;
        ContextLimit = contextLimit;
        BatchCapacity = Math.Min(model.MemoryPlan.ForwardBatchSize, contextLimit);
        var stateCapacity = checked(BatchCapacity * model.Configuration.HiddenSize);

        float[]? hidden = null;
        float[]? normalized = null;
        float[]? sublayer = null;
        float[]? residual = null;
        KvCache? newKvCache = null;
        AttentionWorkspace? newAttentionWorkspace = null;
        TensorWorkspace? newTensorWorkspace = null;
        MoeWorkspace? newMoeWorkspace = null;
        try
        {
            hidden = ArrayPool<float>.Shared.Rent(stateCapacity);
            normalized = ArrayPool<float>.Shared.Rent(stateCapacity);
            sublayer = ArrayPool<float>.Shared.Rent(stateCapacity);
            residual = ArrayPool<float>.Shared.Rent(stateCapacity);
            newKvCache = model.CreateKvCache();
            newAttentionWorkspace = model.CreateAttentionWorkspace();
            newTensorWorkspace = model.CreateTensorWorkspace();
            if (model.ExpertLayout.MoeLayerCount > 0)
            {
                newMoeWorkspace = model.CreateMoeWorkspace();
                routerLookahead = new RouterLookaheadPrefetcher(model.Configuration, expertCache!);
            }

            hiddenStates = hidden;
            normalizedStates = normalized;
            sublayerOutputs = sublayer;
            residualStates = residual;
            kvCache = newKvCache;
            attentionWorkspace = newAttentionWorkspace;
            tensorWorkspace = newTensorWorkspace;
            moeWorkspace = newMoeWorkspace;
            sequences = Enumerable.Range(0, model.Configuration.LayerCount)
                .Select(layer => model.CreateSequenceState(layer, contextLimit))
                .ToArray();
        }
        catch
        {
            Return(hidden);
            Return(normalized);
            Return(sublayer);
            Return(residual);
            newMoeWorkspace?.Dispose();
            newTensorWorkspace?.Dispose();
            newAttentionWorkspace?.Dispose();
            newKvCache?.Dispose();
            throw;
        }
    }

    public int ContextLimit { get; }

    public int BatchCapacity { get; }

    public long RouterLookaheadPrefetches => routerLookahead?.RequestedExperts ?? 0;

    public int Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(hiddenStates is null, this);
            return sequences[0].Position;
        }
    }

    public async ValueTask<ReadOnlyMemory<float>> ForwardAsync(
        ReadOnlyMemory<int> tokenIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(hiddenStates is null, this);
        if (faulted)
        {
            throw new InvalidOperationException(
                "The forward context cannot be reused after a failed execution.");
        }

        if (tokenIds.IsEmpty)
        {
            throw new ArgumentException("At least one token is required for forward execution.", nameof(tokenIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var sequence in sequences)
        {
            sequence.EnsureCanAppend(tokenIds.Length);
        }

        try
        {
            var consumed = 0;
            while (consumed < tokenIds.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(BatchCapacity, tokenIds.Length - consumed);
                await RunBatchAsync(
                    tokenIds.Slice(consumed, count),
                    cancellationToken).ConfigureAwait(false);
                consumed = checked(consumed + count);
            }

            return tensorWorkspace.GetOutputMemory(model.Configuration.VocabularySize);
        }
        catch
        {
            faulted = true;
            throw;
        }
    }

    public void Dispose()
    {
        var hidden = Interlocked.Exchange(ref hiddenStates, null);
        if (hidden is null)
        {
            return;
        }

        Return(hidden);
        Return(Interlocked.Exchange(ref normalizedStates, null));
        Return(Interlocked.Exchange(ref sublayerOutputs, null));
        Return(Interlocked.Exchange(ref residualStates, null));
        moeWorkspace?.Dispose();
        tensorWorkspace.Dispose();
        attentionWorkspace.Dispose();
        kvCache.Dispose();
    }

    private async ValueTask RunBatchAsync(
        ReadOnlyMemory<int> tokenIds,
        CancellationToken cancellationToken)
    {
        var tokenCount = tokenIds.Length;
        var started = ForwardTimingScope.Start();
        GatherEmbeddings(tokenIds, tokenCount);
        Timing.AddEmbedding(ForwardTimingScope.Elapsed(started));
        Timing.AddBatch(tokenCount);
        for (var layer = 0; layer < model.Configuration.LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeLayerInputs(layer, tokenCount);
            started = ForwardTimingScope.Start();
            RunAttention(layer, tokenCount, cancellationToken);
            Timing.AddAttention(ForwardTimingScope.Elapsed(started));
            AddAttentionResidual(tokenCount);
            NormalizePostAttention(layer, tokenCount);

            if (layer < model.Configuration.FirstMoeLayer)
            {
                started = ForwardTimingScope.Start();
                RunDenseLayer(layer, tokenCount);
                Timing.AddDense(ForwardTimingScope.Elapsed(started));
            }
            else
            {
                started = ForwardTimingScope.Start();
                await RunMoeLayerAsync(layer, tokenCount, cancellationToken).ConfigureAwait(false);
                Timing.AddMoe(ForwardTimingScope.Elapsed(started));
            }

            CommitLayer(tokenCount);
        }

        started = ForwardTimingScope.Start();
        ProjectLastToken(tokenCount);
        Timing.AddProjection(ForwardTimingScope.Elapsed(started));
    }

    private void GatherEmbeddings(ReadOnlyMemory<int> tokenIds, int tokenCount)
        => model.GatherEmbeddings(
            tokenIds.Span,
            GetHiddenStates().AsSpan(0, GetStateLength(tokenCount)));

    private void NormalizeLayerInputs(int layer, int tokenCount)
    {
        var hidden = GetHiddenStates();
        var normalized = GetNormalizedStates();
        var hiddenSize = model.Configuration.HiddenSize;
        for (var token = 0; token < tokenCount; token++)
        {
            var offset = checked(token * hiddenSize);
            model.NormalizeLayerInput(
                layer,
                hidden.AsSpan(offset, hiddenSize),
                normalized.AsSpan(offset, hiddenSize));
        }
    }

    private void RunAttention(int layer, int tokenCount, CancellationToken cancellationToken)
    {
        var length = GetStateLength(tokenCount);
        model.RunAttentionPrefill(
            layer,
            GetNormalizedStates().AsSpan(0, length),
            tokenCount,
            kvCache,
            sequences[layer],
            attentionWorkspace,
            GetSublayerOutputs().AsSpan(0, length),
            cancellationToken);
    }

    private void AddAttentionResidual(int tokenCount)
    {
        var length = GetStateLength(tokenCount);
        ScalarKernels.Add(
            GetHiddenStates().AsSpan(0, length),
            GetSublayerOutputs().AsSpan(0, length),
            GetResidualStates().AsSpan(0, length));
    }

    private void NormalizePostAttention(int layer, int tokenCount)
    {
        var residual = GetResidualStates();
        var normalized = GetNormalizedStates();
        var hiddenSize = model.Configuration.HiddenSize;
        for (var token = 0; token < tokenCount; token++)
        {
            var offset = checked(token * hiddenSize);
            model.NormalizePostAttention(
                layer,
                residual.AsSpan(offset, hiddenSize),
                normalized.AsSpan(offset, hiddenSize));
        }
    }

    private void RunDenseLayer(int layer, int tokenCount)
    {
        var normalized = GetNormalizedStates();
        var outputs = GetSublayerOutputs();
        var hiddenSize = model.Configuration.HiddenSize;
        for (var token = 0; token < tokenCount; token++)
        {
            var offset = checked(token * hiddenSize);
            model.RunDenseMlp(
                layer,
                normalized.AsSpan(offset, hiddenSize),
                tensorWorkspace,
                outputs.AsSpan(offset, hiddenSize));
        }
    }

    private async ValueTask RunMoeLayerAsync(
        int layer,
        int tokenCount,
        CancellationToken cancellationToken)
    {
        var hiddenSize = model.Configuration.HiddenSize;
        if (tokenCount == 1)
        {
            await routerLookahead!.PrefetchAsync(layer, cancellationToken).ConfigureAwait(false);
        }

        if (tokenCount > 1)
        {
            await PrefetchBatchExpertsAsync(layer, tokenCount, hiddenSize, cancellationToken)
                .ConfigureAwait(false);
        }

        for (var token = 0; token < tokenCount; token++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked(token * hiddenSize);
            await model.RunMoeTokenAsync(
                layer,
                GetNormalizedStates().AsMemory(offset, hiddenSize),
                expertCache!,
                moeWorkspace!,
                GetSublayerOutputs().AsMemory(offset, hiddenSize),
                cancellationToken).ConfigureAwait(false);
            routerLookahead!.Observe(layer, moeWorkspace!.SelectedExpertIds);
        }
    }

    private async ValueTask PrefetchBatchExpertsAsync(
        int layer,
        int tokenCount,
        int hiddenSize,
        CancellationToken cancellationToken)
    {
        var expertsPerToken = model.Configuration.ExpertsPerToken;
        var ids = ArrayPool<int>.Shared.Rent(checked(tokenCount * expertsPerToken));
        try
        {
            var uniqueCount = 0;
            var normalized = GetNormalizedStates();
            for (var token = 0; token < tokenCount; token++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = checked(token * hiddenSize);
                MoeRouter.Route(
                    model,
                    layer,
                    normalized.AsSpan(offset, hiddenSize),
                    moeWorkspace!);
                foreach (var expertId in moeWorkspace.SelectedExpertIds)
                {
                    var found = false;
                    for (var index = 0; index < uniqueCount; index++)
                    {
                        if (ids[index] == expertId)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        ids[uniqueCount++] = expertId;
                    }
                }
            }

            if (uniqueCount > 0 && uniqueCount <= expertCache!.SlotCapacityPerLayer)
            {
                await expertCache.PrefetchLayerAsync(
                    layer,
                    ids.AsMemory(0, uniqueCount),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ids, clearArray: true);
        }
    }

    private void CommitLayer(int tokenCount)
    {
        var length = GetStateLength(tokenCount);
        ScalarKernels.Add(
            GetResidualStates().AsSpan(0, length),
            GetSublayerOutputs().AsSpan(0, length),
            GetHiddenStates().AsSpan(0, length));
    }

    private void ProjectLastToken(int tokenCount)
    {
        var hiddenSize = model.Configuration.HiddenSize;
        var offset = checked((tokenCount - 1) * hiddenSize);
        var normalized = GetNormalizedStates().AsSpan(0, hiddenSize);
        model.NormalizeFinal(
            GetHiddenStates().AsSpan(offset, hiddenSize),
            normalized);
        model.ProjectLogits(
            normalized,
            tensorWorkspace.GetOutputs(model.Configuration.VocabularySize));
    }

    private int GetStateLength(int tokenCount)
        => checked(tokenCount * model.Configuration.HiddenSize);

    private float[] GetHiddenStates()
    {
        ObjectDisposedException.ThrowIf(hiddenStates is null, this);
        return hiddenStates;
    }

    private float[] GetNormalizedStates()
    {
        ObjectDisposedException.ThrowIf(normalizedStates is null, this);
        return normalizedStates;
    }

    private float[] GetSublayerOutputs()
    {
        ObjectDisposedException.ThrowIf(sublayerOutputs is null, this);
        return sublayerOutputs;
    }

    private float[] GetResidualStates()
    {
        ObjectDisposedException.ThrowIf(residualStates is null, this);
        return residualStates;
    }

    private static void Return(float[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }
}
