using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tomur.Storage;

namespace Tomur.Realtime;

internal static class RealtimeRouteExtensions
{
    public static IServiceCollection AddRealtimeGateway(this IServiceCollection services)
    {
        services.AddHttpLoggingInterceptor<RealtimeHttpLoggingInterceptor>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ApiKeyStore>();
        services.AddSingleton<RealtimeTicketStore>();
        services.AddSingleton<RealtimeSessionRegistry>();
        services.AddSingleton<RealtimeRequestValidator>();
        services.AddSingleton<RealtimeGateway>();
        return services;
    }

    public static WebApplication UseRealtimeGateway(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        });

        app.MapGet(RealtimeProtocol.StatusPath, static (HttpContext context, RealtimeGateway gateway)
            => gateway.WriteStatusAsync(context));
        app.MapPost(RealtimeProtocol.TicketPath, static (HttpContext context, RealtimeGateway gateway)
            => gateway.IssueTicketAsync(context));
        app.MapGet(RealtimeProtocol.WebSocketPath, static (HttpContext context, RealtimeGateway gateway)
            => gateway.AcceptAsync(context));
        return app;
    }
}
