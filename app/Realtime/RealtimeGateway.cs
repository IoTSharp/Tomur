using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tomur.Api;

namespace Tomur.Realtime;

internal sealed class RealtimeGateway
{
    private readonly RealtimeRequestValidator requests;
    private readonly RealtimeTicketStore tickets;
    private readonly RealtimeSessionRegistry sessions;
    private readonly ILoggerFactory loggerFactory;

    public RealtimeGateway(
        RealtimeRequestValidator requests,
        RealtimeTicketStore tickets,
        RealtimeSessionRegistry sessions,
        ILoggerFactory loggerFactory)
    {
        this.requests = requests;
        this.tickets = tickets;
        this.sessions = sessions;
        this.loggerFactory = loggerFactory;
    }

    public async Task WriteStatusAsync(HttpContext context)
    {
        var validation = requests.ValidateLocalRequest(context);
        if (!validation.Success)
        {
            await WriteHttpErrorAsync(context, validation).ConfigureAwait(false);
            return;
        }

        var snapshot = sessions.GetSnapshot();
        var response = new RealtimeStatusResponse(
            snapshot.ActiveSessions > 0 ? "active" : "available_unverified",
            RealtimeProtocol.Name,
            RealtimeProtocol.WebSocketPath,
            RealtimeProtocol.TicketPath,
            new RealtimeCapabilityStatus(
                "available_unverified",
                "unavailable",
                "not_connected",
                "not_connected",
                "not_connected",
                "not_implemented",
                "pending"),
            snapshot,
            RealtimeLimitsResponse.Create());
        context.Response.Headers.CacheControl = "no-store";
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            RealtimeJsonSerializerContext.Default.RealtimeStatusResponse).ConfigureAwait(false);
    }

    public async Task IssueTicketAsync(HttpContext context)
    {
        var validation = requests.ValidateTicketRequest(context);
        if (!validation.Success)
        {
            await WriteHttpErrorAsync(context, validation).ConfigureAwait(false);
            return;
        }

        if (!tickets.TryIssue(validation.Source, out var issue, out var issueError))
        {
            var capacityLimited = issueError is
                "ticket_source_limit_reached" or
                "ticket_capacity_exceeded";
            var message = issueError switch
            {
                "ticket_source_limit_reached" =>
                    "The Realtime ticket limit for this source was reached. Try again after an existing ticket expires.",
                "ticket_capacity_exceeded" =>
                    "The bounded Realtime ticket store is full. Try again after existing tickets expire.",
                _ => "A Realtime ticket could not be generated."
            };
            context.Response.Headers.CacheControl = "no-store";
            await JsonHttpResponse.WriteAsync(
                context,
                new RealtimeHttpError(issueError!, message),
                RealtimeJsonSerializerContext.Default.RealtimeHttpError,
                capacityLimited
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await JsonHttpResponse.WriteAsync(
            context,
            new RealtimeTicketResponse(
                issue!.Ticket,
                issue.ExpiresAt,
                checked((int)RealtimeProtocol.TicketLifetime.TotalSeconds),
                RealtimeProtocol.Name,
                RealtimeProtocol.WebSocketPath,
                "session.authenticate"),
            RealtimeJsonSerializerContext.Default.RealtimeTicketResponse,
            StatusCodes.Status200OK).ConfigureAwait(false);
    }

    public async Task AcceptAsync(HttpContext context)
    {
        var validation = requests.ValidateUpgrade(context);
        if (!validation.Success)
        {
            await WriteHttpErrorAsync(context, validation).ConfigureAwait(false);
            return;
        }

        if (!sessions.TryReserve(validation.Source, out var lease, out var reservationError))
        {
            var capacityLimited = reservationError is
                "connection_limit_reached" or
                "source_connection_limit_reached";
            var message = capacityLimited
                ? "The bounded Realtime connection quota has been reached."
                : "A Realtime connection reservation could not be created.";
            context.Response.Headers.CacheControl = "no-store";
            await JsonHttpResponse.WriteAsync(
                context,
                new RealtimeHttpError(
                    reservationError!,
                    message),
                RealtimeJsonSerializerContext.Default.RealtimeHttpError,
                capacityLimited
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
            return;
        }

        using (lease)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(RealtimeProtocol.Name).ConfigureAwait(false);
            var connection = new RealtimeConnection(
                tickets,
                loggerFactory.CreateLogger<RealtimeConnection>());
            await connection.RunAsync(
                socket,
                lease!,
                validation.PreAuthenticated,
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task WriteHttpErrorAsync(
        HttpContext context,
        RealtimeRequestValidationResult validation)
    {
        if (validation.StatusCode == StatusCodes.Status426UpgradeRequired)
        {
            context.Response.Headers.Upgrade = "websocket";
            context.Response.Headers["Sec-WebSocket-Protocol"] = RealtimeProtocol.Name;
        }

        context.Response.Headers.CacheControl = "no-store";
        await JsonHttpResponse.WriteAsync(
            context,
            new RealtimeHttpError(validation.ErrorCode!, validation.ErrorMessage!),
            RealtimeJsonSerializerContext.Default.RealtimeHttpError,
            validation.StatusCode).ConfigureAwait(false);
    }
}
