using System.Buffers;
using Tomur.Providers.Glm;

namespace Tomur.Providers.Olmoe;

internal sealed class OlmoeKvCache : IDisposable
{
    private readonly int[] validTokenCounts;
    private float[][]? keys;
    private float[][]? values;

    public OlmoeKvCache(OlmoeModelConfiguration configuration, int contextSize)
    {
        LayerCount = configuration.LayerCount;
        ContextSize = contextSize;
        KeyValueHeadCount = configuration.KeyValueHeadCount;
        HeadSize = configuration.HeadSize;
        KeyValueSize = configuration.KeyValueSize;
        var layerElements = checked((long)contextSize * KeyValueSize);
        if (layerElements > int.MaxValue)
        {
            throw new InvalidOperationException("One OLMoE KV layer exceeds the managed array element limit.");
        }

        keys = new float[LayerCount][];
        values = new float[LayerCount][];
        for (var layer = 0; layer < LayerCount; layer++)
        {
            keys[layer] = GC.AllocateUninitializedArray<float>((int)layerElements);
            values[layer] = GC.AllocateUninitializedArray<float>((int)layerElements);
        }

        validTokenCounts = new int[LayerCount];
        ByteLength = checked(layerElements * LayerCount * 2 * sizeof(float));
    }

    public int LayerCount { get; }
    public int ContextSize { get; }
    public int KeyValueHeadCount { get; }
    public int HeadSize { get; }
    public int KeyValueSize { get; }
    public long ByteLength { get; }

    public int GetValidTokenCount(int layer)
    {
        ValidateLayer(layer);
        _ = GetKeys();
        return validTokenCounts[layer];
    }

    public ReadOnlySpan<float> GetKeyHead(int layer, int tokenIndex, int head)
        => GetHead(GetKeys(), layer, tokenIndex, head);

    public ReadOnlySpan<float> GetValueHead(int layer, int tokenIndex, int head)
        => GetHead(GetValues(), layer, tokenIndex, head);

    public void Append(int layer, ReadOnlySpan<float> key, ReadOnlySpan<float> value)
    {
        ValidateLayer(layer);
        if (key.Length != KeyValueSize || value.Length != KeyValueSize)
        {
            throw new ArgumentException($"OLMoE KV entries must contain {KeyValueSize} elements.");
        }

        EnsureFinite(key, nameof(key));
        EnsureFinite(value, nameof(value));
        var tokenIndex = validTokenCounts[layer];
        if (tokenIndex >= ContextSize)
        {
            throw new ContextLengthExceededException(tokenIndex, 1, ContextSize);
        }

        key.CopyTo(GetKeys()[layer].AsSpan(checked(tokenIndex * KeyValueSize), KeyValueSize));
        value.CopyTo(GetValues()[layer].AsSpan(checked(tokenIndex * KeyValueSize), KeyValueSize));
        validTokenCounts[layer] = checked(tokenIndex + 1);
    }

    public void RollbackLayer(int layer, int validTokenCount)
    {
        ValidateLayer(layer);
        var current = validTokenCounts[layer];
        if ((uint)validTokenCount > (uint)current)
        {
            throw new ArgumentOutOfRangeException(nameof(validTokenCount));
        }

        if (current == validTokenCount)
        {
            return;
        }

        var offset = checked(validTokenCount * KeyValueSize);
        var length = checked((current - validTokenCount) * KeyValueSize);
        GetKeys()[layer].AsSpan(offset, length).Clear();
        GetValues()[layer].AsSpan(offset, length).Clear();
        validTokenCounts[layer] = validTokenCount;
    }

    public void Dispose()
    {
        var currentKeys = Interlocked.Exchange(ref keys, null);
        var currentValues = Interlocked.Exchange(ref values, null);
        if (currentKeys is not null)
        {
            foreach (var buffer in currentKeys)
            {
                buffer.AsSpan().Clear();
            }
        }

        if (currentValues is not null)
        {
            foreach (var buffer in currentValues)
            {
                buffer.AsSpan().Clear();
            }
        }

        validTokenCounts.AsSpan().Clear();
    }

