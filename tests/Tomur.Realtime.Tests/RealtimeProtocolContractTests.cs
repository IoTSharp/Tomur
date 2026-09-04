using System.Text;
using System.Text.Json;
using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeProtocolContractTests
{
    [Fact]
    public void ProtocolConstantsFreezeVersionedRoutesAndPcmBaseline()
    {
        Assert.Equal("tomur.realtime.v1", RealtimeProtocol.Name);
        Assert.Equal("/api/realtime/v1", RealtimeProtocol.WebSocketPath);
        Assert.Equal("/api/realtime/tickets", RealtimeProtocol.TicketPath);
        Assert.Equal("/api/realtime/status", RealtimeProtocol.StatusPath);
        Assert.Equal(1, RealtimeProtocol.Version);
        Assert.Equal(44, RealtimeProtocol.BinaryHeaderSize);
        Assert.Equal(16_000, RealtimeProtocol.InputSampleRate);
        Assert.Equal(1, RealtimeProtocol.InputChannels);
        Assert.Equal(20, RealtimeProtocol.InputFrameDurationMilliseconds);
        Assert.Equal(640, RealtimeProtocol.InputFramePayloadBytes);
        Assert.Equal(24_000, RealtimeProtocol.OutputSampleRate);
        Assert.Equal(1, RealtimeProtocol.OutputChannels);
        Assert.Equal(16 * 1024, RealtimeProtocol.MaxJsonMessageBytes);
        Assert.Equal(32, RealtimeProtocol.MaxMessageFragments);
        Assert.Equal(32, RealtimeProtocol.MaxCloseHandshakeReceiveIterations);
        Assert.Equal(64, RealtimeProtocol.InboundQueueCapacity);
        Assert.Equal(64, RealtimeProtocol.OutboundControlQueueCapacity);
        Assert.Equal(960_000, RealtimeProtocol.MaxInputAudioBytes);
        Assert.Equal(100, RealtimeProtocol.MaxEventsPerSecond);
        Assert.Equal(50_000, RealtimeProtocol.MaxEventsPerSession);
        Assert.Equal(8, RealtimeProtocol.MaxPendingConnections);
        Assert.Equal(2, RealtimeProtocol.MaxPendingConnectionsPerSource);
        Assert.Equal(1, RealtimeProtocol.MaxActiveSessions);
        Assert.Equal(128, RealtimeProtocol.MaxTickets);
        Assert.Equal(16, RealtimeProtocol.MaxTicketsPerSource);
        Assert.Equal(TimeSpan.FromSeconds(2), RealtimeProtocol.GracefulCloseTimeout);

        var limits = RealtimeLimitsResponse.Create();
        Assert.Equal(RealtimeProtocol.MaxPendingConnections, limits.MaximumPendingConnections);
        Assert.Equal(RealtimeProtocol.MaxPendingConnectionsPerSource, limits.MaximumPendingConnectionsPerSource);
        Assert.Equal(RealtimeProtocol.MaxTickets, limits.MaximumTickets);
        Assert.Equal(RealtimeProtocol.MaxTicketsPerSource, limits.MaximumTicketsPerSource);
        Assert.Equal(30, limits.TicketLifetimeSeconds);
        Assert.Equal(2_000, limits.GracefulCloseTimeoutMilliseconds);
    }

    [Fact]
    public void SourceGeneratedClientEventUsesSnakeCaseAndRoundTrips()
    {
        var expected = new RealtimeAuthenticateEvent(
            "session.authenticate",
            "event.auth_1",
            7,
            123_456,
            "rtt_ticket");

        var json = JsonSerializer.Serialize(
            expected,
            RealtimeJsonSerializerContext.Default.RealtimeAuthenticateEvent);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("event.auth_1", root.GetProperty("event_id").GetString());
        Assert.Equal(123_456, root.GetProperty("timestamp_us").GetInt64());
        Assert.False(root.TryGetProperty("eventId", out _));
        Assert.False(root.TryGetProperty("timestampUs", out _));

        var actual = JsonSerializer.Deserialize(
            json,
            RealtimeJsonSerializerContext.Default.RealtimeAuthenticateEvent);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SourceGeneratedTicketResponseExposesTheFrozenAuthenticationFlow()
    {
        var value = new RealtimeTicketResponse(
            "rtt_sensitive",
            new DateTimeOffset(2026, 9, 5, 12, 0, 30, TimeSpan.Zero),
            30,
            RealtimeProtocol.Name,
            RealtimeProtocol.WebSocketPath,
            "session.authenticate");

        var json = JsonSerializer.Serialize(
            value,
            RealtimeJsonSerializerContext.Default.RealtimeTicketResponse);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(30, root.GetProperty("expires_in_seconds").GetInt32());
        Assert.Equal(RealtimeProtocol.Name, root.GetProperty("protocol").GetString());
        Assert.Equal(RealtimeProtocol.WebSocketPath, root.GetProperty("web_socket_path").GetString());
        Assert.Equal("session.authenticate", root.GetProperty("authenticate_event_type").GetString());
    }

    [Fact]
    public void SourceGeneratedServerErrorOmitsNullScopedIdentifiers()
    {
        var value = new RealtimeErrorEvent(
            "error",
            "event.error_1",
            1,
            0,
            null,
            "trace_1",
            "connecting",
            "invalid_event",
            "Invalid control event.",
            true);

        var json = JsonSerializer.Serialize(
            value,
            RealtimeJsonSerializerContext.Default.RealtimeErrorEvent);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("event.error_1", root.GetProperty("event_id").GetString());
        Assert.Equal("trace_1", root.GetProperty("trace_id").GetString());
        Assert.False(root.TryGetProperty("session_id", out _));
        Assert.False(root.TryGetProperty("response_epoch", out _));
        Assert.False(root.TryGetProperty("capture_stream_id", out _));
    }

    [Fact]
    public void ParserAcceptsKnownEventWithStableIdentityFields()
    {
        var result = Parse(
            """
            {
              "type": "session.ping",
              "event_id": "event.ping_1",
              "sequence": 8,
              "timestamp_us": 987654
            }
            """);

        Assert.True(result.Success);
        Assert.Null(result.ErrorCode);
        var ping = Assert.IsType<RealtimePingEvent>(result.Event);
        Assert.Equal("session.ping", ping.Type);
        Assert.Equal("event.ping_1", ping.EventId);
        Assert.Equal(8, ping.Sequence);
        Assert.Equal(987_654, ping.TimestampUs);
    }

    [Fact]
    public void ParserRejectsUnknownFieldsInsteadOfIgnoringThem()
    {
        var result = Parse(
            """
            {
              "type": "session.ping",
              "event_id": "event.ping_1",
              "sequence": 1,
              "timestamp_us": 0,
              "unexpected": true
            }
            """);

        Assert.False(result.Success);
        Assert.Null(result.Event);
        Assert.Equal("invalid_event", result.ErrorCode);
    }

    [Fact]
    public void ParserRejectsUnknownEventTypeWithStableDiagnostic()
    {
        var result = Parse(
            """
            {
              "type": "session.future_event",
              "event_id": "event.future_1",
              "sequence": 1,
              "timestamp_us": 0
            }
            """);

        Assert.False(result.Success);
        Assert.Null(result.Event);
        Assert.Equal("unsupported_event", result.ErrorCode);
        Assert.Contains(RealtimeProtocol.Name, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidEventIds))]
    public void ParserRejectsInvalidEventIds(string eventId)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "session.ping",
            event_id = eventId,
            sequence = 1,
            timestamp_us = 0
        });

        var result = Parse(json);

        Assert.False(result.Success);
        Assert.Equal("invalid_event_id", result.ErrorCode);
    }

    [Fact]
    public void ParserAcceptsMaximumLengthAsciiEventId()
    {
        var eventId = new string('a', 64);
        var json = JsonSerializer.Serialize(new
        {
            type = "session.ping",
            event_id = eventId,
            sequence = 1,
            timestamp_us = 0
        });

        var result = Parse(json);

        Assert.True(result.Success);
        Assert.Equal(eventId, result.Event!.EventId);
    }

    [Theory]
    [InlineData(0, 0, "invalid_sequence")]
    [InlineData(-1, 0, "invalid_sequence")]
    [InlineData(1, -1, "invalid_timestamp")]
    public void ParserRejectsInvalidSequenceOrTimestamp(
        long sequence,
        long timestampUs,
        string expectedCode)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "session.ping",
            event_id = "event.ping_1",
            sequence,
            timestamp_us = timestampUs
        });

        var result = Parse(json);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    public static IEnumerable<object[]> InvalidEventIds()
    {
        yield return [string.Empty];
        yield return ["contains space"];
        yield return ["contains:colon"];
        yield return ["unicode_\u4e8b\u4ef6"];
        yield return [new string('a', 65)];
    }

    private static RealtimeClientEventParseResult Parse(string json)
        => RealtimeClientEventParser.Parse(Encoding.UTF8.GetBytes(json));
}
