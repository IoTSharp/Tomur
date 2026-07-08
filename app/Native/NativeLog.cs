using Microsoft.Extensions.Logging;

namespace Tomur.Native;

/// <summary>
/// Source-generated log messages for native library loading and bundle preparation.
/// EventId range: 1200-1299.
/// </summary>
internal static partial class NativeLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug,
        Message = "native library loaded component={Component} library={Library}")]
    public static partial void LibraryLoaded(this ILogger logger, string component, string library);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning,
        Message = "native library load failed component={Component} library={Library}: {Reason}")]
    public static partial void LibraryLoadFailed(this ILogger logger, string component, string library, string reason);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
        Message = "native component not ready component={Component}: {Reason}")]
    public static partial void ComponentNotReady(this ILogger logger, string component, string reason);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Error,
        Message = "native bundle manifest could not be read: {Reason}")]
    public static partial void BundleManifestInvalid(this ILogger logger, string reason);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Warning,
        Message = "native bundle file prepare failed dest={Destination}: {Reason}")]
    public static partial void BundleFilePrepareFailed(this ILogger logger, string destination, string reason);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Information,
        Message = "native bundle prepared: {ChangedFiles} file(s) changed ({Status})")]
    public static partial void BundlePrepared(this ILogger logger, int changedFiles, string status);
}
