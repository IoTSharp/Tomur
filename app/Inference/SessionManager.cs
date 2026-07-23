using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tomur.Runtime;
using Tomur.Hardware;
using Tomur.Providers;

namespace Tomur.Inference;

public sealed class SessionManager : IDisposable
{
    private const int DefaultNpuContextLimit = 4096;

    private readonly LlamaBackendInitializer backendInitializer;
    private readonly HardwareAccelerationService accelerationService;
    private readonly ModelProviderRegistry providerRegistry;
    private readonly ILogger<SessionManager> logger;
    private readonly object gate = new();
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private LlamaNativeSession? currentSession;
    private ITextGenerationSession? currentManagedSession;
    private CancellationTokenSource? activeRequestCancellation;
    private SessionErrorSnapshot? lastError;
    private string? currentProviderId;
    private string? currentModelId;
    private string? currentModelPath;
    private int currentContextSize;
    private bool currentEmbeddings;
    private int currentGpuLayers;
    private string? currentAcceleratorKey;
    private bool unloadRequested;
    private int activeRequestCount;
    private bool disposed;

    public SessionManager(
        LlamaBackendInitializer backendInitializer,
        HardwareAccelerationService accelerationService,
        ModelProviderRegistry providerRegistry,
        ILogger<SessionManager> logger)
    {
        this.backendInitializer = backendInitializer;
        this.accelerationService = accelerationService;
        this.providerRegistry = providerRegistry;
        this.logger = logger;
    }

    internal CompletionResult Generate(
        LocalModelDescriptor model,
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Execute(cancellationToken, effectiveCancellationToken =>
        {
            ITextGenerationSession? managedSession = null;
            LlamaNativeSession? nativeSession = null;
            lock (gate)
            {
                var provider = providerRegistry.FindTextProvider(model);
                if (provider is not null)
                {
                    managedSession = GetOrLoadManagedCore(provider, model, options.ContextSize);
                }
                else
                {
                    if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
                    {
                        ThrowManagedProviderUnavailable(model);
                    }

                    nativeSession = GetOrLoadCore(model, embeddings: false, options.ContextSize);
                }
            }

            return managedSession is not null
                ? managedSession.Generate(prompt, options, effectiveCancellationToken, onToken)
                : nativeSession!.Generate(prompt, options, effectiveCancellationToken, onToken);
        });
    }

    internal CompletionResult Chat(
        LocalModelDescriptor model,
        IReadOnlyList<ChatTurn> messages,
        string fallbackPrompt,
        CompletionOptions managedOptions,
        CompletionOptions fallbackOptions,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(managedOptions);
        ArgumentNullException.ThrowIfNull(fallbackOptions);

        return Execute(cancellationToken, effectiveCancellationToken =>
        {
            ITextGenerationSession? managedSession = null;
            LlamaNativeSession? nativeSession = null;
            lock (gate)
            {
                var provider = providerRegistry.FindTextProvider(model);
                if (provider is not null)
                {
                    managedSession = GetOrLoadManagedCore(provider, model, managedOptions.ContextSize);
                }
                else
                {
                    if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
                    {
                        ThrowManagedProviderUnavailable(model);
                    }

                    nativeSession = GetOrLoadCore(model, embeddings: false, fallbackOptions.ContextSize);
                }
            }

            if (managedSession is IChatGenerationSession chatSession)
            {
                return chatSession.GenerateChat(messages, managedOptions, effectiveCancellationToken, onToken);
            }

            return managedSession is not null
                ? managedSession.Generate(fallbackPrompt, managedOptions, effectiveCancellationToken, onToken)
                : nativeSession!.Generate(fallbackPrompt, fallbackOptions, effectiveCancellationToken, onToken);
        });
    }

    internal EmbeddingResult Embed(
        LocalModelDescriptor model,
        string input,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Execute(cancellationToken, effectiveCancellationToken =>
        {
            LlamaNativeSession session;
            lock (gate)
            {
                session = GetOrLoadCore(model, embeddings: true, options.ContextSize);
            }

            return session.Embed(input, options, effectiveCancellationToken);
        });
    }

