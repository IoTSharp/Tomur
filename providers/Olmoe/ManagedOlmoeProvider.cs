using System.Diagnostics;
using System.Text.Json;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.Olmoe;

public sealed class ManagedOlmoeProvider : ITextGenerationProvider, IModelReadinessProvider, IModelConversionProvider
{
    public const string ProviderId = "managed-olmoe";

    public string Id => ProviderId;

    public bool CanHandle(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Format.Equals("managed-model", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ModelProviderManifestReader.TryRead(model.AbsolutePath, out var manifest, out var error) || manifest is null)
        {
            throw new InvalidDataException(error ?? "Managed model provider manifest is invalid.");
        }

        return manifest.Provider.Equals(ProviderId, StringComparison.OrdinalIgnoreCase);
    }

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var probe = OlmoeModelDirectoryProbe.Read(model, ProviderId);
            return new ManagedOlmoeSession(model, options, probe);
        }
        catch (OlmoeMemoryBudgetExceededException exception)
        {
            throw new InferenceException(
                "managed_model_memory_budget_exceeded",
                exception.Message,
                ["Reduce the requested context size.", "Unload other memory-heavy applications."],
                exception);
        }
        catch (ExpertCacheBudgetExceededException exception)
        {
            throw new InferenceException(
                "managed_model_memory_budget_exceeded",
                exception.Message,
                ["The OLMoE provider requires one complete top-k expert working set per layer."],
                exception);
        }
        catch (OutOfMemoryException exception)
        {
            throw new InferenceException(
                "managed_model_out_of_memory",
                $"The managed OLMoE model could not be allocated after memory preflight: {exception.Message}",
                ["Reduce context size or unload other memory-heavy applications."],
                exception);
        }
        catch (ContextLengthExceededException exception)
        {
            throw new InferenceException(
                "managed_context_length_exceeded",
                exception.Message,
                [$"Request no more than {exception.ContextLimit} total tokens."],
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw CreateModelException(exception, "prepared");
        }
    }

    public ModelConversionResult ConvertModel(
        ModelConversionRequest request,
        Action<ModelConversionProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
        => OlmoeModelConverter.Convert(request, onProgress, cancellationToken);

    public ModelPreparationResult InspectModel(LocalModelDescriptor model, ModelSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var probe = OlmoeModelDirectoryProbe.Read(model, ProviderId);
            var contextSize = Math.Min(options.ContextSize, probe.Configuration.MaxPositionEmbeddings);
            var residentWeights = OlmoeResidentWeightLayout.Create(probe.Configuration, probe.Tensors);
            var expertLayout = ExpertDescriptorLayout.Create(
                probe.Configuration.ExpertConfiguration,
                probe.Manifest.Quantization,
                probe.Manifest.QuantizationLayout,
                probe.Tensors);
            var memoryPlan = OlmoeMemoryPlan.Create(
                probe.Configuration,
                residentWeights,
                contextSize,
                ModelMemoryPlan.GetAvailableMemoryBytes());
            var expertCacheBytes = checked(
                expertLayout.SlotBudgetedBytes *
                expertLayout.MoeLayerCount *
                probe.Configuration.ExpertsPerToken);

            return new ModelPreparationResult(
                ProviderId,
                probe.Manifest.Architecture,
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
                    $"expert storage format: {expertLayout.Format}"
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

    private static InferenceException CreateModelException(Exception exception, string operation)
    {
        var assetsIncomplete = exception is FileNotFoundException or DirectoryNotFoundException ||
            exception.Message.Contains("is missing", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("must exist", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("No tensor files", StringComparison.OrdinalIgnoreCase);
        var quantizationUnsupported = exception.Message.Contains("does not support quantization", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("quantization layout", StringComparison.OrdinalIgnoreCase);
        return new InferenceException(
            assetsIncomplete
                ? "managed_model_assets_incomplete"
                : quantizationUnsupported
                    ? "managed_quantization_unsupported"
                    : "managed_model_invalid",
            $"The managed OLMoE model could not be {operation}: {exception.Message}",
            [
                $"Verify {ModelProviderManifest.FileName}, config.json, tokenizer.json and every safetensors shard.",
                "Complete model download and checksum verification before exposing the model through compatibility APIs."
            ],
            exception);
    }
}

internal sealed class ManagedOlmoeSession : IChatGenerationSession
{
    private readonly object gate = new();
    private readonly LocalModelDescriptor descriptor;
    private readonly ModelSessionOptions sessionOptions;
    private readonly ManagedOlmoeModel model;
    private readonly ExpertCache expertCache;
    private readonly OlmoeTextGenerator generator;
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private long requestCount;
    private long promptTokens;
    private long completionTokens;
    private readonly long loadElapsedMilliseconds;
    private double lastFirstTokenMilliseconds;
    private double lastGenerationMilliseconds;
    private double lastOutputTokensPerSecond;
    private double lastDecodeTokensPerSecond = double.NaN;
    private bool disposed;

    public ManagedOlmoeSession(
        LocalModelDescriptor descriptor,
        ModelSessionOptions sessionOptions,
        OlmoeModelProbe probe)
    {
        var loadStopwatch = Stopwatch.StartNew();
        this.descriptor = descriptor;
        this.sessionOptions = sessionOptions;
        var effectiveContextSize = Math.Min(
            sessionOptions.ContextSize,
            probe.Configuration.MaxPositionEmbeddings);
        var candidate = ManagedOlmoeModel.Load(probe, effectiveContextSize);
        try
        {
            var minimumCacheBytes = checked(
                candidate.ExpertLayout.SlotBudgetedBytes *
                candidate.ExpertLayout.MoeLayerCount *
                candidate.Configuration.ExpertsPerToken);
            expertCache = candidate.CreateExpertCache(new ExpertCacheOptions(
                minimumCacheBytes,
                WorkerCount: Math.Min(2, candidate.Configuration.ExpertsPerToken),
                QueueCapacity: Math.Max(8, candidate.Configuration.ExpertsPerToken)));
            model = candidate;
            generator = new OlmoeTextGenerator(candidate, expertCache);
            loadStopwatch.Stop();
            loadElapsedMilliseconds = loadStopwatch.ElapsedMilliseconds;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public string ProviderId => ManagedOlmoeProvider.ProviderId;

    public CompletionResult Generate(
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw EmptyPrompt("The prompt is empty after normalization.");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var prepared = new OlmoePromptTemplate(model.Configuration, model.Tokenizer)
                .BuildCompletion(prompt);
            return GeneratePrepared(prepared, options, cancellationToken, onToken);
        }
    }

    public CompletionResult GenerateChat(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!messages.Any(static message => !string.IsNullOrWhiteSpace(message.Content)))
        {
            throw EmptyPrompt("The chat request does not contain a non-empty message.");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var prepared = new OlmoePromptTemplate(model.Configuration, model.Tokenizer)
                .BuildChat(messages);
            return GeneratePrepared(prepared, options, cancellationToken, onToken);
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        var cache = expertCache.GetSnapshot();
        var completedRequests = Interlocked.Read(ref requestCount);
        return new SessionSnapshot(
            Loaded: true,
            ModelId: descriptor.Id,
            ModelPath: descriptor.AbsolutePath,
            Mode: "managed-olmoe-generation",
            LoadedAt: createdAt,
            RequestCount: completedRequests,
            PromptTokens: Interlocked.Read(ref promptTokens),
            CompletionTokens: Interlocked.Read(ref completionTokens),
            Diagnostics: BuildDiagnostics("forward execution: scalar reference"))
        {
            ProviderId = ProviderId,
            Architecture = model.Manifest.Architecture,
            Quantization = model.Manifest.Quantization,
            ContextSize = model.MemoryPlan.ContextSize,
            ResidentBytes = model.ActualResidentBytes,
            KvBytes = model.MemoryPlan.KvBytes,
            ScratchBytes = model.MemoryPlan.ScratchBytes,
            ExpertCacheBytes = cache.BudgetedBytes,
            ExpertCacheHits = cache.Hits,
            ExpertCacheMisses = cache.Misses,
            ExpertCacheEvictions = cache.Evictions,
            ExpertDiskReads = cache.DiskReads,
            ExpertDiskBytes = cache.DiskBytes,
            LoadElapsedMilliseconds = loadElapsedMilliseconds,
            LastFirstTokenMilliseconds = completedRequests == 0 ? null : Volatile.Read(ref lastFirstTokenMilliseconds),
            LastGenerationMilliseconds = completedRequests == 0 ? null : Volatile.Read(ref lastGenerationMilliseconds),
            LastOutputTokensPerSecond = completedRequests == 0 ? null : Volatile.Read(ref lastOutputTokensPerSecond),
            LastDecodeTokensPerSecond = completedRequests == 0 || double.IsNaN(Volatile.Read(ref lastDecodeTokensPerSecond))
                ? null
                : Volatile.Read(ref lastDecodeTokensPerSecond)
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
            model.Dispose();
        }
    }

    private CompletionResult GeneratePrepared(
        OlmoePrompt prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = generator.GenerateAsync(prompt, options, cancellationToken, onToken)
                .AsTask().GetAwaiter().GetResult();
            stopwatch.Stop();
            Interlocked.Increment(ref requestCount);
            Interlocked.Add(ref promptTokens, result.PromptTokenCount);
            Interlocked.Add(ref completionTokens, result.GeneratedTokenIds.Count);
            Volatile.Write(ref lastFirstTokenMilliseconds, result.FirstTokenElapsed.TotalMilliseconds);
            Volatile.Write(ref lastGenerationMilliseconds, result.GenerationElapsed.TotalMilliseconds);
            Volatile.Write(ref lastOutputTokensPerSecond, result.OutputTokensPerSecond);
            Volatile.Write(ref lastDecodeTokensPerSecond, result.DecodeTokensPerSecond ?? double.NaN);
            return new CompletionResult(
                result.Text,
                new TokenUsage(
                    result.PromptTokenCount,
                    result.GeneratedTokenIds.Count,
                    checked(result.PromptTokenCount + result.GeneratedTokenIds.Count)),
                stopwatch.Elapsed,
                BuildDiagnostics(
                    $"prompt tokens: {result.PromptTokenCount}",
                    $"completion tokens: {result.GeneratedTokenIds.Count}",
                    $"stop reason: {result.StopReason}",
                    $"sampling seed: {result.Seed}",
                    $"first token ms: {result.FirstTokenElapsed.TotalMilliseconds:F3}",
                    $"generation ms: {result.GenerationElapsed.TotalMilliseconds:F3}",
                    $"output tokens/s: {result.OutputTokensPerSecond:F6}",
                    result.DecodeTokensPerSecond is null
                        ? "decode tokens/s: unavailable (requires at least two output tokens)"
                        : $"decode tokens/s: {result.DecodeTokensPerSecond.Value:F6}"));
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
                ["Reduce max_tokens, shorten the prompt, or request a larger supported context."],
                exception);
        }
        catch (Exception exception)
        {
            throw new InferenceException(
                "managed_inference_failed",
                $"Managed OLMoE forward execution failed: {exception.Message}",
                ["Verify the model assets and retry with a shorter context."],
                exception);
        }
    }

    private IReadOnlyList<string> BuildDiagnostics(params string[] requestDiagnostics)
    {
        var cache = expertCache.GetSnapshot();
        var diagnostics = new List<string>
        {
            $"provider: {ProviderId}",
            $"context limit requested: {sessionOptions.ContextSize}",
            $"context limit effective: {model.MemoryPlan.ContextSize}",
            $"layers: {model.Configuration.LayerCount}",
            $"attention heads/KV heads: {model.Configuration.AttentionHeadCount}/{model.Configuration.KeyValueHeadCount}",
            $"routed experts/top-k: {model.Configuration.RoutedExpertCount}/{model.Configuration.ExpertsPerToken}",
            $"tensor files: {model.TensorFileCount}",
            $"open tensor shards: {model.OpenShardCount}",
            $"resident tensors: {model.ResidentTensorCount}",
            $"resident bytes: {model.ActualResidentBytes}",
            $"planned KV bytes: {model.MemoryPlan.KvBytes}",
            $"planned scratch bytes: {model.MemoryPlan.ScratchBytes}",
            $"expert storage format: {model.ExpertLayout.Format}",
            $"quantization layout: {model.Manifest.QuantizationLayout}",
            $"expert cache bytes/slots per layer: {cache.BudgetedBytes}/{cache.SlotCapacityPerLayer}",
            $"expert cache hit/miss/eviction: {cache.Hits}/{cache.Misses}/{cache.Evictions}",
            $"expert disk reads/bytes: {cache.DiskReads}/{cache.DiskBytes}",
            $"load budget bytes: {model.MemoryPlan.RequiredBytes}/{model.MemoryPlan.AvailableBytes}",
            $"load elapsed ms: {loadElapsedMilliseconds}"
        };
        diagnostics.AddRange(requestDiagnostics);
        return diagnostics;
    }

    private static InferenceException EmptyPrompt(string message)
        => new("empty_prompt", message, ["Provide at least one non-empty user message or prompt."]);
}
