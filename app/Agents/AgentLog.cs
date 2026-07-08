using Microsoft.Extensions.Logging;

namespace Tomur.Agents;

/// <summary>
/// Source-generated log messages for the agent runtime and tool invocation.
/// EventId range: 1600-1699.
/// </summary>
internal static partial class AgentLog
{
    [LoggerMessage(EventId = 1600, Level = LogLevel.Warning,
        Message = "agent tool invocation failed tool={Tool}")]
    public static partial void ToolInvocationFailed(this ILogger logger, string tool, Exception exception);
}
