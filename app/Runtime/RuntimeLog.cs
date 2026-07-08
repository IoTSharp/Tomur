using Microsoft.Extensions.Logging;

namespace Tomur.Runtime;

/// <summary>
/// Source-generated log messages for runtime diagnostics (directory/disk preparation).
/// EventId range: 1700-1799.
/// </summary>
internal static partial class RuntimeLog
{
    [LoggerMessage(EventId = 1700, Level = LogLevel.Warning,
        Message = "runtime directory unavailable name={Name} path={Path}: {Reason}")]
    public static partial void DirectoryUnavailable(this ILogger logger, string name, string path, string reason);
}
