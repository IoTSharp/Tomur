using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tomur.Runtime;
using Tomur.Hardware;

namespace Tomur.Inference;

public sealed class SessionManager : IDisposable
{
    private const int DefaultNpuContextLimit = 4096;

    private readonly LlamaBackendInitializer backendInitializer;
    private readonly HardwareAccelerationService accelerationService;
    private readonly ILogger<SessionManager> logger;
    private readonly object gate = new();
    private LlamaNativeSession? currentSession;
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
        ILogger<SessionManager> logger)
    {
        this.backendInitializer = backendInitializer;
        this.accelerationService = accelerationService;
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
            var session = GetOrLoadCore(model, embeddings: false, options.ContextSize);
            return session.Generate(prompt, options, cancellationToken, onToken);
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

    public void Unload()
    {
        lock (gate)
        {
            if (currentSession is not null)
            {
                logger.SessionUnloaded(currentModelId ?? "unknown");
            }

            currentSession?.Dispose();
            currentSession = null;
            currentModelId = null;
            currentModelPath = null;
            currentContextSize = 0;
            currentEmbeddings = false;
            currentGpuLayers = 0;
            currentAcceleratorKey = null;
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (gate)
        {
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
                    Diagnostics: ["no active llama session"]);
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
            currentContextSize = 0;
            currentEmbeddings = false;
            currentGpuLayers = 0;
            currentAcceleratorKey = null;
            disposed = true;
        }
    }
}
