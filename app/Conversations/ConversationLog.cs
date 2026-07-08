using Microsoft.Extensions.Logging;

namespace Tomur.Conversations;

/// <summary>
/// Source-generated log messages for conversation orchestration. EventId range: 1500-1599.
/// Complements the existing RuntimeDiagnostic responses and AgentEventLog JSONL audit trail
/// with severity-graded operational logging at the turn level.
/// </summary>
internal static partial class ConversationLog
{
    [LoggerMessage(EventId = 1500, Level = LogLevel.Error,
        Message = "conversation turn failed: native runtime unavailable")]
    public static partial void TurnNativeRuntimeUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "conversation file operation failed: {Reason}")]
    public static partial void TurnFileOperationFailed(this ILogger logger, string reason);
}
