using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeConnectionTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScriptedCloseFramesFollowWebSocketStateTransitions()
    {
        using var peerInitiated = new ScriptedWebSocket(ScriptedWebSocketFrame.Close());
        var peerClose = await peerInitiated.ReceiveAsync(
                new byte[1].AsMemory(),
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WebSocketMessageType.Close, peerClose.MessageType);
        Assert.Equal(WebSocketState.CloseReceived, peerInitiated.State);

        using var serverInitiated = new ScriptedWebSocket(ScriptedWebSocketFrame.Close());
        await serverInitiated.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "server_closed",
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WebSocketState.CloseSent, serverInitiated.State);

        var closeResponse = await serverInitiated.ReceiveAsync(
                new byte[1].AsMemory(),
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WebSocketMessageType.Close, closeResponse.MessageType);
        Assert.Equal(WebSocketState.Closed, serverInitiated.State);
    }

    [Fact]
    public async Task ScriptedSocketRejectsConcurrentReceiveOperations()
    {
        using var socket = new ScriptedWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var firstReceive = socket.ReceiveAsync(new byte[1].AsMemory(), cancellation.Token).AsTask();

        try
        {
            var secondReceive = socket.ReceiveAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask();
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondReceive)
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstReceive)
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ScriptedInputExhaustionWaitsForServerClose()
    {
        using var socket = new ScriptedWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var receive = socket.ReceiveAsync(new byte[1].AsMemory(), cancellation.Token).AsTask();

        Assert.False(receive.IsCompleted);
        await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "server_closed",
                cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        var closeResponse = await receive.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WebSocketMessageType.Close, closeResponse.MessageType);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task ControlSequenceGapReturnsExpectedAndReceivedSequenceThenCloses()
    {
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"ping_2","sequence":2,"timestamp_us":10}"""));

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "error"], ReadTypes(events));
        Assert.Equal(1, events[0].GetProperty("sequence").GetInt64());
        Assert.Equal(2, events[1].GetProperty("sequence").GetInt64());
        Assert.Equal("control_sequence_mismatch", events[1].GetProperty("code").GetString());
        Assert.Equal(1, events[1].GetProperty("expected_sequence").GetInt64());
        Assert.Equal(2, events[1].GetProperty("received_sequence").GetInt64());
        Assert.True(events[1].GetProperty("fatal").GetBoolean());
        Assert.Equal((int)RealtimeCloseCode.ProtocolError, (int)socket.CloseStatus!.Value);
        Assert.Equal("control_sequence_mismatch", socket.CloseStatusDescription);
    }

    [Fact]
    public async Task DuplicateEventIdIsRejectedAfterFirstEventIsAcknowledged()
    {
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"same_id","sequence":1,"timestamp_us":10}"""),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"same_id","sequence":2,"timestamp_us":11}"""));

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "session.pong", "error"], ReadTypes(events));
        Assert.Equal("same_id", events[1].GetProperty("client_event_id").GetString());
        Assert.Equal("duplicate_event_id", events[2].GetProperty("code").GetString());
        Assert.Equal((int)RealtimeCloseCode.ProtocolError, (int)socket.CloseStatus!.Value);
    }

    [Fact]
    public async Task FirstAudioFrameMustStartAtSequenceOne()
    {
        var captureStreamId = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Binary(CreateInputAudioFrame(captureStreamId, 2, 0)));

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "error"], ReadTypes(events));
        var error = events[1];
        Assert.Equal("audio_sequence_mismatch", error.GetProperty("code").GetString());
        Assert.Equal(1, error.GetProperty("expected_sequence").GetInt64());
        Assert.Equal(2, error.GetProperty("received_sequence").GetInt64());
        Assert.Equal(captureStreamId.ToString("N"), error.GetProperty("capture_stream_id").GetString());
        Assert.Equal((int)RealtimeCloseCode.ProtocolError, (int)socket.CloseStatus!.Value);
    }

    [Fact]
    public async Task AudioAndControlSequencesAreIndependentAcrossManualCommit()
    {
        var captureStreamId = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Binary(CreateInputAudioFrame(captureStreamId, 1, 10_000)),
            ScriptedWebSocketFrame.Text($$"""
                {"type":"input_audio_buffer.commit","event_id":"commit_1","sequence":1,"timestamp_us":10001,"capture_stream_id":"{{captureStreamId:N}}","utterance_id":null}
                """),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_2","sequence":2,"timestamp_us":10002,"reason":"user_requested"}"""));

        var events = ReadEvents(socket);
        Assert.Equal(
            [
                "session.created",
                "input_audio_buffer.started",
                "input_audio_buffer.committed",
                "error",
                "session.closed"
            ],
            ReadTypes(events));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], ReadSequences(events));
        Assert.Equal("user_speaking", events[1].GetProperty("state").GetString());
        Assert.Equal(1UL, events[1].GetProperty("first_sequence").GetUInt64());
        Assert.Equal("transcribing", events[2].GetProperty("state").GetString());
        Assert.Equal(RealtimeProtocol.InputFramePayloadBytes, events[2].GetProperty("buffered_audio_bytes").GetInt32());
        Assert.Equal(RealtimeProtocol.InputFrameDurationMilliseconds, events[2].GetProperty("duration_ms").GetInt32());
        Assert.Equal("realtime_pipeline_unavailable", events[3].GetProperty("code").GetString());
        Assert.Equal("listening", events[3].GetProperty("state").GetString());
        Assert.Equal("user_requested", events[4].GetProperty("reason").GetString());
        Assert.Equal(3L, events[4].GetProperty("received_events").GetInt64());
        Assert.Equal(1L, events[4].GetProperty("received_audio_frames").GetInt64());
        Assert.Equal((long)RealtimeProtocol.InputFramePayloadBytes, events[4].GetProperty("received_audio_bytes").GetInt64());
        Assert.Equal(1L, events[4].GetProperty("committed_utterances").GetInt64());
        Assert.Equal(0L, events[4].GetProperty("discarded_audio_frames").GetInt64());
        Assert.Equal(0L, events[4].GetProperty("discarded_audio_bytes").GetInt64());
        Assert.Equal((int)System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, (int)socket.CloseStatus!.Value);
    }

    [Fact]
    public async Task ClearReportsAndAccumulatesDiscardedAudio()
    {
        var captureStreamId = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Binary(CreateInputAudioFrame(captureStreamId, 1, 10_000)),
            ScriptedWebSocketFrame.Binary(CreateInputAudioFrame(captureStreamId, 2, 30_000)),
            ScriptedWebSocketFrame.Text($$"""
                {"type":"input_audio_buffer.clear","event_id":"clear_1","sequence":1,"timestamp_us":30001,"capture_stream_id":"{{captureStreamId:N}}"}
                """),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_2","sequence":2,"timestamp_us":30002,"reason":"done"}"""));

        var events = ReadEvents(socket);
        Assert.Equal(
            ["session.created", "input_audio_buffer.started", "input_audio_buffer.cleared", "session.closed"],
            ReadTypes(events));
        Assert.Equal(2, events[2].GetProperty("discarded_audio_frames").GetInt32());
        Assert.Equal(
            RealtimeProtocol.InputFramePayloadBytes * 2,
            events[2].GetProperty("discarded_audio_bytes").GetInt32());
        Assert.Equal(4L, events[3].GetProperty("received_events").GetInt64());
        Assert.Equal(2L, events[3].GetProperty("received_audio_frames").GetInt64());
        Assert.Equal(2L, events[3].GetProperty("discarded_audio_frames").GetInt64());
        Assert.Equal(
            (long)RealtimeProtocol.InputFramePayloadBytes * 2,
            events[3].GetProperty("discarded_audio_bytes").GetInt64());
    }

    [Fact]
    public async Task CancelAndAcknowledgementsRemainScopedToResponseEpoch()
    {
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Text(
                """{"type":"response.cancel","event_id":"cancel_1","sequence":1,"timestamp_us":1,"response_epoch":9}"""),
            ScriptedWebSocketFrame.Text(
                """{"type":"response.text.displayed","event_id":"displayed_2","sequence":2,"timestamp_us":2,"response_epoch":9,"item_id":"item_1","character_count":5}"""),
            ScriptedWebSocketFrame.Text(
                """{"type":"response.audio.playback_consumed","event_id":"played_3","sequence":3,"timestamp_us":3,"response_epoch":9,"audio_sequence":4,"played_through_timestamp_us":20000}"""),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_4","sequence":4,"timestamp_us":4,"reason":"done"}"""));

        var events = ReadEvents(socket);
        Assert.Equal(
            ["session.created", "response.cancelled", "error", "error", "session.closed"],
            ReadTypes(events));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], ReadSequences(events));

        Assert.Equal(9, events[1].GetProperty("response_epoch").GetInt64());
        Assert.Equal("not_active", events[1].GetProperty("reason").GetString());
        Assert.Equal("response_not_active", events[2].GetProperty("code").GetString());
        Assert.Equal(9, events[2].GetProperty("response_epoch").GetInt64());
        Assert.False(events[2].GetProperty("fatal").GetBoolean());
        Assert.Equal("response_not_active", events[3].GetProperty("code").GetString());
        Assert.Equal(9, events[3].GetProperty("response_epoch").GetInt64());
        Assert.False(events[3].GetProperty("fatal").GetBoolean());
    }

    [Fact]
    public async Task SessionUpdateRejectsOverlongWhitespaceConversationId()
    {
        var update = JsonSerializer.Serialize(new
        {
            type = "session.update",
            event_id = "update_1",
            sequence = 1,
            timestamp_us = 0,
            session = new
            {
                conversation_id = new string(' ', 129),
                turn_detection = "manual",
                input_audio_format = "pcm16le",
                input_sample_rate = RealtimeProtocol.InputSampleRate,
                input_channels = RealtimeProtocol.InputChannels,
                input_frame_duration_ms = RealtimeProtocol.InputFrameDurationMilliseconds,
                output_audio_format = "pcm16le",
                output_sample_rate = RealtimeProtocol.OutputSampleRate,
                output_channels = RealtimeProtocol.OutputChannels
            }
        });
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Text(update),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_2","sequence":2,"timestamp_us":1,"reason":"user_requested"}"""));

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "error", "session.closed"], ReadTypes(events));
        Assert.Equal("invalid_session_configuration", events[1].GetProperty("code").GetString());
        Assert.False(events[1].GetProperty("fatal").GetBoolean());
    }

    [Fact]
    public async Task MissingFirstAuthenticationEventFailsWithoutActivatingSession()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = new ScriptedWebSocket(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"ping_1","sequence":1,"timestamp_us":0}"""));

        await RunAsync(socket, reservation, registry, preAuthenticated: false);

        var error = Assert.Single(ReadEvents(socket));
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("authentication_required", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("session_id", out _));
        Assert.Equal((int)RealtimeCloseCode.AuthenticationFailed, (int)socket.CloseStatus!.Value);
        Assert.Equal(1, socket.ReceiveCallsAfterCloseOutput);
        Assert.False(socket.WasAborted);
    }

    [Fact]
    public async Task CloseHandshakeReceiveStopsAtTheIterationLimitThenAborts()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var frames = new[]
        {
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"ping_1","sequence":1,"timestamp_us":0}""")
        }.Concat(
            Enumerable.Range(0, RealtimeProtocol.MaxCloseHandshakeReceiveIterations)
                .Select(static _ => ScriptedWebSocketFrame.Text("{}")))
            .ToArray();
        var socket = new ScriptedWebSocket(frames);

        await RunAsync(socket, reservation, registry, preAuthenticated: false);

        Assert.Equal(
            RealtimeProtocol.MaxCloseHandshakeReceiveIterations,
            socket.ReceiveCallsAfterCloseOutput);
        Assert.Equal((int)RealtimeCloseCode.AuthenticationFailed, (int)socket.CloseStatus!.Value);
        Assert.True(socket.WasAborted);
    }

    [Fact]
    public async Task InFlightApplicationReceiveReservesOneCloseHandshakeIteration()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var frames = new[]
        {
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_1","sequence":1,"timestamp_us":0,"reason":"done"}"""),
            ScriptedWebSocketFrame.TextAfterServerClose("{}")
        }.Concat(
            Enumerable.Range(0, RealtimeProtocol.MaxCloseHandshakeReceiveIterations - 1)
                .Select(static _ => ScriptedWebSocketFrame.Text("{}")))
            .ToArray();
        var socket = new ScriptedWebSocket(frames);
        var logger = new ActivationLogBarrier();
        var runTask = Task.Run(() => RunAsync(
            socket,
            reservation,
            registry,
            preAuthenticated: true,
            logger: logger));

        try
        {
            await logger.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            await socket.ApplicationReceiveWaitingForServerClose.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            logger.Release();
            await runTask;
        }

        Assert.Equal(
            RealtimeProtocol.MaxCloseHandshakeReceiveIterations - 1,
            socket.ReceiveCallsAfterCloseOutput);
        Assert.Equal(
            RealtimeProtocol.MaxCloseHandshakeReceiveIterations,
            1 + socket.ReceiveCallsAfterCloseOutput);
        Assert.Equal(1, socket.CloseOutputCalls);
        Assert.True(socket.WasAborted);
    }

    [Fact]
    public async Task SilentPeerIsCanceledByTheGracefulCloseDeadlineThenAborted()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = ScriptedWebSocket.CreateSilentPeer(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_1","sequence":1,"timestamp_us":0,"reason":"done"}"""));
        using var requestDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var elapsed = Stopwatch.StartNew();

        await RunAsync(
            socket,
            reservation,
            registry,
            preAuthenticated: true,
            requestAborted: requestDeadline.Token);
        elapsed.Stop();

        Assert.Equal(["session.created", "session.closed"], ReadTypes(ReadEvents(socket)));
        Assert.Equal(1, socket.CloseOutputCalls);
        Assert.True(socket.SilentReceiveWasCanceled);
        Assert.True(socket.WasAborted);
        Assert.False(requestDeadline.IsCancellationRequested);
        Assert.True(
            elapsed.Elapsed >= RealtimeProtocol.GracefulCloseTimeout - TimeSpan.FromMilliseconds(250),
            $"The silent peer was canceled too early after {elapsed.Elapsed}.");
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(4),
            $"The graceful-close deadline did not cancel the silent peer in time: {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task SessionCloseControlEventCannotBypassFirstEventAuthentication()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = new ScriptedWebSocket(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_1","sequence":1,"timestamp_us":0,"reason":"done"}"""));

        await RunAsync(socket, reservation, registry, preAuthenticated: false);

        var error = Assert.Single(ReadEvents(socket));
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("authentication_required", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("session_id", out _));
        Assert.Equal((int)RealtimeCloseCode.AuthenticationFailed, (int)socket.CloseStatus!.Value);
    }

    [Fact]
    public async Task OneTimeTicketAuthenticatesOnlyAsTheFirstControlEvent()
    {
        var registry = new RealtimeSessionRegistry();
        var tickets = new RealtimeTicketStore(new ManualTimeProvider(StartTime));
        Assert.True(tickets.TryIssue("source-a", out var issue, out var issueError));
        Assert.Null(issueError);
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = new ScriptedWebSocket(
            ScriptedWebSocketFrame.Text($$"""
                {"type":"session.authenticate","event_id":"auth_1","sequence":1,"timestamp_us":0,"ticket":"{{issue!.Ticket}}"}
                """),
            ScriptedWebSocketFrame.Text(
                """{"type":"session.close","event_id":"close_2","sequence":2,"timestamp_us":1,"reason":"done"}"""));

        await RunAsync(socket, reservation, registry, preAuthenticated: false, tickets);

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "session.closed"], ReadTypes(events));
        Assert.Equal([1L, 2L], ReadSequences(events));
        Assert.Equal(0, tickets.Count);
        Assert.Equal((int)System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, (int)socket.CloseStatus!.Value);
    }

    [Fact]
    public async Task PeerCloseDoesNotProduceApplicationDataAfterTheCloseFrame()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = ScriptedWebSocket.CreateCoordinatedPeerClose(
            ScriptedWebSocketFrame.Text(
                """{"type":"session.ping","event_id":"ping_1","sequence":1,"timestamp_us":0}"""),
            ScriptedWebSocketFrame.Close());
        var runTask = RunAsync(socket, reservation, registry, preAuthenticated: true);

        try
        {
            await socket.FirstApplicationSendStarted.WaitAsync(TimeSpan.FromSeconds(1));
            await socket.PeerCloseReceived.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(WebSocketState.CloseReceived, socket.State);
        }
        finally
        {
            socket.ReleaseFirstApplicationSend();
            await runTask;
        }

        Assert.Equal(["session.created"], ReadTypes(ReadEvents(socket)));
        Assert.Equal(0, socket.ApplicationSendsAfterPeerClose);
        Assert.Equal(1, socket.CloseOutputCalls);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, (int)socket.CloseStatus!.Value);
        Assert.False(socket.WasAborted);
    }

    [Theory]
    [InlineData("rtt_sensitive_ticket_value")]
    [InlineData("tmr_sensitive_api_key")]
    [InlineData("arbitrary-client-detail")]
    public async Task ClientCloseReasonIsRestrictedToNonSensitiveProtocolValues(string unsafeReason)
    {
        var socket = await RunPreAuthenticatedAsync(
            ScriptedWebSocketFrame.Text($$"""
                {"type":"session.close","event_id":"close_1","sequence":1,"timestamp_us":0,"reason":"{{unsafeReason}}"}
                """));

        var events = ReadEvents(socket);
        Assert.Equal(["session.created", "session.closed"], ReadTypes(events));
        Assert.Equal("client_closed", events[1].GetProperty("reason").GetString());
    }

    private static async Task<ScriptedWebSocket> RunPreAuthenticatedAsync(
        params ScriptedWebSocketFrame[] frames)
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);
        var socket = new ScriptedWebSocket(frames);

        await RunAsync(socket, reservation, registry, preAuthenticated: true);
        return socket;
    }

    private static async Task RunAsync(
        ScriptedWebSocket socket,
        RealtimeSessionRegistry.RealtimeConnectionLease reservation,
        RealtimeSessionRegistry registry,
        bool preAuthenticated,
        RealtimeTicketStore? ticketStore = null,
        CancellationToken requestAborted = default,
        ILogger<RealtimeConnection>? logger = null)
    {
        using var testDeadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        testDeadline.CancelAfter(TimeSpan.FromMilliseconds(4_500));
        try
        {
            var connection = new RealtimeConnection(
                ticketStore ?? new RealtimeTicketStore(new ManualTimeProvider(StartTime)),
                logger ?? NullLogger<RealtimeConnection>.Instance);
            await connection.RunAsync(
                    socket,
                    reservation,
                    preAuthenticated,
                    testDeadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            reservation.Dispose();
        }

        Assert.True(socket.WasDisposed);
        Assert.Equal(0, registry.GetSnapshot().ActiveSessions);
        Assert.Equal(0, registry.GetSnapshot().PendingConnections);
    }

    private static byte[] CreateInputAudioFrame(
        Guid identifier,
        ulong sequence,
        long timestampUs)
    {
        var message = new byte[
            RealtimeProtocol.BinaryHeaderSize + RealtimeProtocol.InputFramePayloadBytes];
        RealtimeBinaryFrameCodec.WriteHeader(
            message,
            new RealtimeBinaryFrameHeader(
                RealtimeBinaryFrameKind.InputAudio,
                identifier,
                sequence,
                timestampUs,
                RealtimeProtocol.InputFramePayloadBytes));
        return message;
    }

    private static JsonElement[] ReadEvents(ScriptedWebSocket socket)
        => socket.SentFrames
            .Where(static frame => frame.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
            .Select(static frame =>
            {
                using var document = JsonDocument.Parse(frame.Payload);
                return document.RootElement.Clone();
            })
            .ToArray();

    private static string?[] ReadTypes(IEnumerable<JsonElement> events)
        => events.Select(static item => item.GetProperty("type").GetString()).ToArray();

    private static long[] ReadSequences(IEnumerable<JsonElement> events)
        => events.Select(static item => item.GetProperty("sequence").GetInt64()).ToArray();

    private sealed class ActivationLogBarrier : ILogger<RealtimeConnection>
    {
        private readonly TaskCompletionSource<bool> entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int hasBlocked;

        public Task Entered => entered.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.CompareExchange(ref hasBlocked, 1, 0) != 0)
            {
                return;
            }

            entered.TrySetResult(true);
            if (!release.Task.Wait(TimeSpan.FromSeconds(4)))
            {
                throw new TimeoutException("The activation log barrier was not released within four seconds.");
            }
        }

        public void Release() => release.TrySetResult(true);
    }
}
