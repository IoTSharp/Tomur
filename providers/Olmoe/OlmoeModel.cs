using Tomur.Providers;
using Tomur.Providers.Glm;

namespace Tomur.Providers.Olmoe;

internal sealed record OlmoeMemoryPlan(
    int ContextSize,
    long ResidentBytes,
    long KvBytes,
    long ScratchBytes,
    long MoeWorkspaceBytes,
    long SamplingWorkspaceBytes,
    long RequiredBytes,
    long AvailableBytes,
    int ActivationCapacity,
    int QuantizationCapacity,
    int OutputCapacity,
    int AttentionActivationCapacity,
    int AttentionScoreCapacity)
{
    public static OlmoeMemoryPlan Create(
        OlmoeModelConfiguration configuration,
        IReadOnlyList<ResidentWeightSpec> residentWeights,
        int contextSize,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(residentWeights);
        if (contextSize <= 0 || contextSize > configuration.MaxPositionEmbeddings)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSize));
        }

        if (availableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableBytes));
        }

        long residentBytes = 0;
        foreach (var weight in residentWeights)
        {
            residentBytes = checked(residentBytes + weight.ResidentBytes);
        }

        var kvElements = checked(
            (long)configuration.LayerCount * contextSize * configuration.KeyValueSize * 2);
        var kvBytes = checked(kvElements * sizeof(float));
        var activationCapacity = Math.Max(
            configuration.HiddenSize,
            checked(configuration.IntermediateSize * 3));
        var quantizationCapacity = Math.Max(configuration.HiddenSize, configuration.IntermediateSize);
        var outputCapacity = Math.Max(configuration.VocabularySize, configuration.HiddenSize);
        var attentionActivationCapacity = checked(
            checked(configuration.HiddenSize * 3) +
            checked(configuration.KeyValueSize * 3));
        var attentionScoreCapacity = contextSize;
        var tensorWorkspaceBytes = checked(
            checked((long)activationCapacity * sizeof(float)) +
            quantizationCapacity +
            checked((long)outputCapacity * sizeof(float)));
        var attentionWorkspaceBytes = checked(
            checked((long)attentionActivationCapacity * sizeof(float)) +
            checked((long)attentionScoreCapacity * sizeof(float)));
        var forwardWorkspaceBytes = checked((long)configuration.HiddenSize * 4 * sizeof(float));
        var samplingWorkspaceBytes = checked(
            (long)configuration.VocabularySize * (sizeof(float) + sizeof(int)));
        var moeWorkspaceBytes = MoeWorkspace.GetBudgetedBytes(
            configuration.HiddenSize,
            configuration.RoutedExpertCount,
            configuration.ExpertsPerToken,
            configuration.IntermediateSize);
        var scratchBytes = checked(
            tensorWorkspaceBytes +
            attentionWorkspaceBytes +
            forwardWorkspaceBytes +
            samplingWorkspaceBytes +
            moeWorkspaceBytes);
        return new OlmoeMemoryPlan(
            contextSize,
            residentBytes,
            kvBytes,
            scratchBytes,
            moeWorkspaceBytes,
            samplingWorkspaceBytes,
            checked(residentBytes + kvBytes + scratchBytes),
            availableBytes,
            activationCapacity,
            quantizationCapacity,
            outputCapacity,
            attentionActivationCapacity,
            attentionScoreCapacity);
    }

    public void EnsureFits()
    {
        if (RequiredBytes > AvailableBytes)
        {
            throw new OlmoeMemoryBudgetExceededException(this);
        }
    }
}

internal sealed class OlmoeMemoryBudgetExceededException(OlmoeMemoryPlan plan)
    : InvalidOperationException(
        $"Managed OLMoE loading requires {plan.RequiredBytes} bytes " +
        $"(resident {plan.ResidentBytes}, KV {plan.KvBytes}, scratch {plan.ScratchBytes}), " +
        $"but only {plan.AvailableBytes} bytes are available.")
{
    public OlmoeMemoryPlan Plan { get; } = plan;
}

internal static class OlmoeResidentWeightLayout
{
    public static IReadOnlyList<ResidentWeightSpec> Create(
        OlmoeModelConfiguration configuration,
        SafeTensorCatalog tensors)
    {
        var specs = new List<ResidentWeightSpec>();
        Add(specs, tensors, "model.embed_tokens.weight", configuration.VocabularySize, configuration.HiddenSize);
        Add(specs, tensors, "model.norm.weight", configuration.HiddenSize);
        Add(specs, tensors, "lm_head.weight", configuration.VocabularySize, configuration.HiddenSize);
        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            var prefix = $"model.layers.{layer}.";
            Add(specs, tensors, $"{prefix}input_layernorm.weight", configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}post_attention_layernorm.weight", configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.q_proj.weight", configuration.HiddenSize, configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.k_proj.weight", configuration.KeyValueSize, configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.v_proj.weight", configuration.KeyValueSize, configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.o_proj.weight", configuration.HiddenSize, configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.q_norm.weight", configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}self_attn.k_norm.weight", configuration.KeyValueSize);
            Add(specs, tensors, $"{prefix}mlp.gate.weight", configuration.RoutedExpertCount, configuration.HiddenSize);
        }

        return specs;
    }

    private static void Add(
        ICollection<ResidentWeightSpec> specs,
        SafeTensorCatalog tensors,
        string name,
        params long[] expectedShape)
    {
        var descriptor = tensors.GetRequired(name);
        if (descriptor.DataType is not (
                TensorDataType.Float32 or TensorDataType.Float16 or TensorDataType.BFloat16))
        {
            throw new InvalidDataException(
                $"Resident OLMoE tensor '{name}' uses unsupported dtype {descriptor.DataTypeName}.");
        }

        if (!descriptor.LogicalShape.SequenceEqual(expectedShape))
        {
            throw new InvalidDataException(
                $"Resident OLMoE tensor '{name}' has shape [{string.Join(", ", descriptor.LogicalShape)}]; " +
                $"expected [{string.Join(", ", expectedShape)}].");
        }

        if (descriptor.ElementCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Resident OLMoE tensor '{name}' exceeds the single-buffer element limit.");
        }

        specs.Add(new ResidentWeightSpec(descriptor));
    }
}

