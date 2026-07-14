using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Native;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M9.Tests;

public sealed class ForwardGenerationTests
{
    [Fact]
    public async Task IncrementalForwardMatchesEveryTeacherForcingLogitVector()
    {
        using var fixture = new GenerationFixture();
        var oracle = fixture.Oracle;
        using var model = fixture.LoadModel();
        using var cache = CreateMinimumCache(model);
        using var forward = new ManagedForwardContext(
            model,
            cache,
            oracle.ModelConfiguration.ContextSize);

        foreach (var expected in oracle.TeacherForcing)
        {
            var logits = await forward.ForwardAsync(
                new[] { expected.InputTokenId },
                CancellationToken.None);

            AssertClose(expected.Logits, logits.ToArray(), oracle.Tolerances);
            Assert.Equal(expected.Position + 1, forward.Position);
        }
    }

    [Fact]
    public async Task MultiTokenPrefillAndGreedyDecodeMatchOracle()
    {
        using var fixture = new GenerationFixture();
        var oracle = fixture.Oracle;
        using var model = fixture.LoadModel();
        var promptTokenIds = oracle.GreedyDecode.PromptTokenIds.ToArray();

        using (var prefillCache = CreateMinimumCache(model))
        using (var forward = new ManagedForwardContext(
                   model,
                   prefillCache,
                   oracle.ModelConfiguration.ContextSize))
        {
            var logits = await forward.ForwardAsync(promptTokenIds, CancellationToken.None);
            AssertClose(oracle.TeacherForcing[^1].Logits, logits.ToArray(), oracle.Tolerances);
            Assert.Equal(promptTokenIds.Length, forward.Position);
        }

        using var generationCache = CreateMinimumCache(model);
        var generator = new ManagedTextGenerator(model, generationCache);
        var options = CreateOptions(oracle.GreedyDecode.MaxNewTokens, temperature: 0, seed: 7);
        var result = await generator.GenerateAsync(
            new GlmPrompt(promptTokenIds, []),
            options,
            CancellationToken.None);

        Assert.Equal(oracle.GreedyDecode.TokenIds, result.GeneratedTokenIds);
        Assert.Equal("length", result.StopReason);
        Assert.Equal(promptTokenIds.Length, result.PromptTokenCount);
        Assert.Equal(7, result.Seed);
    }

    [Fact]
    public void ProviderSessionReturnsDecodedGreedyTextAndTracksUsage()
    {
        using var fixture = new GenerationFixture();
        var oracle = fixture.Oracle;
        var provider = new ManagedGlmProvider();
        using var session = provider.CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(oracle.ModelConfiguration.ContextSize));
        var emitted = new StringBuilder();
        var options = CreateOptions(oracle.GreedyDecode.MaxNewTokens, temperature: 0, seed: 11);

        var result = session.Generate(
            "hello Tomur",
            options,
            CancellationToken.None,
            value => emitted.Append(value));

        var tokenizer = ManagedTokenizer.Read(
            Path.Combine(fixture.Path, TinyFixtureFiles.Tokenizer));
        var stopTokenIds = new GlmPromptTemplate(tokenizer).ResolveStopTokenIds();
        var expectedTokenIds = oracle.GreedyDecode.TokenIds
            .TakeWhile(tokenId => !stopTokenIds.Contains(tokenId))
            .ToArray();
        var expectedText = tokenizer.Decode(expectedTokenIds);
        Assert.Equal(expectedText, result.Text);
        Assert.Equal(result.Text, emitted.ToString());
        Assert.Equal(oracle.GreedyDecode.PromptTokenIds.Count, result.Usage.PromptTokens);
        Assert.Equal(expectedTokenIds.Length, result.Usage.CompletionTokens);
        Assert.Equal(result.Usage.PromptTokens + result.Usage.CompletionTokens, result.Usage.TotalTokens);

