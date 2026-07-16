using Tomur.Inference;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeBoundaryTests
{
    [Theory]
    [InlineData("\"model_type\": \"olmoe\"", "\"model_type\": \"other\"")]
    [InlineData("\"num_attention_heads\": 2", "\"num_attention_heads\": 3")]
    [InlineData("\"num_experts_per_tok\": 1", "\"num_experts_per_tok\": 3")]
    public void InvalidConfigurationFailsDuringMetadataProbe(string oldValue, string newValue)
    {
        using var fixture = new OlmoeFixture();
        var configPath = Path.Combine(fixture.Path, "config.json");
        File.WriteAllText(
            configPath,
            File.ReadAllText(configPath).Replace(oldValue, newValue, StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => fixture.ReadProbe());
    }

    [Fact]
    public void MemoryBudgetFailsBeforeStaleCatalogReadsTruncatedPayload()
    {
        using var fixture = new OlmoeFixture();
        var probe = fixture.ReadProbe();
        var resident = probe.Tensors.GetRequired("lm_head.weight");
        using (var stream = File.Open(fixture.TensorPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(checked(resident.Offset + resident.PhysicalLength - 1));
        }

        var budgetException = Assert.Throws<OlmoeMemoryBudgetExceededException>(() =>
            ManagedOlmoeModel.Load(probe, contextSize: 8, availableMemoryBytes: 0));
        Assert.Equal(0L, budgetException.Plan.AvailableBytes);
        Assert.True(budgetException.Plan.RequiredBytes > 0);

        Assert.Throws<EndOfStreamException>(() =>
            ManagedOlmoeModel.Load(probe, contextSize: 8, availableMemoryBytes: long.MaxValue));
    }

    [Fact]
    public void ResidentShapeMismatchFailsBeforePayloadIsOpened()
    {
        using var fixture = new OlmoeFixture();
        var configPath = Path.Combine(fixture.Path, "config.json");
        File.WriteAllText(
            configPath,
            File.ReadAllText(configPath).Replace(
                "\"hidden_size\": 4",
                "\"hidden_size\": 8",
                StringComparison.Ordinal));
        var probe = fixture.ReadProbe();

        var exception = Assert.Throws<InvalidDataException>(() =>
            ManagedOlmoeModel.Load(probe, contextSize: 8, availableMemoryBytes: long.MaxValue));

        Assert.Contains("model.embed_tokens.weight", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected [8, 8]", exception.Message, StringComparison.Ordinal);
        using var exclusive = File.Open(fixture.TensorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void UnsupportedQuantizationAndMissingAssetsReturnStructuredDiagnostics()
    {
        using (var fixture = new OlmoeFixture())
        {
            var manifestPath = Path.Combine(fixture.Path, "model.tomur.json");
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace(
                    "rowwise-qs",
                    "packed-offset",
                    StringComparison.Ordinal));
            var exception = Assert.Throws<InferenceException>(() =>
                new ManagedOlmoeProvider().CreateSession(
                    fixture.Descriptor,
                    new ModelSessionOptions(8)));
            Assert.Equal("managed_quantization_unsupported", exception.Code);
        }

        using (var fixture = new OlmoeFixture())
        {
            File.Delete(Path.Combine(fixture.Path, "tokenizer.json"));
            var exception = Assert.Throws<InferenceException>(() =>
                new ManagedOlmoeProvider().CreateSession(
                    fixture.Descriptor,
                    new ModelSessionOptions(8)));
            Assert.Equal("managed_model_assets_incomplete", exception.Code);
        }
    }

    [Fact]
    public void InvalidOlmoeModelDoesNotAffectManagedGlmProvider()
    {
        using var fixture = new OlmoeFixture();
        File.Delete(Path.Combine(fixture.Path, "tokenizer.json"));
        Assert.Throws<InferenceException>(() =>
            new ManagedOlmoeProvider().CreateSession(
                fixture.Descriptor,
                new ModelSessionOptions(8)));

        var glmDirectory = Path.Combine(fixture.Path, "glm");
        TinyFixtureBundle.Generate(glmDirectory);
        var manifestPath = Path.Combine(glmDirectory, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "tiny-glm",
            "Tiny GLM fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            GlmModelConfiguration.DsaModelType,
            "f32",
            ["completion", "chat"]);
        var provider = new ManagedGlmProvider();

        Assert.True(provider.CanHandle(descriptor));
        using var session = provider.CreateSession(descriptor, new ModelSessionOptions(16));
        Assert.Equal("managed-glm-generation", session.GetSnapshot().Mode);
    }

    [Fact]
    public async Task ContextCancellationAndInvalidTokenFailBeforeExpertReads()
    {
        using var fixture = new OlmoeFixture();
        using var model = fixture.LoadModel(contextSize: 8);
        using var cache = CreateMinimumCache(model);
        var generator = new OlmoeTextGenerator(model, cache);
        var overflow = CreateOptions(maxOutputTokens: 7);

        var contextException = await Assert.ThrowsAsync<ContextLengthExceededException>(async () =>
            await generator.GenerateAsync(
                new OlmoePrompt([4, 5], []),
                overflow,
                CancellationToken.None));
        Assert.Equal(8, contextException.ContextLimit);
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await generator.GenerateAsync(
                new OlmoePrompt([4], []),
                CreateOptions(maxOutputTokens: 1),
                cancellation.Token));
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);

        using var forward = new OlmoeForwardContext(model, cache, contextLimit: 8);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await forward.ForwardAsync(new[] { -1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await forward.ForwardAsync(new[] { 4 }, CancellationToken.None));
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);
    }

    [Fact]
    public void CancelledLoadAndDisposeReleaseBuffersAndShardHandles()
    {
        using var fixture = new OlmoeFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            fixture.LoadModel(
                contextSize: 8,
                availableMemoryBytes: long.MaxValue,
                cancellationToken: cancellation.Token));

        var model = fixture.LoadModel();
        var cache = CreateMinimumCache(model);
        var forward = new OlmoeForwardContext(model, cache, contextLimit: 8);
        forward.Dispose();
        forward.Dispose();
        cache.Dispose();
        model.Dispose();
        model.Dispose();

        Assert.Throws<ObjectDisposedException>(() => model.GatherEmbedding(0, new float[4]));
        using var exclusive = File.Open(fixture.TensorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    private static CompletionOptions CreateOptions(int maxOutputTokens)
        => CompletionOptions.Default with
        {
            ContextSize = 8,
            MaxOutputTokens = maxOutputTokens,
            Temperature = 0,
            TopK = 8,
            TopP = 1,
            PenaltyLastTokens = 0,
            RepeatPenalty = 1,
            FrequencyPenalty = 0,
            PresencePenalty = 0,
            StopSequences = []
        };

    private static ExpertCache CreateMinimumCache(ManagedOlmoeModel model)
    {
        var minimum = checked(
            model.ExpertLayout.SlotBudgetedBytes *
            model.ExpertLayout.MoeLayerCount *
            model.Configuration.ExpertsPerToken);
        return model.CreateExpertCache(new ExpertCacheOptions(
            minimum,
            WorkerCount: 1,
            QueueCapacity: model.Configuration.ExpertsPerToken));
    }
}
