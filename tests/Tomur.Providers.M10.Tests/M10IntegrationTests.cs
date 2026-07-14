using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Api;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Native;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M10.Tests;

public sealed class M10IntegrationTests
{
    [Fact]
    public async Task OllamaChatStreamingWritesIncrementalAndTerminalFrames()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ApiRouteExtensions.WriteOllamaChatStreamAsync(
            context,
            "ready",
            emit =>
            {
                emit("first");
                emit(" second");
                return new CompletionResult(
                    "first second",
                    new TokenUsage(3, 2, 5),
                    TimeSpan.FromMilliseconds(25),
                    []);
            },
            exception => new RuntimeDiagnostic(
                "error",
                exception.Code,
                exception.Message,
                "ready",
                exception.Actions));

        var lines = Encoding.UTF8.GetString(body.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        using var terminal = JsonDocument.Parse(lines[2]);
        Assert.False(first.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("first", first.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.False(second.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal(" second", second.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.True(terminal.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal(3, terminal.RootElement.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(2, terminal.RootElement.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public void ReadinessProbeReportsValidatedAssetsAndMemoryPlan()
    {
        using var fixture = new TemporaryDataDirectory();
        var modelDirectory = Path.Combine(fixture.Paths.ModelsDirectory, "ready");
        TinyFixtureBundle.Generate(modelDirectory);
        var descriptor = CreateDescriptor(modelDirectory, "ready");
        var provider = new ManagedGlmProvider();

        var result = ((IModelReadinessProvider)provider).InspectModel(
            descriptor,
            new ModelSessionOptions(16));

        Assert.Equal(ManagedGlmProvider.ProviderId, result.ProviderId);
        Assert.Equal(GlmModelConfiguration.DsaModelType, result.Architecture);
        Assert.True(result.TensorFileCount > 0);
        Assert.True(result.TensorCount > 0);
        Assert.True(result.ResidentBytes > 0);
        Assert.True(result.KvBytes > 0);
        Assert.True(result.ScratchBytes > 0);
        Assert.True(result.ExpertCacheBytes > 0);
        Assert.Equal(
            result.ResidentBytes + result.KvBytes + result.ScratchBytes + result.ExpertCacheBytes,
            result.RequiredBytes);
    }

    [Fact]
    public void CompatibilityCatalogOmitsManagedModelWithIncompleteAssets()
    {
        using var fixture = new TemporaryDataDirectory();
        var readyDirectory = Path.Combine(fixture.Paths.ModelsDirectory, "ready");
        TinyFixtureBundle.Generate(readyDirectory);
        var incompleteDirectory = Path.Combine(fixture.Paths.ModelsDirectory, "incomplete");
        Directory.CreateDirectory(incompleteDirectory);
        File.WriteAllText(
            Path.Combine(incompleteDirectory, ModelProviderManifest.FileName),
            """
            {
              "schema_version": 1,
              "provider": "managed-glm",
              "architecture": "glm_moe_dsa",
              "display_name": "Incomplete managed model",
              "config": "config.json",
              "tokenizer": "tokenizer.json",
              "tensor_pattern": "*.safetensors",
              "quantization": "f32",
              "capabilities": ["completion", "chat"]
            }
            """);
        using var registry = CreateRegistry(new ManagedGlmProvider());
        var catalog = new LocalModelCatalog(fixture.Paths, registry);

        var candidates = catalog.ListModelCandidates();
        var visible = catalog.ListModels();
        var incomplete = candidates.Single(model => model.Id == "incomplete");
        var readiness = registry.InspectModel(incomplete);

        Assert.Equal(2, candidates.Count);
        Assert.Single(visible);
        Assert.Equal("ready", visible[0].Id);
        Assert.True(readiness.ProviderDiscovered);
        Assert.False(readiness.AssetsComplete);
        Assert.Contains(
            readiness.Diagnostics,
            diagnostic => diagnostic.Code == "managed_model_assets_incomplete");
    }

    [Fact]
    public void SuccessfulSessionMarksModelForwardVerified()
    {
        using var fixture = new TemporaryDataDirectory();
        var modelDirectory = Path.Combine(fixture.Paths.ModelsDirectory, "ready");
        TinyFixtureBundle.Generate(modelDirectory);
        var descriptor = CreateDescriptor(modelDirectory, "ready");
        using var registry = CreateRegistry(new ManagedGlmProvider());
        var session = new SessionSnapshot(
            true,
            descriptor.Id,
            descriptor.AbsolutePath,
            "managed-glm-generation",
            DateTimeOffset.UtcNow,
            1,
            4,
            1,
            [])
        {
            ProviderId = ManagedGlmProvider.ProviderId,
            ContextSize = 16
        };

        var readiness = registry.InspectModel(descriptor, session, contextSize: 16);

        Assert.Equal("loaded", readiness.Status);
        Assert.True(readiness.SessionLoaded);
        Assert.True(readiness.ForwardVerified);
    }

    [Fact]
    public async Task UnloadCancelsActiveGenerationBeforeDisposingSession()
    {
        using var fixture = new TemporaryDataDirectory();
        var provider = new BlockingProvider();
        using var registry = CreateRegistry(provider);
        var configurationStore = new ConfigurationStore(fixture.Paths);
        var nativeProbe = new NativeBundleProbe(fixture.Paths);
        var resolver = new NativeLibraryResolver(nativeProbe);
        var importResolver = new LlamaImportResolver(resolver);
        var backendInitializer = new LlamaBackendInitializer(
            importResolver,
            resolver,
            configurationStore);
        var acceleration = new HardwareAccelerationService(
            backendInitializer,
            nativeProbe,
            configurationStore);
        using var sessions = new SessionManager(
            backendInitializer,
            acceleration,
            registry,
            NullLogger<SessionManager>.Instance);
        var inference = new LocalInferenceService(sessions);
        var model = new LocalModelDescriptor(
            "blocking",
            "Blocking test model",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            Path.Combine(fixture.Paths.ModelsDirectory, ModelProviderManifest.FileName),
            0,
            DateTime.UtcNow,
            "managed-model",
            "blocking",
            "f32",
            ["completion", "chat"]);

        var generation = Task.Run(() => Record.Exception(() => inference.Complete(
            model,
            "wait",
            CompletionOptions.Default,
            CancellationToken.None)));
        Assert.True(provider.Session.Started.Wait(TimeSpan.FromSeconds(5)));

        sessions.Unload();
        var exception = await generation.WaitAsync(TimeSpan.FromSeconds(5));

        var inferenceException = Assert.IsType<InferenceException>(exception);
        Assert.Equal("session_unloaded", inferenceException.Code);
        Assert.True(provider.Session.Disposed);
        Assert.False(sessions.GetSnapshot().Loaded);
    }

    private static ModelProviderRegistry CreateRegistry(ITextGenerationProvider provider)
        => new(
            [provider],
            dynamicLoadingSupported: true,
            searchDirectories: [],
            loadedProviders:
            [
                new ModelProviderInfo(
                    provider.Id,
                    provider.GetType().Assembly.GetName().Name ?? provider.Id,
                    provider.GetType().Assembly.GetName().Version?.ToString(),
                    provider.GetType().Assembly.Location)
            ],
            diagnostics: []);

    private static LocalModelDescriptor CreateDescriptor(string directory, string id)
    {
        var manifestPath = Path.Combine(directory, ModelProviderManifest.FileName);
        return new LocalModelDescriptor(
            id,
            id,
            ModelProviderManifest.FileName,
            $"{id}/{ModelProviderManifest.FileName}",
            manifestPath,
            Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length),
            File.GetLastWriteTimeUtc(manifestPath),
            "managed-model",
            GlmModelConfiguration.DsaModelType,
            "f32",
            ["completion", "chat"]);
    }

    private sealed class TemporaryDataDirectory : IDisposable
    {
        public TemporaryDataDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"tomur-m10-{Guid.NewGuid():N}");
            Paths = new DataPaths(new PathOptions { DataDirectory = Root });
            Directory.CreateDirectory(Paths.ModelsDirectory);
        }

        public string Root { get; }

        public DataPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class BlockingProvider : ITextGenerationProvider
    {
        public string Id => "blocking-provider";

        public BlockingSession Session { get; } = new();

        public bool CanHandle(LocalModelDescriptor model) => model.Id == "blocking";

        public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
            => Session;
    }

    private sealed class BlockingSession : ITextGenerationSession
    {
        public string ProviderId => "blocking-provider";

        public ManualResetEventSlim Started { get; } = new();

        public bool Disposed { get; private set; }

        public CompletionResult Generate(
            string prompt,
            CompletionOptions options,
            CancellationToken cancellationToken,
            Action<string>? onToken = null)
        {
            Started.Set();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was expected.");
        }

        public SessionSnapshot GetSnapshot()
            => new(
                true,
                "blocking",
                null,
                "blocking",
                DateTimeOffset.UtcNow,
                0,
                0,
                0,
                []);

        public void Dispose()
        {
            Disposed = true;
            Started.Dispose();
        }
    }
}
