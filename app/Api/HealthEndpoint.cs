using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tomur.Serialization;

namespace Tomur.Api;

public static class HealthEndpoint
{
    public static readonly HealthCheckOptions Options = new()
    {
        ResponseWriter = static async (context, report) =>
        {
            var response = new HealthResponse(
                report.Status.ToString(),
                report.TotalDuration.TotalMilliseconds,
                DateTimeOffset.UtcNow);

            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.HealthResponse,
                ToStatusCode(report.Status));
        }
    };

    private static int ToStatusCode(HealthStatus status)
    {
        return status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
    }
}