    private ReadOnlySpan<float> GetHead(float[][] buffers, int layer, int tokenIndex, int head)
    {
        ValidateLayer(layer);
        if ((uint)tokenIndex >= (uint)validTokenCounts[layer] || (uint)head >= (uint)KeyValueHeadCount)
        {
            throw new ArgumentOutOfRangeException(
                (uint)tokenIndex >= (uint)validTokenCounts[layer] ? nameof(tokenIndex) : nameof(head));
        }

        var offset = checked((tokenIndex * KeyValueSize) + (head * HeadSize));
        return buffers[layer].AsSpan(offset, HeadSize);
    }

    private float[][] GetKeys()
    {
        ObjectDisposedException.ThrowIf(keys is null, this);
        return keys;
    }

    private float[][] GetValues()
    {
        ObjectDisposedException.ThrowIf(values is null, this);
        return values;
    }

    private void ValidateLayer(int layer)
    {
        if ((uint)layer >= (uint)LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
    }

    private static void EnsureFinite(ReadOnlySpan<float> values, string name)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException($"{name} value at index {index} must be finite.");
            }
        }
    }
}

internal sealed class OlmoeAttentionWorkspace : IDisposable
{
    private float[]? activations;
    private float[]? scores;

    public OlmoeAttentionWorkspace(OlmoeMemoryPlan plan)
    {
        activations = ArrayPool<float>.Shared.Rent(plan.AttentionActivationCapacity);
        scores = ArrayPool<float>.Shared.Rent(plan.AttentionScoreCapacity);
        ActivationCapacity = plan.AttentionActivationCapacity;
        ScoreCapacity = plan.AttentionScoreCapacity;
    }

    public int ActivationCapacity { get; }
    public int ScoreCapacity { get; }