internal sealed class ManagedOlmoeModel : IDisposable
{
    private readonly Dictionary<string, ResidentWeight> residentWeights;
    private readonly object expertCacheGate = new();
    private TensorDataSource? dataSource;
    private ExpertCache? expertCache;

    private ManagedOlmoeModel(
        OlmoeModelProbe probe,
        TensorDataSource dataSource,
        Dictionary<string, ResidentWeight> residentWeights,
        ExpertDescriptorLayout expertLayout,
        OlmoeMemoryPlan memoryPlan,
        long actualResidentBytes)
    {
        Manifest = probe.Manifest;
        Configuration = probe.Configuration;
        ModelDirectory = probe.ModelDirectory;
        Tokenizer = probe.Tokenizer;
        Tensors = probe.Tensors;
        TensorFileCount = probe.TensorFileCount;
        this.dataSource = dataSource;
        this.residentWeights = residentWeights;
        ExpertLayout = expertLayout;
        MemoryPlan = memoryPlan;
        ActualResidentBytes = actualResidentBytes;
    }

    public ModelProviderManifest Manifest { get; }
    public OlmoeModelConfiguration Configuration { get; }
    public string ModelDirectory { get; }
    public ManagedTokenizer Tokenizer { get; }
    public SafeTensorCatalog Tensors { get; }
    public int TensorFileCount { get; }
    public int ResidentTensorCount => residentWeights.Count;
    public int OpenShardCount => GetDataSource().ShardCount;
    public OlmoeMemoryPlan MemoryPlan { get; }
    public long ActualResidentBytes { get; }
    internal ExpertDescriptorLayout ExpertLayout { get; }

    public static ManagedOlmoeModel Load(
        OlmoeModelProbe probe,
        int contextSize,
        long? availableMemoryBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (contextSize > probe.Configuration.MaxPositionEmbeddings)
        {
            throw new ContextLengthExceededException(0, contextSize, probe.Configuration.MaxPositionEmbeddings);
        }

        var specs = OlmoeResidentWeightLayout.Create(probe.Configuration, probe.Tensors);
        var expertLayout = ExpertDescriptorLayout.Create(
            probe.Configuration.ExpertConfiguration,
            probe.Manifest.Quantization,
            probe.Manifest.QuantizationLayout,
            probe.Tensors);
        var memoryPlan = OlmoeMemoryPlan.Create(
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
                    actualResidentBytes = checked(actualResidentBytes + resident.ResidentBytes);
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
                    $"Resident memory accounting mismatch: planned {memoryPlan.ResidentBytes}, loaded {actualResidentBytes}.");
            }

            return new ManagedOlmoeModel(
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
        => residentWeights.TryGetValue(name, out var weight)
            ? weight.GetFloatingValues()
            : throw new KeyNotFoundException($"Resident model weight was not found: {name}");

    public void MultiplyResidentWeight(string name, ReadOnlySpan<float> input, Span<float> destination)
    {
        if (!residentWeights.TryGetValue(name, out var weight))
        {
            throw new KeyNotFoundException($"Resident model weight was not found: {name}");
        }

        weight.Multiply(input, destination);
    }

    public void GatherEmbedding(int tokenId, Span<float> destination)
    {
        Span<int> token = stackalloc int[1] { tokenId };
        residentWeights["model.embed_tokens.weight"].GatherRows(token, destination);
    }

    public void Normalize(string weightName, ReadOnlySpan<float> input, Span<float> destination)
        => ScalarKernels.RmsNorm(
            input,
            GetResidentWeight(weightName),
            Configuration.RmsNormEpsilon,
            destination);

    public void ProjectLogits(ReadOnlySpan<float> input, Span<float> destination)
        => MultiplyResidentWeight("lm_head.weight", input, destination);

    public TensorWorkspace CreateTensorWorkspace()
        => new(
            MemoryPlan.ActivationCapacity,
            MemoryPlan.QuantizationCapacity,
            MemoryPlan.OutputCapacity);

    public MoeWorkspace CreateMoeWorkspace()
        => new(
            Configuration.HiddenSize,
            Configuration.RoutedExpertCount,
            Configuration.ExpertsPerToken,
            Configuration.IntermediateSize);

    public ExpertCache CreateExpertCache(ExpertCacheOptions options)
    {
        lock (expertCacheGate)
        {
            if (expertCache is { IsDisposed: false })
            {
                throw new InvalidOperationException("This managed model already owns an active expert cache.");
            }

            expertCache = new ExpertCache(
                ExpertLayout,
                GetDataSource(),
                MemoryPlan.AvailableBytes,
                MemoryPlan.RequiredBytes,
                options);
            return expertCache;
        }
    }

    internal TensorDataSource DataSource => GetDataSource();

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
}
