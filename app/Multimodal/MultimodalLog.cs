using Microsoft.Extensions.Logging;

namespace Tomur.Multimodal;

/// <summary>
/// Source-generated log messages for multimodal execution and the isolated image worker.
/// EventId range: 1300-1399.
/// </summary>
internal static partial class MultimodalLog
{
    [LoggerMessage(EventId = 1300, Level = LogLevel.Error,
        Message = "multimodal native runtime unavailable backend={Backend}")]
    public static partial void NativeRuntimeUnavailable(this ILogger logger, string backend, Exception exception);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Error,
        Message = "image worker failed to start: {Reason}")]
    public static partial void ImageWorkerStartFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Error,
        Message = "image worker timed out after {TimeoutSeconds}s")]
    public static partial void ImageWorkerTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Error,
        Message = "image worker produced no valid response: {Reason}")]
    public static partial void ImageWorkerInvalidResponse(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Debug,
        Message = "image worker exited code={ExitCode}")]
    public static partial void ImageWorkerExited(this ILogger logger, int exitCode);
}