    public Span<float> GetActivations(int length)
    {
        ObjectDisposedException.ThrowIf(activations is null, this);
        if (length < 0 || length > ActivationCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return activations.AsSpan(0, length);
    }

    public Span<float> GetScores(int length)
    {
        ObjectDisposedException.ThrowIf(scores is null, this);
        if (length < 0 || length > ScoreCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return scores.AsSpan(0, length);
    }

    public void Dispose()
    {
        var currentActivations = Interlocked.Exchange(ref activations, null);
        var currentScores = Interlocked.Exchange(ref scores, null);
        if (currentActivations is not null)
        {
            ArrayPool<float>.Shared.Return(currentActivations, clearArray: true);
        }

        if (currentScores is not null)
        {
            ArrayPool<float>.Shared.Return(currentScores, clearArray: true);
        }
    }
}

internal static class OlmoeAttention
{
    public static void RunToken(
        ManagedOlmoeModel model,
        int layer,
        ReadOnlySpan<float> input,
        OlmoeKvCache cache,
        SequenceState sequence,
        OlmoeAttentionWorkspace workspace,
        Span<float> destination,
        CancellationToken cancellationToken = default)
    {
        var configuration = model.Configuration;
        if ((uint)layer >= (uint)configuration.LayerCount || sequence.Layer != layer)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }

        if (input.Length != configuration.HiddenSize || destination.Length != configuration.HiddenSize)
        {
            throw new ArgumentException("OLMoE attention input and destination must match hidden_size.");
        }

        sequence.EnsureCanAppend();
        var cacheCount = cache.GetValidTokenCount(layer);
        if (cacheCount != sequence.Position)
        {
            throw new InvalidOperationException("OLMoE sequence position and KV cache length are inconsistent.");
        }

        var checkpoint = sequence.Capture();
        var hidden = configuration.HiddenSize;
        var keyValueSize = configuration.KeyValueSize;
        var required = checked((hidden * 3) + (keyValueSize * 3));
        var work = workspace.GetActivations(required);
        var offset = 0;
        var queryRaw = work.Slice(offset, hidden); offset += hidden;
        var query = work.Slice(offset, hidden); offset += hidden;
        var context = work.Slice(offset, hidden); offset += hidden;
        var keyRaw = work.Slice(offset, keyValueSize); offset += keyValueSize;
        var key = work.Slice(offset, keyValueSize); offset += keyValueSize;
        var value = work.Slice(offset, keyValueSize);
        var prefix = $"model.layers.{layer}.self_attn.";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            model.MultiplyResidentWeight($"{prefix}q_proj.weight", input, queryRaw);
            model.MultiplyResidentWeight($"{prefix}k_proj.weight", input, keyRaw);
            model.MultiplyResidentWeight($"{prefix}v_proj.weight", input, value);
            ScalarKernels.RmsNorm(
                queryRaw,
                model.GetResidentWeight($"{prefix}q_norm.weight"),
                configuration.RmsNormEpsilon,
                query);
            ScalarKernels.RmsNorm(
                keyRaw,
                model.GetResidentWeight($"{prefix}k_norm.weight"),
                configuration.RmsNormEpsilon,
                key);

            for (var head = 0; head < configuration.AttentionHeadCount; head++)
            {
                ApplySplitHalfRope(
                    query.Slice(head * configuration.HeadSize, configuration.HeadSize),
                    sequence.Position,
                    configuration.RopeTheta);
            }

            for (var head = 0; head < configuration.KeyValueHeadCount; head++)
            {
                ApplySplitHalfRope(
                    key.Slice(head * configuration.HeadSize, configuration.HeadSize),
                    sequence.Position,
                    configuration.RopeTheta);
            }

            cache.Append(layer, key, value);
            context.Clear();
            var tokenCount = checked(sequence.Position + 1);
            var scale = 1.0 / Math.Sqrt(configuration.HeadSize);
            for (var queryHead = 0; queryHead < configuration.AttentionHeadCount; queryHead++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyValueHead = queryHead / configuration.QueryHeadsPerKeyValueHead;
                var queryHeadValues = query.Slice(
                    queryHead * configuration.HeadSize,
                    configuration.HeadSize);
                var scores = workspace.GetScores(tokenCount);
                for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
                {
                    var cachedKey = cache.GetKeyHead(layer, tokenIndex, keyValueHead);
                    double dot = 0;
                    for (var component = 0; component < configuration.HeadSize; component++)
                    {
                        dot += (double)queryHeadValues[component] * cachedKey[component];
                    }

                    scores[tokenIndex] = (float)(dot * scale);
                }

                ScalarKernels.SoftmaxInPlace(scores);
                var headContext = context.Slice(
                    queryHead * configuration.HeadSize,
                    configuration.HeadSize);
                for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
                {
                    var cachedValue = cache.GetValueHead(layer, tokenIndex, keyValueHead);
                    var attentionWeight = scores[tokenIndex];
                    for (var component = 0; component < configuration.HeadSize; component++)
                    {
                        headContext[component] += attentionWeight * cachedValue[component];
                    }
                }
            }

            model.MultiplyResidentWeight($"{prefix}o_proj.weight", context, destination);
            EnsureFinite(destination);
            sequence.Advance();
        }
        catch
        {
            cache.RollbackLayer(layer, cacheCount);
            sequence.Restore(checkpoint);
            destination.Clear();
            throw;
        }
    }

    internal static void ApplySplitHalfRope(Span<float> values, int position, float theta)
    {
        if (values.IsEmpty || (values.Length & 1) != 0 || position < 0 ||
            !float.IsFinite(theta) || theta <= 0)
        {
            throw new ArgumentException("Split-half RoPE arguments are invalid.");
        }

        var half = values.Length / 2;
        for (var index = 0; index < half; index++)
        {
            var first = values[index];
            var second = values[index + half];
            var inverseFrequency = Math.Pow(theta, -2.0 * index / values.Length);
            var angle = position * inverseFrequency;
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            values[index] = (float)((first * cosine) - (second * sine));
            values[index + half] = (float)((second * cosine) + (first * sine));
        }
    }

    private static void EnsureFinite(ReadOnlySpan<float> values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException($"OLMoE attention output at index {index} is not finite.");
            }
        }
    }
}

