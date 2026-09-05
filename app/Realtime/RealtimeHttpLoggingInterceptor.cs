using Microsoft.AspNetCore.HttpLogging;

namespace Tomur.Realtime;

internal sealed class RealtimeHttpLoggingInterceptor : IHttpLoggingInterceptor
{
    private const string RealtimePathPrefix = "/api/realtime";

    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        if (ShouldSuppressRequestPath(logContext.HttpContext.Request.Path.Value))
        {
            logContext.Disable(HttpLoggingFields.RequestPath);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext)
        => ValueTask.CompletedTask;

    internal static bool ShouldSuppressRequestPath(string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return false;
        }

        if (ContainsKnownCredentialPrefix(requestPath))
        {
            return true;
        }

        if (!IsRealtimeNamespacePath(requestPath))
        {
            return false;
        }

        // Only the fixed public routes are valid. Extra path material in this
        // reserved namespace may be a credential appended by a client.
        return !IsSafeRealtimePath(requestPath);
    }

    private static bool ContainsKnownCredentialPrefix(string requestPath)
        => requestPath.Contains("rtt_", StringComparison.OrdinalIgnoreCase) ||
           requestPath.Contains("tmr_", StringComparison.OrdinalIgnoreCase) ||
           requestPath.Contains("rtt%5f", StringComparison.OrdinalIgnoreCase) ||
           requestPath.Contains("tmr%5f", StringComparison.OrdinalIgnoreCase);

    private static bool IsRealtimeNamespacePath(string requestPath)
    {
        if (!requestPath.StartsWith(RealtimePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestPath.Length == RealtimePathPrefix.Length)
        {
            return true;
        }

        return requestPath[RealtimePathPrefix.Length] is '/' or '\\' or ';' or '%';
    }

    private static bool IsSafeRealtimePath(string requestPath)
        => IsExactPathOrTrailingSlash(requestPath, RealtimePathPrefix) ||
           IsExactPathOrTrailingSlash(requestPath, RealtimeProtocol.StatusPath) ||
           IsExactPathOrTrailingSlash(requestPath, RealtimeProtocol.TicketPath) ||
           IsExactPathOrTrailingSlash(requestPath, RealtimeProtocol.WebSocketPath);

    private static bool IsExactPathOrTrailingSlash(string requestPath, string expectedPath)
        => requestPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
           (requestPath.Length == expectedPath.Length + 1 &&
            requestPath[^1] == '/' &&
            requestPath.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase));
}
