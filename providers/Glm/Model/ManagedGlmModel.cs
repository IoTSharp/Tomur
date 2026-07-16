namespace Tomur.Providers.Glm;

internal sealed class ManagedGlmModel : IDisposable
{
    private readonly Dictionary<string, ResidentWeight> residentWeights;
    private readonly object expertCacheGate = new();
    private ExpertCache? expertCache;
    private KvContextBudget? isolatedKvBudget;
    private TensorDataSource? dataSource;

    private ManagedGlmModel(
        ModelProbe probe,
        TensorDataSource dataSource,
        Dictionary<string, ResidentWeight> residentWeights,
        ExpertDescriptorLayout expertLayout,
        ModelMemoryPlan memoryPlan,
        long actualResidentBytes)
    {
        Manifest = probe.Manifest;
        Configuration = probe.Configuration;
        ModelDirectory = probe.ModelDirectory;
        Tokenizer = probe.Tokenizer;
        Tensors = probe.Tensors;
        AdvancedFeatures = probe.AdvancedFeatures;
        TensorFileCount = probe.TensorFileCount;
        this.dataSource = dataSource;
        this.residentWeights = residentWeights;
        ExpertLayout = expertLayout;
        MemoryPlan = memoryPlan;
        ActualResidentBytes = actualResidentBytes;
    }

    public Tomur.Providers.ModelProviderManifest Manifest { get; }

    public GlmModelConfiguration Configuration { get; }

    public string ModelDirectory { get; }

    public ManagedTokenizer Tokenizer { get; }

    public SafeTensorCatalog Tensors { get; }

    public AdvancedFeatureProbe AdvancedFeatures { get; }

    public int TensorFileCount { get; }

    public int ResidentTensorCount => residentWeights.Count;

    public int OpenShardCount => GetDataSource().ShardCount;

    public TensorIoMode IoMode => GetDataSource().IoMode;

    public ModelMemoryPlan MemoryPlan { get; }

    public long ActualResidentBytes { get; }

    internal ExpertDescriptorLayout ExpertLayout { get; }

    internal TensorDataSource DataSource => GetDataSource();

    public static ManagedGlmModel Load(
        ModelProbe probe,
        int contextSize,
        long? availableMemoryBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (contextSize > probe.Configuration.MaxPositionEmbeddings)
        {
            throw new ContextLengthExceededException(
                position: 0,
                requestedTokenCount: contextSize,
                contextLimit: probe.Configuration.MaxPositionEmbeddings);
        }

        var specs = ResidentWeightLayout.Create(
            probe.Configuration,
            probe.Tensors,
            probe.Manifest.Quantization,
            probe.Manifest.QuantizationLayout,
            probe.AdvancedFeatures);
        var expertLayout = ExpertDescriptorLayout.Create(
            probe.Configuration,
            probe.Manifest.Quantization,
            probe.Manifest.QuantizationLayout,
            probe.Tensors);
        var memoryPlan = ModelMemoryPlan.Create(
            probe.Configuration,
            specs,
            contextSize,
            availableMemoryBytes ?? ModelMemoryPlan.GetAvailableMemoryBytes());
        memoryPlan.EnsureFits();
        cancellationToken.ThrowIfCancellationRequested();

        TensorDataSource? source = null;
        var weights = new Dictionary<string, ResidentWeight>(specs.Count, StringComparer.Ordinal);
        long actualResidentBytes = 0;
        try
        {
            source = new TensorDataSource(probe.Tensors);
            foreach (var spec in specs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resident = ResidentWeight.Load(source, spec, cancellationToken);
                try
                {
                    weights.Add(spec.Descriptor.Name, resident);
                    actualResidentBytes = checked(
                        actualResidentBytes + resident.ResidentBytes);
                }
                catch
                {
                    resident.Dispose();
                    throw;
                }
            }

            if (actualResidentBytes != memoryPlan.ResidentBytes)
            {
                throw new InvalidDataException(
                    $"Resident memory accounting mismatch: planned {memoryPlan.ResidentBytes} bytes, loaded {actualResidentBytes} bytes.");
            }

            return new ManagedGlmModel(
                probe,
                source,
                weights,
                expertLayout,
                memoryPlan,
                actualResidentBytes);
        }
        catch
        {
            foreach (var weight in weights.Values)
            {
                weight.Dispose();
            }

            source?.Dispose();
            throw;
        }
    }

    public ReadOnlySpan<float> GetResidentWeight(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = GetDataSource();
        return residentWeights.TryGetValue(name, out var tensor)
            ? tensor.GetFloatingValues()
            : throw new KeyNotFoundException($"Resident model weight was not found: {name}");
    }