internal static class OlmoeMoeExecutor
{
    public static async ValueTask RunTokenAsync(
        ManagedOlmoeModel model,
        int layer,
        ReadOnlyMemory<float> input,
        ExpertCache cache,
        MoeWorkspace workspace,
        Memory<float> destination,
        CancellationToken cancellationToken = default)
    {
        var configuration = model.Configuration;
        if ((uint)layer >= (uint)configuration.LayerCount ||
            input.Length != configuration.HiddenSize ||
            destination.Length != configuration.HiddenSize)
        {
            throw new ArgumentException("OLMoE MoE dimensions are invalid.");
        }

        workspace.EnsureCompatible(
            configuration.HiddenSize,
            configuration.RoutedExpertCount,
            configuration.ExpertsPerToken,
            configuration.IntermediateSize);
        if (!ReferenceEquals(cache.Layout, model.ExpertLayout))
        {
            throw new ArgumentException("Expert cache does not belong to this OLMoE model.", nameof(cache));
        }

        var gateName = $"model.layers.{layer}.mlp.gate.weight";
        model.MultiplyResidentWeight(gateName, input.Span, workspace.AdjustedScores);
        ScalarKernels.Softmax(workspace.AdjustedScores, workspace.Scores);
        ScalarKernels.TopK(
            workspace.Scores,
            configuration.ExpertsPerToken,
            workspace.SelectedExpertIds,
            workspace.SelectedWeights);
        if (configuration.NormalizeTopKProbabilities)
        {
            double denominator = 0;
            foreach (var weight in workspace.SelectedWeights)
            {
                denominator += weight;
            }

            if (!double.IsFinite(denominator) || denominator <= 0)
            {
                throw new InvalidDataException("OLMoE selected router weights have an invalid sum.");
            }

            for (var index = 0; index < workspace.SelectedWeights.Length; index++)
            {
                workspace.SelectedWeights[index] = (float)(workspace.SelectedWeights[index] / denominator);
            }
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var acquisition = cache.AcquireLayerAsync(
            layer,
            workspace.SelectedExpertIdMemory,
            operation.Token).AsTask();
        ExpertLeaseBatch? leases = null;
        try
        {
            leases = await acquisition.ConfigureAwait(false);
            await leases.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            var routed = workspace.RoutedOutput;
            routed.Clear();
            for (var route = 0; route < leases.Count; route++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expertOutput = workspace.ExpertOutput;
                leases[route].Run(input.Span, workspace, expertOutput);
                var weight = workspace.SelectedWeights[route];
                for (var component = 0; component < routed.Length; component++)
                {
                    routed[component] += weight * expertOutput[component];
                }
            }

            for (var index = 0; index < routed.Length; index++)
            {
                if (!float.IsFinite(routed[index]))
                {
                    throw new InvalidDataException($"OLMoE MoE output at index {index} is not finite.");
                }
            }

            routed.CopyTo(destination.Span);
        }
        catch
        {
            operation.Cancel();
            if (leases is null)
            {
                try
                {
                    leases = await acquisition.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            destination.Span.Clear();
            throw;
        }
        finally
        {
            leases?.Dispose();
        }
    }
}

internal sealed class OlmoeForwardContext : IDisposable
{
    private readonly ManagedOlmoeModel model;
    private readonly ExpertCache expertCache;
    private readonly OlmoeKvCache kvCache;
    private readonly OlmoeAttentionWorkspace attentionWorkspace;
    private readonly TensorWorkspace tensorWorkspace;
    private readonly MoeWorkspace moeWorkspace;
    private readonly SequenceState[] sequences;
    private float[]? hidden;
    private float[]? normalized;
    private float[]? sublayer;
    private float[]? residual;
    private bool faulted;

    public OlmoeForwardContext(ManagedOlmoeModel model, ExpertCache expertCache, int contextLimit)
    {
        this.model = model;
        this.expertCache = expertCache;
        ContextLimit = contextLimit;
        if (contextLimit <= 0 || contextLimit > model.MemoryPlan.ContextSize)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLimit));
        }

        var hiddenSize = model.Configuration.HiddenSize;
        hidden = ArrayPool<float>.Shared.Rent(hiddenSize);
        normalized = ArrayPool<float>.Shared.Rent(hiddenSize);
        sublayer = ArrayPool<float>.Shared.Rent(hiddenSize);
        residual = ArrayPool<float>.Shared.Rent(hiddenSize);
        try
        {
            kvCache = new OlmoeKvCache(model.Configuration, model.MemoryPlan.ContextSize);
            attentionWorkspace = new OlmoeAttentionWorkspace(model.MemoryPlan);
            tensorWorkspace = model.CreateTensorWorkspace();
            moeWorkspace = model.CreateMoeWorkspace();
            sequences = Enumerable.Range(0, model.Configuration.LayerCount)
                .Select(layer => new SequenceState(layer, model.Configuration.LayerCount, contextLimit))
                .ToArray();
        }
        catch
        {
            Return(Interlocked.Exchange(ref hidden, null));
            Return(Interlocked.Exchange(ref normalized, null));
            Return(Interlocked.Exchange(ref sublayer, null));
            Return(Interlocked.Exchange(ref residual, null));
            throw;
        }
    }

