using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M6.Tests;

public sealed class ResidentModelTests
{
    [Fact]
    public void ResidentLoadAccountsMemoryAndMatchesEmbeddingAndNormOracle()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var probe = ReadProbe(fixturePath);

        using var model = ManagedGlmModel.Load(probe, oracle.ModelConfiguration.ContextSize, long.MaxValue);

        Assert.Equal(model.MemoryPlan.ResidentBytes, model.ActualResidentBytes);
        Assert.Equal(
            model.MemoryPlan.RequiredBytes,
            model.MemoryPlan.ResidentBytes + model.MemoryPlan.KvBytes + model.MemoryPlan.ScratchBytes);
        Assert.Equal(256L, model.MemoryPlan.KvBytes);
        Assert.Equal(17, model.ResidentTensorCount);
        Assert.Equal(1, model.OpenShardCount);
        Assert.Throws<KeyNotFoundException>(() => ReadFirstRoutedExpertValue(model));

        var tokenIds = oracle.Tokenization.Single(item => item.Text == "hello Tomur").TokenIds.ToArray();
        var embeddings = new float[tokenIds.Length * oracle.ModelConfiguration.HiddenSize];
        model.GatherEmbeddings(tokenIds, embeddings);
        AssertClose(oracle.GetCheckpoint("embedding.lookup").Values, embeddings, oracle.Tolerances);

        var normInput = oracle.GetCheckpoint("rms_norm.input").Values.ToArray();
        var normOutput = new float[normInput.Length];
        model.NormalizeLayerInput(0, normInput, normOutput);
        AssertClose(oracle.GetCheckpoint("rms_norm.output").Values, normOutput, oracle.Tolerances);
    }

    [Fact]
    public void DenseMlpMatchesFixtureOracleWhenLayerIsConfiguredDense()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var configPath = Path.Combine(fixturePath, TinyFixtureFiles.Configuration);
        var configuration = File.ReadAllText(configPath);
        File.WriteAllText(
            configPath,
            configuration.Replace(
                "\"first_k_dense_replace\": 0",
                "\"first_k_dense_replace\": 1",
                StringComparison.Ordinal));
        var probe = ReadProbe(fixturePath);

        using var model = ManagedGlmModel.Load(probe, oracle.ModelConfiguration.ContextSize, long.MaxValue);
        using var workspace = new TensorWorkspace(
            model.MemoryPlan.ActivationCapacity,
            model.MemoryPlan.QuantizationCapacity,
            model.MemoryPlan.OutputCapacity);
        var input = oracle.GetCheckpoint("dense_mlp.input").Values.ToArray();
        var output = new float[oracle.ModelConfiguration.HiddenSize];

        model.RunDenseMlp(0, input, workspace, output);

        Assert.Equal(15, model.ResidentTensorCount);
        AssertClose(oracle.GetCheckpoint("dense_mlp.output").Values, output, oracle.Tolerances);
    }

    [Fact]
    public void MemoryBudgetFailsBeforeAStaleCatalogTouchesTruncatedPayload()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        var tensorPath = Path.Combine(fixturePath, TinyFixtureFiles.Tensors);
        using (var stream = File.Open(tensorPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(stream.Length - sizeof(float));
        }

        var budgetException = Assert.Throws<ModelMemoryBudgetExceededException>(() =>
            ManagedGlmModel.Load(probe, 16, availableMemoryBytes: 0));
        Assert.Equal(0L, budgetException.Plan.AvailableBytes);
        Assert.True(budgetException.Plan.RequiredBytes > 0);

        Assert.Throws<EndOfStreamException>(() =>
            ManagedGlmModel.Load(probe, 16, availableMemoryBytes: long.MaxValue));
    }

    [Fact]
    public void ResidentShapeMismatchFailsBeforePayloadLoading()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var configPath = Path.Combine(fixturePath, TinyFixtureFiles.Configuration);
        var configuration = File.ReadAllText(configPath);
        File.WriteAllText(
            configPath,
            configuration.Replace(
                "\"hidden_size\": 4",
                "\"hidden_size\": 5",
                StringComparison.Ordinal));
        var probe = ReadProbe(fixturePath);

        var exception = Assert.Throws<InvalidDataException>(() =>
            ManagedGlmModel.Load(probe, 16, availableMemoryBytes: long.MaxValue));

        Assert.Contains("model.embed_tokens.weight", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected [12, 5]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationAndDisposeReleaseResidentBuffersAndShardHandles()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ManagedGlmModel.Load(probe, 16, long.MaxValue, cancellation.Token));

        var model = ManagedGlmModel.Load(probe, 16, long.MaxValue);
        model.Dispose();
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ReadFirstEmbeddingValue(model));

        var tensorPath = Path.Combine(fixturePath, TinyFixtureFiles.Tensors);
        using var exclusive = File.Open(tensorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    private static ModelProbe ReadProbe(string fixturePath)
    {
        var manifestPath = Path.Combine(fixturePath, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "m6-fixture",
            "Managed GLM M6 fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);
        return ModelDirectoryProbe.Read(descriptor, ManagedGlmProvider.ProviderId);
    }

    private static float ReadFirstRoutedExpertValue(ManagedGlmModel model)
        => model.GetResidentWeight("model.layers.0.mlp.experts.0.gate_proj.weight")[0];

    private static float ReadFirstEmbeddingValue(ManagedGlmModel model)
        => model.GetResidentWeight("model.embed_tokens.weight")[0];

    private static void AssertClose(
        IReadOnlyList<float> expected,
        IReadOnlyList<float> actual,
        TinyOracleTolerance tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var error = Math.Abs(expected[index] - actual[index]);
            var limit = tolerance.Absolute + (tolerance.Relative * Math.Abs(expected[index]));
            Assert.True(
                error <= limit,
                $"Value {index} differs: expected {expected[index]}, actual {actual[index]}, limit {limit}.");
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
