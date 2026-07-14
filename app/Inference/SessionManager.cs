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
    private LlamaNativeSession? currentSession;
    private ITextGenerationSession? currentManagedSession;
    private string? currentProviderId;
    private string? currentModelId;
    private string? currentModelPath;
    private int currentContextSize;
    private bool currentEmbeddings;
    private int currentGpuLayers;
    private string? currentAcceleratorKey;
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

        lock (gate)
        {
            var provider = providerRegistry.FindTextProvider(model);
            if (provider is not null)
            {
                var managedSession = GetOrLoadManagedCore(provider, model, options.ContextSize);
                return managedSession.Generate(prompt, options, cancellationToken, onToken);
            }

            if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
            {
                ThrowManagedProviderUnavailable(model);
            }

            var session = GetOrLoadCore(model, embeddings: false, options.ContextSize);
            return session.Generate(prompt, options, cancellationToken, onToken);
        }
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

        lock (gate)
        {
            var provider = providerRegistry.FindTextProvider(model);
            if (provider is not null)
            {
                var managedSession = GetOrLoadManagedCore(provider, model, managedOptions.ContextSize);
                return managedSession is IChatGenerationSession chatSession
                    ? chatSession.GenerateChat(messages, managedOptions, cancellationToken, onToken)
                    : managedSession.Generate(fallbackPrompt, managedOptions, cancellationToken, onToken);
            }

            if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
            {
                ThrowManagedProviderUnavailable(model);
            }

            var session = GetOrLoadCore(model, embeddings: false, fallbackOptions.ContextSize);
            return session.Generate(fallbackPrompt, fallbackOptions, cancellationToken, onToken);
        }
    }

    internal EmbeddingResult Embed(
        LocalModelDescriptor model,
        string input,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (gate)
        {
            var session = GetOrLoadCore(model, embeddings: true, options.ContextSize);
            return session.Embed(input, options, cancellationToken);
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
                    $"Place the matching provider DLL in '{Path.Combine(AppContext.BaseDirectory, "providers")}'.",
                    $"Alternatively set {ModelProviderRegistry.ProviderPathEnvironmentVariable} to the provider output directory."
                ]
                : loaderDiagnostics);
    }

    public void Unload()
    {
        lock (gate)
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
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (gate)
        {
            if (currentManagedSession is not null)
            {
                return currentManagedSession.GetSnapshot();
            }

            if (currentSession is null)
            {
                return new SessionSnapshot(
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

            return currentSession.GetSnapshot();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            currentSession?.Dispose();
            currentSession = null;
            currentManagedSession?.Dispose();
            currentManagedSession = null;
            currentProviderId = null;
            ResetSessionMetadata();
            disposed = true;
        }
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