    public void MultiplyResidentWeight(
        string name,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = GetDataSource();
        if (!residentWeights.TryGetValue(name, out var tensor))
        {
            throw new KeyNotFoundException($"Resident model weight was not found: {name}");
        }

        tensor.Multiply(input, destination);
    }

    public void MultiplyResidentWeightPair(
        string firstName,
        string secondName,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondName);
        _ = GetDataSource();
        if (!residentWeights.TryGetValue(firstName, out var first))
        {
            throw new KeyNotFoundException($"Resident model weight was not found: {firstName}");
        }

        if (!residentWeights.TryGetValue(secondName, out var second))
        {
            throw new KeyNotFoundException($"Resident model weight was not found: {secondName}");
        }

        first.MultiplyPair(second, input, firstDestination, secondDestination);
    }

    public float GetResidentWeightValue(string name, int row, int column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = GetDataSource();
        return residentWeights.TryGetValue(name, out var tensor)
            ? tensor.GetValue(row, column)
            : throw new KeyNotFoundException($"Resident model weight was not found: {name}");
    }

    public void GatherEmbeddings(ReadOnlySpan<int> tokenIds, Span<float> destination)
    {
        _ = GetDataSource();
        residentWeights["model.embed_tokens.weight"].GatherRows(tokenIds, destination);
    }

    public void NormalizeLayerInput(
        int layer,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ValidateLayer(layer);
        ScalarKernels.RmsNorm(
            input,
            GetResidentWeight($"model.layers.{layer}.input_layernorm.weight"),
            Configuration.RmsNormEpsilon,
            destination);
    }

    public void NormalizePostAttention(
        int layer,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ValidateLayer(layer);
        ScalarKernels.RmsNorm(
            input,
            GetResidentWeight($"model.layers.{layer}.post_attention_layernorm.weight"),
            Configuration.RmsNormEpsilon,
            destination);
    }

    public void NormalizeFinal(ReadOnlySpan<float> input, Span<float> destination)
    {
        _ = GetDataSource();
        ScalarKernels.RmsNorm(
            input,
            GetResidentWeight("model.norm.weight"),
            Configuration.RmsNormEpsilon,
            destination);
    }

    public void ProjectLogits(ReadOnlySpan<float> input, Span<float> destination)
    {
        _ = GetDataSource();
        MultiplyResidentWeight("lm_head.weight", input, destination);
    }

    public void ProjectMtpLogits(ReadOnlySpan<float> input, Span<float> destination)
    {
        var tensorName = AdvancedFeatures.MtpHeadTensorName
            ?? throw new InvalidOperationException("The managed model does not contain a validated MTP head.");
        MultiplyResidentWeight(tensorName, input, destination);
    }

    public void RunDenseMlp(
        int layer,
        ReadOnlySpan<float> input,
        TensorWorkspace workspace,
        Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateLayer(layer);
        if (layer >= Configuration.FirstMoeLayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(layer),
                $"Layer {layer} is a MoE layer and does not have a resident dense MLP.");
        }

        if (input.Length != Configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"Dense MLP input must contain {Configuration.HiddenSize} elements.",
                nameof(input));
        }

        if (destination.Length != Configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"Dense MLP destination must contain {Configuration.HiddenSize} elements.",
                nameof(destination));
        }

        var intermediateSize = Configuration.DenseIntermediateSize;
        var work = workspace.GetActivations(checked(intermediateSize * 3));
        var gate = work[..intermediateSize];
        var up = work.Slice(intermediateSize, intermediateSize);
        var activated = work.Slice(checked(intermediateSize * 2), intermediateSize);
        var prefix = $"model.layers.{layer}.mlp.";

        MultiplyResidentWeightPair(
            $"{prefix}gate_proj.weight",
            $"{prefix}up_proj.weight",
            input,
            gate,
            up);
        ScalarKernels.SiLU(gate, activated);
        ScalarKernels.Multiply(activated, up, gate);
        MultiplyResidentWeight($"{prefix}down_proj.weight", gate, destination);
    }

    public KvCache CreateKvCache()
    {
        _ = GetDataSource();
        return new KvCache(Configuration, MemoryPlan.ContextSize);
    }

    public IsolatedKvContext CreateIsolatedKvContext(int? contextLimit = null)
    {
        _ = GetDataSource();
        var effectiveLimit = contextLimit ?? MemoryPlan.ContextSize;
        if (effectiveLimit <= 0 || effectiveLimit > MemoryPlan.ContextSize)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLimit));
        }

        lock (expertCacheGate)
        {
            isolatedKvBudget ??= new KvContextBudget(Math.Max(
                0,
                MemoryPlan.AvailableBytes -
                MemoryPlan.RequiredBytes -
                (expertCache?.BudgetedBytes ?? 0)));
            return new IsolatedKvContext(Configuration, effectiveLimit, isolatedKvBudget);
        }
    }

    public AttentionWorkspace CreateAttentionWorkspace()
    {
        _ = GetDataSource();
        return new AttentionWorkspace(
            Configuration,
            MemoryPlan.AttentionActivationCapacity,
            MemoryPlan.AttentionScoreCapacity);
    }

    public MoeWorkspace CreateMoeWorkspace()
    {
        _ = GetDataSource();
        return new MoeWorkspace(Configuration);
    }

    public TensorWorkspace CreateTensorWorkspace()
    {
        _ = GetDataSource();
        return new TensorWorkspace(
            MemoryPlan.ActivationCapacity,
            MemoryPlan.QuantizationCapacity,
            MemoryPlan.OutputCapacity);
    }

    public ExpertCache CreateExpertCache(ExpertCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (expertCacheGate)
        {
            if (expertCache is { IsDisposed: false })
            {
                throw new InvalidOperationException("This managed model already owns an active expert cache.");
            }

            if (isolatedKvBudget?.UsedBytes > 0)
            {
                throw new InvalidOperationException(
                    "An expert cache cannot be created after isolated KV contexts have reserved the remaining model memory budget.");
            }

            isolatedKvBudget = null;
            var cache = new ExpertCache(ExpertLayout, GetDataSource(), MemoryPlan, options);
            expertCache = cache;
            return cache;
        }
    }

    public SequenceState CreateSequenceState(int layer, int? contextLimit = null)
    {
        ValidateLayer(layer);
        var effectiveLimit = contextLimit ?? MemoryPlan.ContextSize;
        if (effectiveLimit <= 0 || effectiveLimit > MemoryPlan.ContextSize)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLimit));
        }

        return new SequenceState(layer, Configuration.LayerCount, effectiveLimit);
    }

    public void RunAttentionToken(
        int layer,
        ReadOnlySpan<float> input,
        KvCache cache,
        SequenceState sequence,
        AttentionWorkspace workspace,
        Span<float> destination,
        CancellationToken cancellationToken = default,
        AttentionTrace? trace = null,
        MlaAttentionMode mode = MlaAttentionMode.Absorbed)
    {
        ValidateLayer(layer);
        MlaAttention.RunToken(
            this,
            layer,
            input,
            cache,
            sequence,
            workspace,
            destination,
            cancellationToken,
            trace,
            mode);
    }

    public void RunAttentionPrefill(
        int layer,
        ReadOnlySpan<float> inputs,
        int tokenCount,
        KvCache cache,
        SequenceState sequence,
        AttentionWorkspace workspace,
        Span<float> destinations,
        CancellationToken cancellationToken = default,
        AttentionTrace? lastTokenTrace = null,
        MlaAttentionMode mode = MlaAttentionMode.Absorbed)
    {
        ValidateLayer(layer);
        MlaAttention.RunPrefill(
            this,
            layer,
            inputs,
            tokenCount,
            cache,
            sequence,
            workspace,
            destinations,
            cancellationToken,
            lastTokenTrace,
            mode);
    }

    public ValueTask RunMoeTokenAsync(
        int layer,
        ReadOnlyMemory<float> input,
        ExpertCache cache,
        MoeWorkspace workspace,
        Memory<float> destination,
        CancellationToken cancellationToken = default,
        MoeTrace? trace = null)
    {
        ValidateLayer(layer);
        return MoeExecutor.RunTokenAsync(
            this,
            layer,
            input,
            cache,
            workspace,
            destination,
            cancellationToken,
            trace);
    }

    public void Dispose()
    {
        TensorDataSource? source;
        ExpertCache? cache;
        lock (expertCacheGate)
        {
            source = Interlocked.Exchange(ref dataSource, null);
            cache = expertCache;
            expertCache = null;
        }

        if (source is null)
        {
            return;
        }

        cache?.Dispose();

        foreach (var weight in residentWeights.Values)
        {
            weight.Dispose();
        }

        residentWeights.Clear();
        source.Dispose();
    }

    private TensorDataSource GetDataSource()
    {
        ObjectDisposedException.ThrowIf(dataSource is null, this);
        return dataSource;
    }

    private void ValidateLayer(int layer)
    {
        _ = GetDataSource();
        if ((uint)layer >= (uint)Configuration.LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
    }
}
