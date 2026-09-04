using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeHttpLoggingInterceptorTests
{
    [Theory]
    [InlineData("/api/realtime")]
    [InlineData("/api/realtime/")]
    [InlineData("/api/realtime/status")]
    [InlineData("/API/REALTIME/STATUS/")]
    [InlineData("/api/realtime/tickets")]
    [InlineData("/api/realtime/v1")]
    [InlineData("/api/realtime/v1/")]
    public void SafeRealtimePathsRemainLoggable(string path)
    {
        Assert.False(RealtimeHttpLoggingInterceptor.ShouldSuppressRequestPath(path));
    }

    [Theory]
    [InlineData("/api/realtime/v1/rtt_sensitive")]
    [InlineData("/api/realtime/tickets/tmr_sensitive")]
    [InlineData("/api/realtime/session_token/sensitive")]
    [InlineData("/api/realtime/unknown")]
    [InlineData("/api/realtime%2Fv1/session-token-value")]
    [InlineData("/api/realtime\\v1\\session-token-value")]
    [InlineData("/elsewhere/rtt_sensitive")]
    [InlineData("/elsewhere/TMR%5Fsensitive")]
    public void SuspectedCredentialPathsAreSuppressed(string path)
    {
        Assert.True(RealtimeHttpLoggingInterceptor.ShouldSuppressRequestPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/api/realtime-v1/rtt-reference")]
    [InlineData("/api/models/installed")]
    public void UnrelatedPathsRemainLoggable(string? path)
    {
        Assert.False(RealtimeHttpLoggingInterceptor.ShouldSuppressRequestPath(path));
    }
}