        var snapshot = session.GetSnapshot();
        Assert.Equal("managed-glm-generation", snapshot.Mode);
        Assert.Equal(1L, snapshot.RequestCount);
        Assert.Equal((long)result.Usage.PromptTokens, snapshot.PromptTokens);
        Assert.Equal((long)result.Usage.CompletionTokens, snapshot.CompletionTokens);
        Assert.Contains(snapshot.Diagnostics, value => value.StartsWith("expert cache hit/miss/eviction:", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalInferenceChatUsesManagedGlmRoleTemplate()
    {
        using var fixture = new GenerationFixture();
        fixture.EnableChatRoleTokens();
        var providerDirectory = Path.GetDirectoryName(typeof(ManagedGlmProvider).Assembly.Location)!;
        var previousProviderPath = Environment.GetEnvironmentVariable(
            ModelProviderRegistry.ProviderPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            providerDirectory);

        try
        {
            using var registry = ModelProviderRegistry.CreateDefault();
            var paths = new DataPaths(new PathOptions { DataDirectory = fixture.Path });
            var configurationStore = new ConfigurationStore(paths);
            var nativeProbe = new NativeBundleProbe(paths);
            var resolver = new NativeLibraryResolver(nativeProbe);
            var importResolver = new LlamaImportResolver(resolver);
            var backendInitializer = new LlamaBackendInitializer(
                importResolver,
                resolver,
                configurationStore);
            var accelerationService = new HardwareAccelerationService(
                backendInitializer,
                nativeProbe,
                configurationStore);
            using var sessionManager = new SessionManager(
                backendInitializer,
                accelerationService,
                registry,
                NullLogger<SessionManager>.Instance);
            var inference = new LocalInferenceService(sessionManager);
            ChatTurn[] messages =
            [
                new("system", "hello"),
                new("user", "Tomur")
            ];
            var options = CreateOptions(1, temperature: 0, seed: 5);

            var result = inference.Chat(
                fixture.Descriptor,
                messages,
                options,
                CancellationToken.None);

            var tokenizer = ManagedTokenizer.Read(
                Path.Combine(fixture.Path, TinyFixtureFiles.Tokenizer));
            var expectedPrompt = new GlmPromptTemplate(tokenizer).BuildChat(messages);
            Assert.Equal(expectedPrompt.TokenIds.Count, result.Usage.PromptTokens);
            Assert.Contains(result.Diagnostics, value => value == "provider: managed-glm");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ModelProviderRegistry.ProviderPathEnvironmentVariable,
                previousProviderPath);
        }
    }

    [Fact]
    public void FixedSeedSamplingIsRepeatableAndPenaltiesAffectGreedySelection()
    {
        var samplingOptions = CreateOptions(4, temperature: 0.8f, seed: 123) with
        {
            TopK = 3,
            TopP = 0.75f
        };
        float[] logits = [0.1f, 1.1f, 0.9f, 0.7f];
        var first = new int[8];
        var second = new int[8];
        using (var sampler = new TokenSampler(logits.Length, samplingOptions))
        {
            for (var index = 0; index < first.Length; index++)
            {
                first[index] = sampler.Sample(logits, []);
            }
        }

        using (var sampler = new TokenSampler(logits.Length, samplingOptions))
        {
            for (var index = 0; index < second.Length; index++)
            {
                second[index] = sampler.Sample(logits, []);
            }
        }

        Assert.Equal(first, second);
        Assert.All(first, tokenId => Assert.Contains(tokenId, new[] { 1, 2, 3 }));

        var penaltyOptions = CreateOptions(1, temperature: 0, seed: 1) with
        {
            PenaltyLastTokens = -1,
            RepeatPenalty = 2.0f,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.1f
        };
        using var penaltySampler = new TokenSampler(2, penaltyOptions);
        Assert.Equal(1, penaltySampler.Sample([1.0f, 0.9f], [0, 0]));
    }

    [Fact]
    public async Task ContextAndCancellationFailBeforeExpertReads()
    {
        using var fixture = new GenerationFixture();
        var oracle = fixture.Oracle;
        using var model = fixture.LoadModel();
        using var cache = CreateMinimumCache(model);
        var generator = new ManagedTextGenerator(model, cache);
        var prompt = new GlmPrompt(oracle.GreedyDecode.PromptTokenIds, []);
        var overflow = CreateOptions(14, temperature: 0, seed: 1);

        var contextException = await Assert.ThrowsAsync<ContextLengthExceededException>(async () =>
            await generator.GenerateAsync(prompt, overflow, CancellationToken.None));
        Assert.Equal(oracle.ModelConfiguration.ContextSize, contextException.ContextLimit);
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await generator.GenerateAsync(
                prompt,
                CreateOptions(4, temperature: 0, seed: 1),
                cancellation.Token));
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);

        using var forward = new ManagedForwardContext(
            model,
            cache,
            oracle.ModelConfiguration.ContextSize);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await forward.ForwardAsync(new[] { -1 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await forward.ForwardAsync(new[] { 1 }, CancellationToken.None));
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);
    }

    private static CompletionOptions CreateOptions(int maxOutputTokens, float temperature, int seed)
        => CompletionOptions.Default with
        {
            MaxOutputTokens = maxOutputTokens,
            ContextSize = 16,
            Temperature = temperature,
            TopP = 1.0f,
            TopK = 12,
            PenaltyLastTokens = 0,
            RepeatPenalty = 1.0f,
            FrequencyPenalty = 0,
            PresencePenalty = 0,
            Seed = seed,
            StopSequences = []
        };

    private static ExpertCache CreateMinimumCache(ManagedGlmModel model)
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

internal sealed class GenerationFixture : IDisposable
{
    public GenerationFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tomur-m9-{Guid.NewGuid():N}");
        TinyFixtureBundle.Generate(Path);
        Oracle = TinyFixtureBundle.ReadOracle(Path);
        var manifestPath = System.IO.Path.Combine(Path, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        Descriptor = new LocalModelDescriptor(
            "m9-fixture",
            "Managed GLM M9 fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);
    }

    public string Path { get; }

    public TinyOracle Oracle { get; }

    public LocalModelDescriptor Descriptor { get; }

    public ManagedGlmModel LoadModel()
        => ManagedGlmModel.Load(
            ModelDirectoryProbe.Read(Descriptor, ManagedGlmProvider.ProviderId),
            Oracle.ModelConfiguration.ContextSize,
            long.MaxValue);

    public void EnableChatRoleTokens()
    {
        var tokenizerPath = System.IO.Path.Combine(Path, TinyFixtureFiles.Tokenizer);
        var json = File.ReadAllText(tokenizerPath)
            .Replace("\"world\": 5", "\"<|system|>\": 5", StringComparison.Ordinal)
            .Replace("\"本地\": 7", "\"<|user|>\": 7", StringComparison.Ordinal)
            .Replace("\"AI\": 8", "\"<|assistant|>\": 8", StringComparison.Ordinal)
            .Replace("\"!\": 9", "\"<|observation|>\": 9", StringComparison.Ordinal);
        File.WriteAllText(tokenizerPath, json);
    }

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
