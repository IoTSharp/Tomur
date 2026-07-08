using Microsoft.Extensions.Logging;

namespace Tomur.Inference;

/// <summary>
/// Source-generated log messages for the inference domain (native backend init,
/// llama session lifecycle, native model/context/generation). Using
/// <c>[LoggerMessage]</c> keeps these allocation-free and Native-AOT/trim safe.
/// EventId range: 1000-1199.
/// </summary>
internal static partial class InferenceLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "llama.cpp native backend initialized")]
    public static partial void BackendInitialized(this ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "llama.cpp native backend initialization failed")]
    public static partial void BackendInitializationFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "loaded dynamic ggml backends from {RuntimeRoot}")]
    public static partial void DynamicBackendsLoaded(this ILogger logger, string runtimeRoot);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "dynamic ggml backend probing skipped: {Reason}")]
    public static partial void DynamicBackendProbeSkipped(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Debug,
        Message = "llama import resolver fallback for {Library}")]
    public static partial void ImportResolverFallback(this ILogger logger, string library);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "loading llama session model={Model} ctx={ContextSize} gpuLayers={GpuLayers} accelerator={Accelerator}")]
    public static partial void SessionLoading(
        this ILogger logger, string model, int contextSize, int gpuLayers, string accelerator);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
        Message = "llama session ready model={Model} elapsedMs={ElapsedMs}")]
    public static partial void SessionLoaded(this ILogger logger, string model, long elapsedMs);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error,
        Message = "llama session load failed model={Model}")]
    public static partial void SessionLoadFailed(this ILogger logger, string model, Exception exception);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
        Message = "llama session unloaded model={Model}")]
    public static partial void SessionUnloaded(this ILogger logger, string model);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Error,
        Message = "native model load failed model={Model} path={Path}")]
    public static partial void ModelLoadFailed(this ILogger logger, string model, string path);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Error,
        Message = "native context init failed model={Model}")]
    public static partial void ContextInitFailed(this ILogger logger, string model);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Warning,
        Message = "native decode failed phase={Phase} code={Code}")]
    public static partial void DecodeFailed(this ILogger logger, string phase, int code);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Debug,
        Message = "generation complete model={Model} promptTokens={PromptTokens} completionTokens={CompletionTokens} elapsedMs={ElapsedMs}")]
    public static partial void GenerationCompleted(
        this ILogger logger, string model, int promptTokens, int completionTokens, long elapsedMs);
}
