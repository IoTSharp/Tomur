using System.Diagnostics;
using System.Text.Json;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Glm;

public sealed class ManagedGlmProvider : IModelFixtureProvider, IModelReadinessProvider
{
    public const string ProviderId = "managed-glm";

    public string Id => ProviderId;

    public bool CanHandle(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ModelProviderManifestReader.TryRead(model.AbsolutePath, out var manifest, out var error) || manifest is null)
        {
            throw new InvalidDataException(error ?? "Managed model provider manifest is invalid.");
        }

        return string.Equals(manifest.Provider, ProviderId, StringComparison.OrdinalIgnoreCase);
    }

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var probe = ModelDirectoryProbe.Read(model, ProviderId);
            return new ManagedGlmSession(model, options, probe);
        }
        catch (ModelMemoryBudgetExceededException exception)
        {
            throw new InferenceException(
                "managed_model_memory_budget_exceeded",
                exception.Message,
                [
                    "Reduce the requested context size.",
                    "Use a smaller model or a system with more available memory.",
                    "Unload other memory-heavy applications before retrying."
                ],
                exception);
        }
        catch (ExpertCacheBudgetExceededException exception)
        {
            throw new InferenceException(
                "managed_model_memory_budget_exceeded",
                exception.Message,
                [
                    "Use a smaller model or a system with more available memory.",
                    "Unload other memory-heavy applications before retrying.",
                    "The managed provider requires at least one complete top-k expert working set for every MoE layer."
                ],
                exception);
        }
        catch (OutOfMemoryException exception)
        {
            throw new InferenceException(
                "managed_model_out_of_memory",
                $"The managed GLM resident model could not be allocated after memory preflight: {exception.Message}",
                [
                    "Reduce the requested context size.",
                    "Use a smaller model or a system with more available memory.",
                    "Unload the active model and other memory-heavy applications before retrying."
                ],
                exception);
        }
        catch (ContextLengthExceededException exception)
        {
            throw new InferenceException(
                "managed_context_length_exceeded",
                exception.Message,
                [
                    $"Request no more than {exception.ContextLimit} total tokens for this model.",
                    "Reduce the requested context size before creating the managed model session."
                ],
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw CreateModelException(exception, "prepared");
        }
    }

    public ModelPreparationResult InspectModel(LocalModelDescriptor model, ModelSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var probe = ModelDirectoryProbe.Read(model, ProviderId);
            var contextSize = Math.Min(options.ContextSize, probe.Configuration.MaxPositionEmbeddings);
            var residentWeights = ResidentWeightLayout.Create(
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
                residentWeights,
                contextSize,
                ModelMemoryPlan.GetAvailableMemoryBytes());
            var expertCacheBytes = expertLayout.MoeLayerCount == 0
                ? 0
                : ExpertCacheOptions.CreateAutomatic(expertLayout, memoryPlan).BudgetBytes;

            return new ModelPreparationResult(
                ProviderId,
                probe.Configuration.ModelType,
                probe.Manifest.Quantization,
                probe.Manifest.QuantizationLayout,
                contextSize,
                probe.TensorFileCount,
                probe.Tensors.Count,
                memoryPlan.ResidentBytes,
                memoryPlan.KvBytes,
                memoryPlan.ScratchBytes,
                expertCacheBytes,
                checked(memoryPlan.RequiredBytes + expertCacheBytes),
                memoryPlan.AvailableBytes,
                [
                    $"manifest: {model.AbsolutePath}",
                    $"config: {probe.Manifest.ConfigFile}",
                    $"tokenizer: {probe.Manifest.TokenizerFile}",
                    $"tensor shards: {probe.TensorFileCount}",
                    $"tokenizer vocabulary: {probe.Tokenizer.VocabularySize}",
                    $"layers: {probe.Configuration.LayerCount}",
                    $"routed experts: {probe.Configuration.RoutedExpertCount}",
                    $"expert storage format: {expertLayout.Format}",
                    $"DSA configured/tensors: {probe.AdvancedFeatures.DsaConfigured}/{probe.AdvancedFeatures.DsaTensorCount}",
                    $"MTP configured/tensors: {probe.AdvancedFeatures.MtpConfigured}/{probe.AdvancedFeatures.MtpTensorCount}"
                ]);
        }
        catch (InferenceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw CreateModelException(exception, "inspected");
        }
    }

    public ModelFixtureResult GenerateFixture(string outputDirectory)
        => TinyFixtureBundle.Generate(outputDirectory).ToResult(ProviderId);

    public ModelFixtureResult VerifyFixture(string fixtureDirectory)
        => TinyFixtureBundle.Verify(fixtureDirectory).ToResult(ProviderId);

    private static InferenceException CreateModelException(Exception exception, string operation)
    {
        var assetsIncomplete = exception is FileNotFoundException or DirectoryNotFoundException ||
            exception.Message.Contains("is missing", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("No tensor files", StringComparison.OrdinalIgnoreCase);
        var quantizationUnsupported = exception.Message.Contains("does not support quantization", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("quantization layout", StringComparison.OrdinalIgnoreCase);
        var code = assetsIncomplete
            ? "managed_model_assets_incomplete"
            : quantizationUnsupported
                ? "managed_quantization_unsupported"
                : "managed_model_invalid";
        return new InferenceException(
            code,
            $"The managed GLM model could not be {operation}: {exception.Message}",
            [
                $"Verify {ModelProviderManifest.FileName}, config.json, tokenizer.json and every safetensors shard.",
                "Complete model download and checksum verification before exposing the model through compatibility APIs."
            ],
            exception);
    }
}

internal sealed class ManagedGlmSession : IChatGenerationSession
{
    private readonly object gate = new();
    private readonly LocalModelDescriptor model;
    private readonly ModelSessionOptions options;
    private readonly ManagedGlmModel loadedModel;
    private readonly ExpertCache? expertCache;
    private readonly ManagedTextGenerator generator;
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private long requestCount;
    private long promptTokens;
    private long completionTokens;
    private bool disposed;

    public ManagedGlmSession(
        LocalModelDescriptor model,
        ModelSessionOptions options,
        ModelProbe probe)
    {
        this.model = model;
        this.options = options;
        var effectiveContextSize = Math.Min(options.ContextSize, probe.Configuration.MaxPositionEmbeddings);
        var candidate = ManagedGlmModel.Load(probe, effectiveContextSize);
        try
        {
            if (candidate.ExpertLayout.MoeLayerCount > 0)
            {
                var cacheOptions = ExpertCacheOptions.CreateAutomatic(
                    candidate.ExpertLayout,
                    candidate.MemoryPlan,
                    WorkerCount: Math.Min(2, candidate.Configuration.ExpertsPerToken),
                    QueueCapacity: Math.Max(8, candidate.Configuration.ExpertsPerToken));
                expertCache = candidate.CreateExpertCache(cacheOptions);
            }

            loadedModel = candidate;
            generator = new ManagedTextGenerator(candidate, expertCache);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public string ProviderId => ManagedGlmProvider.ProviderId;

    public CompletionResult Generate(
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InferenceException(
                "empty_prompt",
                "The prompt is empty after normalization.",
                ["Provide at least one non-empty user message or prompt string."]);
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var preparedPrompt = new GlmPromptTemplate(
                    loadedModel.Tokenizer,
                    loadedModel.Configuration.ModelType)
                .BuildCompletion(prompt);
            return GeneratePrepared(preparedPrompt, options, cancellationToken, onToken);
        }
    }

    public CompletionResult GenerateChat(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        if (!messages.Any(static message => !string.IsNullOrWhiteSpace(message.Content)))
        {
            throw new InferenceException(
                "empty_prompt",
                "The chat request does not contain a non-empty message.",
                ["Provide at least one non-empty user message."]);
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var preparedPrompt = new GlmPromptTemplate(
                    loadedModel.Tokenizer,
                    loadedModel.Configuration.ModelType)
                .BuildChat(messages);
            return GeneratePrepared(preparedPrompt, options, cancellationToken, onToken);
        }
    }

    private CompletionResult GeneratePrepared(
        GlmPrompt preparedPrompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = generator.GenerateAsync(
                    preparedPrompt,
                    options,
                    cancellationToken,
                    onToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            stopwatch.Stop();

            var generatedCount = result.GeneratedTokenIds.Count;
            Interlocked.Increment(ref requestCount);
            Interlocked.Add(ref promptTokens, result.PromptTokenCount);
            Interlocked.Add(ref completionTokens, generatedCount);
            var diagnostics = BuildDiagnostics(
                $"prompt tokens: {result.PromptTokenCount}",
                $"completion tokens: {generatedCount}",
                $"stop reason: {result.StopReason}",
                $"sampling seed: {result.Seed}",
                $"router lookahead prefetched experts: {result.RouterLookaheadPrefetches}",
                result.Timing.ToString());
            return new CompletionResult(
                result.Text,
                new TokenUsage(
                    result.PromptTokenCount,
                    generatedCount,
                    checked(result.PromptTokenCount + generatedCount)),
                stopwatch.Elapsed,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InferenceException)
        {
            throw;
        }
        catch (ContextLengthExceededException exception)
        {
            throw new InferenceException(
                "managed_context_length_exceeded",
                exception.Message,
                [
                    $"Request no more than {exception.ContextLimit} total prompt and output tokens.",
                    "Reduce max_tokens, shorten the prompt, or create the session with a larger supported context."
                ],
                exception);
        }
        catch (Exception exception)
        {
            throw new InferenceException(
                "managed_inference_failed",
                $"Managed GLM forward execution failed: {exception.Message}",
                [
                    "Verify the model manifest, tokenizer and tensor assets.",
                    "Retry with a shorter context and inspect tomur doctor diagnostics.",
                    "Use the existing llama.cpp provider for models that are not explicitly packaged for the managed provider."
                ],
                exception);
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        var cache = expertCache?.GetSnapshot();
        return new SessionSnapshot(
            Loaded: true,
            ModelId: model.Id,
            ModelPath: model.AbsolutePath,
            Mode: "managed-glm-generation",
            LoadedAt: createdAt,
            RequestCount: Interlocked.Read(ref requestCount),
            PromptTokens: Interlocked.Read(ref promptTokens),
            CompletionTokens: Interlocked.Read(ref completionTokens),
            Diagnostics: BuildDiagnostics($"forward execution: {OptimizedKernels.Description}"))
        {
            ProviderId = ProviderId,
            Architecture = loadedModel.Configuration.ModelType,
            Quantization = loadedModel.Manifest.Quantization,
            ExecutionBackend = "managed-cpu",
            ExecutionDetail = OptimizedKernels.Description,
            ContextSize = loadedModel.MemoryPlan.ContextSize,
            ResidentBytes = loadedModel.ActualResidentBytes,
            KvBytes = loadedModel.MemoryPlan.KvBytes,
            ScratchBytes = loadedModel.MemoryPlan.ScratchBytes,
            ExpertCacheBytes = cache?.BudgetedBytes,
            ExpertCacheHits = cache?.Hits,
            ExpertCacheMisses = cache?.Misses,
            ExpertCacheEvictions = cache?.Evictions,
            ExpertDiskReads = cache?.DiskReads,
            ExpertDiskBytes = cache?.DiskBytes
        };
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            loadedModel.Dispose();
        }
    }

    private IReadOnlyList<string> BuildDiagnostics(params string[] requestDiagnostics)
    {
        var diagnostics = new List<string>
        {
            $"provider: {ProviderId}",
            $"model type: {loadedModel.Configuration.ModelType}",
            $"context limit requested: {options.ContextSize}",
            $"context limit effective: {loadedModel.MemoryPlan.ContextSize}",
            $"layers: {loadedModel.Configuration.LayerCount}",
            $"routed experts: {loadedModel.Configuration.RoutedExpertCount}",
            $"tokenizer vocabulary: {loadedModel.Tokenizer.VocabularySize}",
            $"tokenizer stop tokens: {new GlmPromptTemplate(loadedModel.Tokenizer, loadedModel.Configuration.ModelType).ResolveStopTokenIds().Count}",
            $"tensor files: {loadedModel.TensorFileCount}",
            $"open tensor shards: {loadedModel.OpenShardCount}",
            $"tensor I/O mode: {loadedModel.IoMode}",
            $"indexed tensors: {loadedModel.Tensors.Count}",
            $"resident tensors: {loadedModel.ResidentTensorCount}",
            $"resident bytes: {loadedModel.ActualResidentBytes}",
            $"planned KV bytes: {loadedModel.MemoryPlan.KvBytes}",
            $"planned scratch bytes: {loadedModel.MemoryPlan.ScratchBytes}",
            $"planned forward workspace bytes: {loadedModel.MemoryPlan.ForwardWorkspaceBytes}",
            $"planned sampling workspace bytes: {loadedModel.MemoryPlan.SamplingWorkspaceBytes}",
            $"forward batch size: {loadedModel.MemoryPlan.ForwardBatchSize}",
            $"planned MoE workspace bytes: {loadedModel.MemoryPlan.MoeWorkspaceBytes}",
            $"expert storage format: {loadedModel.ExpertLayout.Format}",
            $"quantization layout: {loadedModel.Manifest.QuantizationLayout}",
            $"expert cache slot bytes: {loadedModel.ExpertLayout.SlotBudgetedBytes}",
            $"load budget bytes: {loadedModel.MemoryPlan.RequiredBytes}/{loadedModel.MemoryPlan.AvailableBytes}",
            $"kernel execution: {OptimizedKernels.Description}",
            $"DSA top-k/start layer: {loadedModel.Configuration.DsaTopK}/{loadedModel.Configuration.DsaStartLayer}",
            $"MTP layers/head: {loadedModel.Configuration.MtpLayerCount}/{loadedModel.AdvancedFeatures.MtpHeadTensorName ?? "none"}"
        };
        if (expertCache is not null)
        {
            var snapshot = expertCache.GetSnapshot();
            diagnostics.Add($"expert cache bytes: {snapshot.BudgetedBytes}");
            diagnostics.Add($"expert cache slots per layer: {snapshot.SlotCapacityPerLayer}");
            diagnostics.Add($"expert cache hot pins/prefetches/repins: {snapshot.HotExpertCount}/{snapshot.Prefetches}/{snapshot.LiveRepins}");
            diagnostics.Add($"expert cache hit/miss/eviction: {snapshot.Hits}/{snapshot.Misses}/{snapshot.Evictions}");
            diagnostics.Add($"expert disk reads/bytes: {snapshot.DiskReads}/{snapshot.DiskBytes}");
        }

        if (generator.GetProgressSnapshot() is { } progress)
        {
            diagnostics.Add($"forward stage/layer: {progress.Stage}/{progress.Layer + 1} of {progress.LayerCount}");
            diagnostics.Add($"forward batch/completed tokens: {progress.BatchTokens}/{progress.CompletedTokens}");
            diagnostics.Add($"forward active elapsed: {progress.Elapsed.TotalSeconds:F1}s");
        }

        diagnostics.AddRange(requestDiagnostics);
        return diagnostics;
    }
}