    public int ContextLimit { get; }
    public int Position => sequences[0].Position;

    public async ValueTask<ReadOnlyMemory<float>> ForwardAsync(
        ReadOnlyMemory<int> tokenIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(hidden is null, this);
        if (faulted)
        {
            throw new InvalidOperationException("The OLMoE forward context cannot be reused after failure.");
        }

        if (tokenIds.IsEmpty)
        {
            throw new ArgumentException("At least one token is required.", nameof(tokenIds));
        }

        foreach (var sequence in sequences)
        {
            sequence.EnsureCanAppend(tokenIds.Length);
        }

        try
        {
            for (var index = 0; index < tokenIds.Length; index++)
            {
                var tokenId = tokenIds.Span[index];
                await RunTokenAsync(tokenId, cancellationToken).ConfigureAwait(false);
            }

            return tensorWorkspace.GetOutputMemory(model.Configuration.VocabularySize);
        }
        catch
        {
            faulted = true;
            throw;
        }
    }

    private async ValueTask RunTokenAsync(int tokenId, CancellationToken cancellationToken)
    {
        var hiddenSize = model.Configuration.HiddenSize;
        var current = hidden!.AsMemory(0, hiddenSize);
        var norm = normalized!.AsMemory(0, hiddenSize);
        var output = sublayer!.AsMemory(0, hiddenSize);
        var skip = residual!.AsMemory(0, hiddenSize);
        model.GatherEmbedding(tokenId, current.Span);
        for (var layer = 0; layer < model.Configuration.LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = $"model.layers.{layer}.";
            model.Normalize($"{prefix}input_layernorm.weight", current.Span, norm.Span);
            OlmoeAttention.RunToken(
                model,
                layer,
                norm.Span,
                kvCache,
                sequences[layer],
                attentionWorkspace,
                output.Span,
                cancellationToken);
            ScalarKernels.Add(current.Span, output.Span, skip.Span);
            model.Normalize($"{prefix}post_attention_layernorm.weight", skip.Span, norm.Span);
            await OlmoeMoeExecutor.RunTokenAsync(
                model,
                layer,
                norm,
                expertCache,
                moeWorkspace,
                output,
                cancellationToken).ConfigureAwait(false);
            ScalarKernels.Add(skip.Span, output.Span, current.Span);
        }

        model.Normalize("model.norm.weight", current.Span, norm.Span);
        model.ProjectLogits(
            norm.Span,
            tensorWorkspace.GetOutputs(model.Configuration.VocabularySize));
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref hidden, null);
        if (current is null)
        {
            return;
        }

        Return(current);
        Return(Interlocked.Exchange(ref normalized, null));
        Return(Interlocked.Exchange(ref sublayer, null));
        Return(Interlocked.Exchange(ref residual, null));
        moeWorkspace.Dispose();
        tensorWorkspace.Dispose();
        attentionWorkspace.Dispose();
        kvCache.Dispose();
    }

    private static void Return(float[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }
}
