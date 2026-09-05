using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Tomur.Storage;

namespace Tomur.Realtime;

internal sealed record RealtimeRequestValidationResult(
    bool Success,
    string Source,
    bool PreAuthenticated,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed class RealtimeRequestValidator
{
    private const int MaxAuthorizationHeaderLength = 384;

    private readonly ApiKeyStore apiKeys;

    public RealtimeRequestValidator(ApiKeyStore apiKeys)
    {
        this.apiKeys = apiKeys;
    }

    public RealtimeRequestValidationResult ValidateTicketRequest(HttpContext context)
    {
        var boundary = ValidateLocalRequest(context);
        if (!boundary.Success)
        {
            return boundary;
        }

        var bearer = ValidateBearer(context.Request.Headers.Authorization);
        if (context.Request.Headers.Origin.Count == 0 && bearer == BearerValidation.Missing)
        {
            return Failure(
                boundary.Source,
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Non-browser clients must use a Bearer API key to issue a Realtime ticket.");
        }

        return bearer switch
        {
            BearerValidation.Invalid => Failure(
                boundary.Source,
                StatusCodes.Status401Unauthorized,
                "invalid_api_key",
                "The Bearer API key is invalid."),
            BearerValidation.Unavailable => Failure(
                boundary.Source,
                StatusCodes.Status503ServiceUnavailable,
                "api_key_store_unavailable",
                "The API key store is unavailable."),
            _ => boundary with { PreAuthenticated = bearer == BearerValidation.Valid }
        };
    }

    internal RealtimeRequestValidationResult ValidateLocalRequest(HttpContext context)
    {
        var boundary = ValidateLocalBoundary(context);
        if (!boundary.Success)
        {
            return boundary;
        }

        return ContainsForbiddenQuery(context.Request.QueryString)
            ? Failure(
                boundary.Source,
                StatusCodes.Status400BadRequest,
                "credential_in_query_forbidden",
                "Realtime protocol v1 does not accept URL query parameters; credentials must not be sent in URLs.")
            : boundary;
    }

    public RealtimeRequestValidationResult ValidateUpgrade(HttpContext context)
    {
        var boundary = ValidateLocalRequest(context);
        if (!boundary.Success)
        {
            return boundary;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Failure(
                boundary.Source,
                StatusCodes.Status426UpgradeRequired,
                "websocket_required",
                "This endpoint requires a WebSocket upgrade.");
        }

        var requestedProtocols = context.WebSockets.WebSocketRequestedProtocols;
        if (requestedProtocols.Count != 1 ||
            !string.Equals(requestedProtocols[0], RealtimeProtocol.Name, StringComparison.Ordinal))
        {
            return Failure(
                boundary.Source,
                StatusCodes.Status426UpgradeRequired,
                "subprotocol_required",
                $"The Sec-WebSocket-Protocol header must include {RealtimeProtocol.Name}.");
        }

        // Browser clients authenticate with the first session.authenticate event. Native
        // WebSocket APIs cannot set Authorization, and an Origin must never weaken that gate.
        if (context.Request.Headers.Origin.Count > 0)
        {
            return boundary;
        }

        var bearer = ValidateBearer(context.Request.Headers.Authorization);
        return bearer switch
        {
            BearerValidation.Invalid => Failure(
                boundary.Source,
                StatusCodes.Status401Unauthorized,
                "invalid_api_key",
                "The Bearer API key is invalid."),
            BearerValidation.Unavailable => Failure(
                boundary.Source,
                StatusCodes.Status503ServiceUnavailable,
                "api_key_store_unavailable",
                "The API key store is unavailable."),
            _ => boundary with { PreAuthenticated = bearer == BearerValidation.Valid }
        };
    }

    private RealtimeRequestValidationResult ValidateLocalBoundary(HttpContext context)
    {
        var remoteAddress = NormalizeAddress(context.Connection.RemoteIpAddress);
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return Failure(
                remoteAddress?.ToString() ?? string.Empty,
                StatusCodes.Status403Forbidden,
                "realtime_remote_disabled",
                "Realtime protocol v1 is restricted to loopback clients.");
        }

        var localAddress = NormalizeAddress(context.Connection.LocalIpAddress);
        if (localAddress is null || !IPAddress.IsLoopback(localAddress))
        {
            return Failure(
                remoteAddress.ToString(),
                StatusCodes.Status403Forbidden,
                "realtime_remote_disabled",
                "Realtime protocol v1 requires a loopback listener and loopback client.");
        }

        var source = remoteAddress.ToString();
        if (!IsLoopbackHost(context, localAddress))
        {
            return Failure(
                source,
                StatusCodes.Status403Forbidden,
                "host_not_allowed",
                "The request Host is not allowed for the loopback Realtime endpoint.");
        }

        if (!IsAllowedOrigin(context.Request))
        {
            return Failure(
                source,
                StatusCodes.Status403Forbidden,
                "origin_not_allowed",
                "The browser Origin must exactly match the loopback request origin.");
        }

        return new RealtimeRequestValidationResult(true, source, false, StatusCodes.Status200OK, null, null);
    }

    private BearerValidation ValidateBearer(StringValues values)
    {
        if (values.Count == 0)
        {
            return BearerValidation.Missing;
        }

        if (values.Count != 1)
        {
            return BearerValidation.Invalid;
        }

        var header = values[0];
        if (string.IsNullOrWhiteSpace(header) || header.Length > MaxAuthorizationHeaderLength ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BearerValidation.Invalid;
        }

        var key = header[7..].Trim();
        if (key.Length == 0 || key.Contains(' '))
        {
            return BearerValidation.Invalid;
        }

        try
        {
            return apiKeys.ValidateKey(key) ? BearerValidation.Valid : BearerValidation.Invalid;
        }
        catch
        {
            return BearerValidation.Unavailable;
        }
    }

    private static bool IsAllowedOrigin(HttpRequest request)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1 || string.IsNullOrWhiteSpace(origins[0]) || origins[0]!.Contains(','))
        {
            return false;
        }

        if (!Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/")
        {
            return false;
        }

        var requestPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
               origin.Port == requestPort;
    }

    private static bool IsLoopbackHost(HttpContext context, IPAddress localAddress)
    {
        var host = context.Request.Host;
        if (!host.HasValue || string.IsNullOrWhiteSpace(host.Host))
        {
            return false;
        }

        if (string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostValue = host.Host;
        if (hostValue.Length > 2 && hostValue[0] == '[' && hostValue[^1] == ']')
        {
            hostValue = hostValue[1..^1];
        }

        if (!IPAddress.TryParse(hostValue, out var address))
        {
            return false;
        }

        var normalized = NormalizeAddress(address)!;
        return IPAddress.IsLoopback(normalized) && normalized.Equals(localAddress);
    }

    private static IPAddress? NormalizeAddress(IPAddress? address)
        => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private static bool ContainsForbiddenQuery(QueryString query)
        // Protocol v1 defines no query parameters. Rejecting the entire query
        // surface also covers credentials hidden under arbitrary parameter names.
        => query.HasValue;

    private static RealtimeRequestValidationResult Failure(
        string source,
        int statusCode,
        string code,
        string message)
        => new(false, source, false, statusCode, code, message);

    private enum BearerValidation
    {
        Missing,
        Valid,
        Invalid,
        Unavailable
    }
}
