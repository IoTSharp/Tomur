using Microsoft.Extensions.Logging;

namespace Tomur.Models;

/// <summary>
/// Source-generated log messages for model download / install. EventId range: 1400-1499.
/// Human-readable progress still goes to the injected TextWriter; these are operational logs.
/// </summary>
internal static partial class ModelLog
{
    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning,
        Message = "model download source failed {Url}: {Reason}")]
    public static partial void DownloadSourceFailed(this ILogger logger, string url, string reason);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error,
        Message = "model download failed {Target}: all sources exhausted")]
    public static partial void DownloadFailed(this ILogger logger, string target);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Error,
        Message = "model checksum mismatch {Target} expected={Expected} actual={Actual}")]
    public static partial void ChecksumMismatch(this ILogger logger, string target, string expected, string actual);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Information,
        Message = "model package installed {Package} ({Status})")]
    public static partial void PackageInstalled(this ILogger logger, string package, string status);
}
