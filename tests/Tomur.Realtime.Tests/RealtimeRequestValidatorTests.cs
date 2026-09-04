using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Tomur.Config;
using Tomur.Realtime;
using Tomur.Storage;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeRequestValidatorTests
{
    [Fact]
    public void BrowserTicketRequestRequiresExactLoopbackOrigin()
    {
        var validator = CreateValidator();
        var accepted = CreateContext(origin: "http://127.0.0.1:5137");

        var success = validator.ValidateTicketRequest(accepted);

        Assert.True(success.Success);
        Assert.Equal("127.0.0.1", success.Source);
        Assert.False(success.PreAuthenticated);

        var rejected = CreateContext(origin: "http://127.0.0.1:5173");
        var failure = validator.ValidateTicketRequest(rejected);
        Assert.False(failure.Success);
        Assert.Equal(StatusCodes.Status403Forbidden, failure.StatusCode);
        Assert.Equal("origin_not_allowed", failure.ErrorCode);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5137")]
    [InlineData("http://localhost:5137")]
    [InlineData("http://127.0.0.1:5137/path")]
    [InlineData("null")]
    public void TicketRequestRejectsOriginSchemeHostPortOrShapeMismatch(string origin)
    {
        var result = CreateValidator().ValidateTicketRequest(CreateContext(origin));

        Assert.False(result.Success);
        Assert.Equal("origin_not_allowed", result.ErrorCode);
    }

    [Fact]
    public void TicketRequestRejectsMultipleOrigins()
    {
        var context = CreateContext(origin: null);
        context.Request.Headers.Origin = new[]
        {
            "http://127.0.0.1:5137",
            "http://localhost:5137"
        };

        var result = CreateValidator().ValidateTicketRequest(context);

        Assert.False(result.Success);
        Assert.Equal("origin_not_allowed", result.ErrorCode);
    }

    [Fact]
    public void NonBrowserTicketRequestRequiresBearerCredential()
    {
        var context = CreateContext(origin: null);

        var result = CreateValidator().ValidateTicketRequest(context);

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("authentication_required", result.ErrorCode);
    }

    [Fact]
    public void UpgradeRequiresWebSocketAndExactVersionedSubprotocol()
    {
        var validator = CreateValidator();
        var plainHttp = CreateContext(origin: "http://127.0.0.1:5137");
        var noUpgrade = validator.ValidateUpgrade(plainHttp);
        Assert.False(noUpgrade.Success);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, noUpgrade.StatusCode);
        Assert.Equal("websocket_required", noUpgrade.ErrorCode);

        var missingProtocol = CreateUpgradeContext(protocol: null);
        var missing = validator.ValidateUpgrade(missingProtocol);
        Assert.False(missing.Success);
        Assert.Equal("subprotocol_required", missing.ErrorCode);

        var wrongCase = CreateUpgradeContext("TOMUR.REALTIME.V1");
        var mismatch = validator.ValidateUpgrade(wrongCase);
        Assert.False(mismatch.Success);
        Assert.Equal("subprotocol_required", mismatch.ErrorCode);

        var mixed = CreateUpgradeContext($"{RealtimeProtocol.Name}, future.unsupported");
        var mixedResult = validator.ValidateUpgrade(mixed);
        Assert.False(mixedResult.Success);
        Assert.Equal("subprotocol_required", mixedResult.ErrorCode);

        var accepted = validator.ValidateUpgrade(CreateUpgradeContext(RealtimeProtocol.Name));
        Assert.True(accepted.Success);
        Assert.False(accepted.PreAuthenticated);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("access_token")]
    [InlineData("session_token")]
    [InlineData("reconnect_token")]
    [InlineData("token")]
    [InlineData("api_key")]
    [InlineData("authorization")]
    public void UpgradeRejectsCredentialsInQuery(string key)
    {
        var context = CreateUpgradeContext(RealtimeProtocol.Name);
        context.Request.QueryString = new QueryString($"?{key}=secret");

        var result = CreateValidator().ValidateUpgrade(context);

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("credential_in_query_forbidden", result.ErrorCode);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("?source=rtt_sensitive_ticket")]
    [InlineData("?diagnostic=true")]
    public void RealtimeRoutesRejectArbitraryQueryParameters(string query)
    {
        var upgradeContext = CreateUpgradeContext(RealtimeProtocol.Name);
        upgradeContext.Request.QueryString = new QueryString(query);
        var ticketContext = CreateContext(origin: "http://127.0.0.1:5137");
        ticketContext.Request.QueryString = new QueryString(query);
        var statusContext = CreateContext(origin: null);
        statusContext.Request.QueryString = new QueryString(query);

        var validator = CreateValidator();
        var results = new[]
        {
            validator.ValidateUpgrade(upgradeContext),
            validator.ValidateTicketRequest(ticketContext),
            validator.ValidateLocalRequest(statusContext)
        };

        Assert.All(results, result =>
        {
            Assert.False(result.Success);
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Equal("credential_in_query_forbidden", result.ErrorCode);
        });
    }

    [Fact]
    public void UpgradeRejectsMalformedAuthorizationWithoutReadingKeyStore()
    {
        var context = CreateUpgradeContext(RealtimeProtocol.Name);
        context.Request.Headers.Authorization = "Basic secret";

        var result = CreateValidator().ValidateUpgrade(context);

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("invalid_api_key", result.ErrorCode);
    }

    [Fact]
    public void BrowserUpgradeAlwaysUsesFirstEventTicketAuthentication()
    {
        var context = CreateUpgradeContext(
            RealtimeProtocol.Name,
            origin: "http://127.0.0.1:5137");
        context.Request.Headers.Authorization = "Bearer ignored-for-browser-gate";

        var result = CreateValidator().ValidateUpgrade(context);

        Assert.True(result.Success);
        Assert.False(result.PreAuthenticated);
    }

    [Fact]
    public void LoopbackDevelopmentProxyMayUseItsClientVisiblePort()
    {
        var context = CreateContext(origin: "http://127.0.0.1:5173");
        context.Request.Host = new HostString("127.0.0.1", 5173);

        var result = CreateValidator().ValidateTicketRequest(context);

        Assert.True(result.Success);
        Assert.False(result.PreAuthenticated);
    }

    [Fact]
    public void BoundaryRejectsRemoteAddressAndNonLoopbackHost()
    {
        var validator = CreateValidator();
        var remote = CreateContext(origin: null);
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        var remoteResult = validator.ValidateTicketRequest(remote);
        Assert.False(remoteResult.Success);
        Assert.Equal("realtime_remote_disabled", remoteResult.ErrorCode);

        var wrongHost = CreateContext(origin: null);
        wrongHost.Request.Host = new HostString("example.test", 5137);
        var hostResult = validator.ValidateTicketRequest(wrongHost);
        Assert.False(hostResult.Success);
        Assert.Equal("host_not_allowed", hostResult.ErrorCode);

        var wrongListener = CreateContext(origin: null);
        wrongListener.Connection.LocalIpAddress = IPAddress.Any;
        var listenerResult = validator.ValidateLocalRequest(wrongListener);
        Assert.False(listenerResult.Success);
        Assert.Equal("realtime_remote_disabled", listenerResult.ErrorCode);
    }

    private static RealtimeRequestValidator CreateValidator()
    {
        var paths = new DataPaths(new PathOptions
        {
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "realtime-validator-unused")
        });
        return new RealtimeRequestValidator(
            new ApiKeyStore(new LocalDatabaseInitializer(paths)));
    }

    private static DefaultHttpContext CreateContext(string? origin)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5137;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5137);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        return context;
    }

    private static DefaultHttpContext CreateUpgradeContext(
        string? protocol,
        string? origin = null)
    {
        var context = CreateContext(origin);
        context.Features.Set<IHttpWebSocketFeature>(new StubWebSocketFeature());
        if (protocol is not null)
        {
            context.Request.Headers["Sec-WebSocket-Protocol"] = protocol;
        }

        return context;
    }

    private sealed class StubWebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => throw new NotSupportedException("The validator must not accept the socket.");
    }
}
