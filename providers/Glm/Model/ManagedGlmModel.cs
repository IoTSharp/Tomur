namespace Tomur.Providers.Glm;

internal sealed class ManagedGlmModel : IDisposable
{
    private readonly Dictionary<string, ResidentTensor<float>> residentWeights;
    private TensorDataSource? dataSource;

    private ManagedGlmModel(
        ModelProbe probe,
        TensorDataSource dataSource,
        Dictionary<string, ResidentTensor<float>> residentWeights,
        ModelMemoryPlan memoryPlan,
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
        MemoryPlan = memoryPlan;
        ActualResidentBytes = actualResidentBytes;
    }

    public Tomur.Providers.ModelProviderManifest Manifest { get; }

    public GlmModelConfiguration Configuration { get; }

    public string ModelDirectory { get; }

    public ManagedTokenizer Tokenizer { get; }

    public SafeTensorCatalog Tensors { get; }

    public int TensorFileCount { get; }

    public int ResidentTensorCount => residentWeights.Count;

    public int OpenShardCount => GetDataSource().ShardCount;

    public ModelMemoryPlan MemoryPlan { get; }

    public long ActualResidentBytes { get; }

    internal TensorDataSource DataSource => GetDataSource();

    public static ManagedGlmModel Load(
        ModelProbe probe,
        int contextSize,
        long? availableMemoryBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var specs = ResidentWeightLayout.Create(probe.Configuration, probe.Tensors);
        var memoryPlan = ModelMemoryPlan.Create(
            probe.Configuration,
            specs,
            contextSize,
            availableMemoryBytes ?? ModelMemoryPlan.GetAvailableMemoryBytes());
        memoryPlan.EnsureFits();
        cancellationToken.ThrowIfCancellationRequested();

        TensorDataSource? source = null;
        var weights = new Dictionary<string, ResidentTensor<float>>(specs.Count, StringComparer.Ordinal);
        long actualResidentBytes = 0;
        try
        {
            source = new TensorDataSource(probe.Tensors);
            foreach (var spec in specs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resident = source.LoadFloat32(spec.Descriptor, cancellationToken);
                try
                {
                    weights.Add(spec.Descriptor.Name, resident);
                    actualResidentBytes = checked(
                        actualResidentBytes + checked((long)resident.Length * sizeof(float)));
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

            return new ManagedGlmModel(probe, source, weights, memoryPlan, actualResidentBytes);
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
            ? tensor.ReadOnlySpan
            : throw new KeyNotFoundException($"Resident model weight was not found: {name}");
    }

    public void GatherEmbeddings(ReadOnlySpan<int> tokenIds, Span<float> destination)
    {
        _ = GetDataSource();
        var configuration = Configuration;
        ScalarKernels.GatherEmbeddings(
            GetResidentWeight("model.embed_tokens.weight"),
            configuration.VocabularySize,
            configuration.HiddenSize,
            configuration.HiddenSize,
            tokenIds,
            destination,
            configuration.HiddenSize);
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

        ScalarKernels.MatVec(
            GetResidentWeight($"{prefix}gate_proj.weight"),
            intermediateSize,
            Configuration.HiddenSize,
            Configuration.HiddenSize,
            input,
            gate);
        ScalarKernels.MatVec(
            GetResidentWeight($"{prefix}up_proj.weight"),
            intermediateSize,
            Configuration.HiddenSize,
            Configuration.HiddenSize,
            input,
            up);
        ScalarKernels.SiLU(gate, activated);
        ScalarKernels.Multiply(activated, up, gate);
        ScalarKernels.MatVec(
            GetResidentWeight($"{prefix}down_proj.weight"),
            Configuration.HiddenSize,
            intermediateSize,
            intermediateSize,
            gate,
            destination);
    }

    public void Dispose()
    {
        var source = Interlocked.Exchange(ref dataSource, null);
        if (source is null)
        {
            return;
        }

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