    internal void Load(LocalModelDescriptor model, int contextSize, CancellationToken cancellationToken)
    {
        Execute<object?>(cancellationToken, unusedCancellationToken =>
        {
            lock (gate)
            {
                var provider = providerRegistry.FindTextProvider(model);
                if (provider is not null)
                {
                    GetOrLoadManagedCore(provider, model, contextSize);
                }
                else
                {
                    if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
                    {
                        ThrowManagedProviderUnavailable(model);
                    }

                    GetOrLoadCore(model, embeddings: false, contextSize);
                }
            }

            return null;
        });
    }

    private T Execute<T>(CancellationToken cancellationToken, Func<CancellationToken, T> action)
    {
        executionGate.Wait(cancellationToken);
        CancellationTokenSource? requestCancellation = null;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (unloadRequested)
                {
                    throw new InferenceException(
                        "session_unloading",
                        "The active inference session is being unloaded.",
                        ["Retry the request after the unload operation completes."]);
                }

                requestCancellation = new CancellationTokenSource();
                activeRequestCancellation = requestCancellation;
                activeRequestCount = 1;
            }

            var activeCancellation = requestCancellation
                ?? throw new InvalidOperationException("Inference request cancellation was not initialized.");
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                activeCancellation.Token);
            try
            {
                return action(linkedCancellation.Token);
            }
            catch (OperationCanceledException exception)
                when (activeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var inferenceException = new InferenceException(
                    "session_unloaded",
                    "The inference request was cancelled because the active session was unloaded.",
                    ["Retry the request to load a new session."],
                    exception);
                RecordError(inferenceException);
                throw inferenceException;
            }
            catch (InferenceException exception)
            {
                RecordError(exception);
                throw;
            }
        }
        finally
        {
            if (requestCancellation is not null)
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeRequestCancellation, requestCancellation))
                    {
                        activeRequestCancellation = null;
                    }

                    activeRequestCount = 0;
                }

                requestCancellation.Dispose();
            }

            executionGate.Release();
        }
    }

    private void RecordError(InferenceException exception)
    {
        lock (gate)
        {
            lastError = new SessionErrorSnapshot(
                exception.Code,
                exception.Message,
                DateTimeOffset.UtcNow);
        }
    }

    private LlamaNativeSession GetOrLoadCore(LocalModelDescriptor model, bool embeddings, int contextSize)
    {
        ArgumentNullException.ThrowIfNull(model);

        ObjectDisposedException.ThrowIf(disposed, this);
        var effectiveContextSize = Math.Clamp(contextSize, 512, 131072);

        if (currentManagedSession is not null)
        {
            currentManagedSession.Dispose();
            currentManagedSession = null;
            currentProviderId = null;
            ResetSessionMetadata();
        }

        var accelerationPlan = accelerationService.ResolvePlan(model);
        EnsureAccelerationPlanCanExecute(accelerationPlan, model, effectiveContextSize);
        if (currentSession is not null &&
            string.Equals(currentModelId, model.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentModelPath, model.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
            currentContextSize == effectiveContextSize &&
            currentEmbeddings == embeddings &&
            currentGpuLayers == accelerationPlan.EffectiveGpuLayers &&
            string.Equals(currentAcceleratorKey, accelerationPlan.SelectedAcceleratorKey, StringComparison.OrdinalIgnoreCase))
        {
            return currentSession;
        }

        currentSession?.Dispose();
        currentSession = null;
        currentModelId = null;
        currentModelPath = null;
        currentContextSize = 0;
        currentEmbeddings = false;
        currentGpuLayers = 0;
        currentAcceleratorKey = null;

        backendInitializer.EnsureInitialized();
        var acceleratorLabel = accelerationPlan.SelectedAcceleratorKey
            ?? accelerationPlan.EffectiveBackend
            ?? "cpu";
        logger.SessionLoading(model.Id, effectiveContextSize, accelerationPlan.EffectiveGpuLayers, acceleratorLabel);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            currentSession = new LlamaNativeSession(
                model.Id,
                model.AbsolutePath,
                contextSize: effectiveContextSize,
                gpuLayers: accelerationPlan.EffectiveGpuLayers,
                acceleratorKey: accelerationPlan.SelectedAcceleratorKey,
                accelerator: accelerationPlan.SelectedAccelerator,
                npuRequested: IsNpuRequested(accelerationPlan),
                embeddings: embeddings,
                logger: logger);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            logger.SessionLoadFailed(model.Id, exception);
            throw new InferenceException(
                "native_runtime_unavailable",
                $"The llama.cpp native runtime could not be loaded: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Run tomur doctor to inspect native runtime status."
                ],
                exception);
        }

        stopwatch.Stop();
        logger.SessionLoaded(model.Id, stopwatch.ElapsedMilliseconds);
        currentModelId = model.Id;
        currentModelPath = model.AbsolutePath;
        currentContextSize = effectiveContextSize;
        currentEmbeddings = embeddings;
        currentGpuLayers = accelerationPlan.EffectiveGpuLayers;
        currentAcceleratorKey = accelerationPlan.SelectedAcceleratorKey;
        lastError = null;
        return currentSession;
    }

    private ITextGenerationSession GetOrLoadManagedCore(
        ITextGenerationProvider provider,
        LocalModelDescriptor model,
        int contextSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var effectiveContextSize = Math.Clamp(contextSize, 1, 131072);
        if (currentManagedSession is not null &&
            string.Equals(currentProviderId, provider.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentModelId, model.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentModelPath, model.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
            currentContextSize == effectiveContextSize)
        {
            return currentManagedSession;
        }

        currentSession?.Dispose();
        currentSession = null;
        currentManagedSession?.Dispose();
        currentManagedSession = null;
        ResetSessionMetadata();

        logger.SessionLoading(model.Id, effectiveContextSize, 0, provider.Id);
        var stopwatch = Stopwatch.StartNew();
        ITextGenerationSession managedSession;
        try
        {
            managedSession = provider.CreateSession(
                model,
                new ModelSessionOptions(effectiveContextSize));
        }
        catch (InferenceException exception)
        {
            logger.SessionLoadFailed(model.Id, exception);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.SessionLoadFailed(model.Id, exception);
            throw new InferenceException(
                "managed_provider_load_failed",
                $"The managed model provider could not load the model: {exception.Message}",
                [
                    "Verify the model provider manifest, configuration, tokenizer and tensor files.",
                    $"Verify that provider '{provider.Id}' supports architecture '{model.Family}'."
                ],
                exception);
        }

        stopwatch.Stop();
        logger.SessionLoaded(model.Id, stopwatch.ElapsedMilliseconds);
        currentManagedSession = managedSession;
        currentProviderId = provider.Id;
        currentModelId = model.Id;
        currentModelPath = model.AbsolutePath;
        currentContextSize = effectiveContextSize;
        currentEmbeddings = false;
        lastError = null;
        return managedSession;
    }

    private static void EnsureAccelerationPlanCanExecute(
        AccelerationPlan accelerationPlan,
        LocalModelDescriptor model,
        int contextSize)
    {
        if (!IsNpuRequested(accelerationPlan) || contextSize <= DefaultNpuContextLimit)
        {
            return;
        }

        var selected = accelerationPlan.SelectedAccelerator;
        var selectedSummary = selected is null
            ? accelerationPlan.OpenVinoDevice ?? "OpenVINO NPU"
            : $"{selected.Name} ({selected.Backend}, {selected.SelectionKey})";

        throw new InferenceException(
            "npu_context_not_supported",
            $"The selected Intel NPU accelerator is limited to {DefaultNpuContextLimit} context tokens in the current Tomur safety profile, but this request asked for {contextSize}.",
            [
                $"Selected accelerator: {selectedSummary}.",
                $"Model: {model.Id} ({model.QuantizationLevel}, {model.SizeBytes} bytes).",
                "Reduce num_ctx or the request context before retrying on Intel NPU.",
                "Use runtime.accelerator.preference=cpu, sycl, vulkan or OpenVINO GPU when this model requires a larger context.",
                "Set runtime.accelerator.openvino_device to NPU only for models and context windows that have real smoke evidence."
            ]);
    }

    private static bool IsNpuRequested(AccelerationPlan accelerationPlan)
        => accelerationPlan.SelectedAccelerator is not null &&
            (IsNpuSelected(accelerationPlan.SelectedAccelerator) ||
             IsNpuDeviceName(accelerationPlan.OpenVinoDevice));

    private static bool IsNpuSelected(AcceleratorDevice? accelerator)
        => accelerator is not null &&
            string.Equals(accelerator.Kind, AcceleratorKind.Npu.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsNpuDeviceName(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
            value.Trim().StartsWith("NPU", StringComparison.OrdinalIgnoreCase);

    private void ThrowManagedProviderUnavailable(LocalModelDescriptor model)
    {
        var loaderDiagnostics = providerRegistry.Diagnostics
            .Select(static item => $"{item.Code}: {item.Message}")
            .ToArray();
        throw new InferenceException(
            "managed_provider_unavailable",
            $"No managed model provider is available for architecture '{model.Family}'.",
            loaderDiagnostics.Length == 0
                ?
                [
                    "Verify that the model manifest selects a provider compiled into this Tomur build.",
                    "Install a Tomur build that includes the required managed model provider."
                ]
                : loaderDiagnostics);
    }

    public void Unload()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            unloadRequested = true;
            cancellation = activeRequestCancellation;
        }

        try
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request completed between observing and cancelling its token.
            }

            executionGate.Wait();
            try
            {
                lock (gate)
                {
                    UnloadCore();
                }
            }
            finally
            {
                executionGate.Release();
            }
        }
        finally
        {
            lock (gate)
            {
                unloadRequested = false;
            }
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (gate)
        {
            SessionSnapshot snapshot;
            if (currentManagedSession is not null)
            {
                snapshot = currentManagedSession.GetSnapshot();
            }
            else if (currentSession is not null)
            {
                snapshot = currentSession.GetSnapshot();
            }
            else
            {
                snapshot = new SessionSnapshot(
                    Loaded: false,
                    ModelId: null,
                    ModelPath: null,
                    Mode: null,
                    LoadedAt: null,
                    RequestCount: 0,
                    PromptTokens: 0,
                    CompletionTokens: 0,
                    Diagnostics: ["no active inference session"]);
            }

            return snapshot with
            {
                Busy = activeRequestCount > 0,
                LastError = lastError
            };
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            unloadRequested = true;
            cancellation = activeRequestCancellation;
        }

        try
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            executionGate.Wait();
            try
            {
                lock (gate)
                {
                    UnloadCore();
                    disposed = true;
                    unloadRequested = false;
                }
            }
            finally
            {
                executionGate.Release();
            }
        }
        catch
        {
            lock (gate)
            {
                unloadRequested = false;
            }

            throw;
        }
    }

    private void UnloadCore()
    {
        if (currentSession is not null || currentManagedSession is not null)
        {
            logger.SessionUnloaded(currentModelId ?? "unknown");
        }

        currentSession?.Dispose();
        currentSession = null;
        currentManagedSession?.Dispose();
        currentManagedSession = null;
        currentProviderId = null;
        ResetSessionMetadata();
    }

    private void ResetSessionMetadata()
    {
        currentModelId = null;
        currentModelPath = null;
        currentContextSize = 0;
        currentEmbeddings = false;
        currentGpuLayers = 0;
        currentAcceleratorKey = null;
    }
}
