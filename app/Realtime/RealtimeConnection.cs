using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tomur.Realtime;

internal sealed class RealtimeConnection
{
    private readonly RealtimeTicketStore tickets;
    private readonly ILogger<RealtimeConnection> logger;

    public RealtimeConnection(
        RealtimeTicketStore tickets,
        ILogger<RealtimeConnection> logger)
    {
        this.tickets = tickets;
        this.logger = logger;
    }

    public async Task RunAsync(
        WebSocket socket,
        RealtimeSessionRegistry.RealtimeConnectionLease lease,
        bool preAuthenticated,
        CancellationToken requestAborted)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        connectionCancellation.CancelAfter(
            RealtimeProtocol.MaximumSessionDuration + RealtimeProtocol.GracefulCloseTimeout);

        // One physical slot is reserved for an overflow diagnostic. Client data remains
        // limited to InboundQueueCapacity and can never consume the terminal slot.
        var inbound = Channel.CreateBounded<RealtimeInboundMessage>(new BoundedChannelOptions(RealtimeProtocol.InboundQueueCapacity + 1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var outbound = Channel.CreateBounded<RealtimeOutboundMessage>(new BoundedChannelOptions(RealtimeProtocol.OutboundControlQueueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var state = new RealtimeConnectionState(lease, preAuthenticated);

        var receiveTask = ReceiveLoopAsync(
            socket,
            inbound.Writer,
            state,
            connectionCancellation.Token);
        var processTask = ProcessLoopAsync(
            inbound.Reader,
            outbound.Writer,
            state,
            connectionCancellation.Token);
        var sendTask = SendLoopAsync(
            socket,
            outbound.Reader,
            state,
            connectionCancellation,
            connectionCancellation.Token);

        try
        {
            await Task.WhenAll(receiveTask, processTask, sendTask).ConfigureAwait(false);

            if (socket.State == WebSocketState.CloseSent)
            {
                await ReceiveCloseHandshakeAsync(
                    socket,
                    state.CloseHandshakeReceiveLimit,
                    connectionCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogWarning(
                exception,
                "Realtime WebSocket transport failed for trace {TraceId} and source {Source}.",
                state.TraceId,
                lease.Source);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Realtime connection failed for trace {TraceId} and source {Source}.",
                state.TraceId,
                lease.Source);
        }
        finally
        {
            connectionCancellation.Cancel();
            inbound.Writer.TryComplete();
            outbound.Writer.TryComplete();
            DrainInbound(inbound.Reader, state);
            state.Dispose();

            if (socket.State is not WebSocketState.Closed and not WebSocketState.Aborted)
            {
                socket.Abort();
            }

            socket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket socket,
        ChannelWriter<RealtimeInboundMessage> writer,
        RealtimeConnectionState state,
        CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            if (state.PreAuthenticated &&
                !await WaitForAuthenticationAsync(
                    state,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Read one event beyond the accepted limit so the 50,000th event remains valid
            // and the limit is reported only when the client actually sends another event.
            for (var received = 0; received <= RealtimeProtocol.MaxEventsPerSession; received++)
            {
                if (state.IsTerminating)
                {
                    return;
                }

                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
                var remainingLifetime = RealtimeProtocol.MaximumSessionDuration - elapsed;
                if (remainingLifetime <= TimeSpan.Zero)
                {
                    TryWriteOrCompleteOverflow(writer, RealtimeInboundMessage.Timeout(
                        "session_duration_exceeded",
                        "The maximum Realtime session duration was reached."), state);
                    return;
                }

                var idleLimit = state.IsAuthenticated
                    ? RealtimeProtocol.IdleTimeout
                    : RealtimeProtocol.AuthenticationTimeout;
                var timeout = remainingLifetime < idleLimit ? remainingLifetime : idleLimit;
                var timeoutCode = remainingLifetime <= idleLimit
                    ? "session_duration_exceeded"
                    : state.IsAuthenticated ? "session_idle_timeout" : "authentication_timeout";
                var timeoutMessage = timeoutCode switch
                {
                    "session_duration_exceeded" => "The maximum Realtime session duration was reached.",
                    "session_idle_timeout" => "The Realtime session was idle for too long.",
                    _ => "The Realtime session was not authenticated before the deadline."
                };

                var message = await ReceiveMessageAsync(
                    socket,
                    state,
                    timeout,
                    timeoutCode,
                    timeoutMessage,
                    cancellationToken).ConfigureAwait(false);

                if (message is null)
                {
                    return;
                }

                if (state.IsTerminating)
                {
                    message.Dispose();
                    return;
                }

                if (message.Kind == RealtimeInboundMessageKind.PeerClose)
                {
                    state.ObservePeerClose();
                }

                if (!TryWriteOrCompleteOverflow(writer, message, state))
                {
                    return;
                }

                if (message.Kind is RealtimeInboundMessageKind.PeerClose or
                    RealtimeInboundMessageKind.Failure or
                    RealtimeInboundMessageKind.Timeout)
                {
                    return;
                }

                if (!state.IsAuthenticated &&
                    !await WaitForAuthenticationAsync(
                        state,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (received == RealtimeProtocol.MaxEventsPerSession)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            TryWriteOrCompleteOverflow(writer, RealtimeInboundMessage.Failure(
                "transport_error",
                "The Realtime transport ended unexpectedly."), state);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task<bool> WaitForAuthenticationAsync(
        RealtimeConnectionState state,
        CancellationToken cancellationToken)
    {
        return await state.AuthenticationCompletion
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessLoopAsync(
        ChannelReader<RealtimeInboundMessage> reader,
        ChannelWriter<RealtimeOutboundMessage> writer,
        RealtimeConnectionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (state.PreAuthenticated && !await ActivateAsync(state, writer, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            for (var processed = 0; processed <= RealtimeProtocol.MaxEventsPerSession; processed++)
            {
                RealtimeInboundMessage message;
                try
                {
                    if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!reader.TryRead(out message))
                    {
                        continue;
                    }

                    state.ReleaseInboundQueueSlot();
                }
                catch (RealtimeTransportException exception)
                {
                    await QueueFatalAsync(
                        state,
                        writer,
                        exception.Code,
                        exception.Message,
                        RealtimeCloseCode.QueueOverflow,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (ChannelClosedException exception)
                {
                    var transport = exception.InnerException as RealtimeTransportException;
                    await QueueFatalAsync(
                        state,
                        writer,
                        transport?.Code ?? "input_channel_closed",
                        transport?.Message ?? "The bounded Realtime input channel closed unexpectedly.",
                        RealtimeCloseCode.QueueOverflow,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                using (message)
                {
                    if (processed == RealtimeProtocol.MaxEventsPerSession &&
                        message.Kind is RealtimeInboundMessageKind.Text or RealtimeInboundMessageKind.Binary)
                    {
                        await QueueFatalAsync(
                            state,
                            writer,
                            "session_event_limit_exceeded",
                            "The maximum number of events for this Realtime session was reached.",
                            RealtimeCloseCode.PolicyViolation,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if ((message.Kind is RealtimeInboundMessageKind.Text or RealtimeInboundMessageKind.Binary) &&
                        !state.TryRecordInboundEvent(message.ReceivedTimestamp))
                    {
                        await QueueFatalAsync(
                            state,
                            writer,
                            "event_rate_exceeded",
                            "The Realtime event rate limit was exceeded.",
                            RealtimeCloseCode.PolicyViolation,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var keepProcessing = message.Kind switch
                    {
                        RealtimeInboundMessageKind.Text => await ProcessControlEventAsync(
                            message.Payload,
                            state,
                            writer,
                            cancellationToken).ConfigureAwait(false),
                        RealtimeInboundMessageKind.Binary => await ProcessBinaryFrameAsync(
                            message.Payload,
                            state,
                            writer,
                            cancellationToken).ConfigureAwait(false),
                        RealtimeInboundMessageKind.PeerClose => await CloseFromPeerAsync(
                            state,
                            writer,
                            cancellationToken).ConfigureAwait(false),
                        RealtimeInboundMessageKind.Timeout => await FailInboundAsync(
                            state,
                            writer,
                            message.ErrorCode!,
                            message.ErrorMessage!,
                            RealtimeCloseCode.Timeout,
                            cancellationToken).ConfigureAwait(false),
                        RealtimeInboundMessageKind.Failure => await FailInboundAsync(
                            state,
                            writer,
                            message.ErrorCode!,
                            message.ErrorMessage!,
                            RealtimeCloseCode.ProtocolError,
                            cancellationToken).ConfigureAwait(false),
                        RealtimeInboundMessageKind.QueueOverflow => await FailInboundAsync(
                            state,
                            writer,
                            message.ErrorCode!,
                            message.ErrorMessage!,
                            RealtimeCloseCode.QueueOverflow,
                            cancellationToken).ConfigureAwait(false),
                        _ => false
                    };

                    if (!keepProcessing)
                    {
                        return;
                    }
                }
            }

            await QueueFatalAsync(
                state,
                writer,
                "input_channel_closed",
                "The bounded Realtime input channel closed unexpectedly.",
                RealtimeCloseCode.ProtocolError,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.RejectPendingAuthentication();
            writer.TryComplete();
        }
    }

    private async Task<bool> ProcessControlEventAsync(
        ReadOnlyMemory<byte> payload,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        var parsed = RealtimeClientEventParser.Parse(payload);
        if (!parsed.Success)
        {
            await QueueFatalAsync(
                state,
                writer,
                parsed.ErrorCode!,
                parsed.ErrorMessage!,
                RealtimeCloseCode.ProtocolError,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var clientEvent = parsed.Event!;
        if (!state.TryAcceptControlEnvelope(clientEvent, out var sequenceError))
        {
            await QueueFatalAsync(
                state,
                writer,
                sequenceError!.Code,
                sequenceError.Message,
                RealtimeCloseCode.ProtocolError,
                cancellationToken,
                sequenceError.ExpectedSequence,
                sequenceError.ReceivedSequence).ConfigureAwait(false);
            return false;
        }

        if (!state.IsAuthenticated)
        {
            if (clientEvent is not RealtimeAuthenticateEvent authenticate)
            {
                await QueueFatalAsync(
                    state,
                    writer,
                    "authentication_required",
                    "session.authenticate must be the first control event.",
                    RealtimeCloseCode.AuthenticationFailed,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!tickets.TryRedeem(authenticate.Ticket, state.Lease.Source))
            {
                await QueueFatalAsync(
                    state,
                    writer,
                    "authentication_failed",
                    "The Realtime ticket is invalid, expired or already used.",
                    RealtimeCloseCode.AuthenticationFailed,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            return await ActivateAsync(state, writer, cancellationToken).ConfigureAwait(false);
        }

        return clientEvent switch
        {
            RealtimeAuthenticateEvent => await RejectAlreadyAuthenticatedAsync(state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeSessionUpdateEvent update => await UpdateSessionAsync(update, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimePingEvent ping => await PongAsync(ping, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeSessionCloseEvent close => await CloseFromClientAsync(close, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeInputAudioCommitEvent commit => await CommitAudioAsync(commit, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeInputAudioClearEvent clear => await ClearAudioAsync(clear, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeResponseCancelEvent cancel => await CancelResponseAsync(cancel.ResponseEpoch, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimeTextDisplayedEvent displayed => await RejectInactiveResponseAsync(displayed.ResponseEpoch, state, writer, cancellationToken).ConfigureAwait(false),
            RealtimePlaybackConsumedEvent played => await RejectInactiveResponseAsync(played.ResponseEpoch, state, writer, cancellationToken).ConfigureAwait(false),
            _ => await RejectUnsupportedStateAsync(clientEvent.Type!, state, writer, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<bool> ProcessBinaryFrameAsync(
        ReadOnlyMemory<byte> payload,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (!state.IsAuthenticated)
        {
            await QueueFatalAsync(
                state,
                writer,
                "authentication_required",
                "Binary audio is not accepted before session authentication.",
                RealtimeCloseCode.AuthenticationFailed,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var parsed = RealtimeBinaryFrameCodec.Parse(payload.Span);
        if (!parsed.Success)
        {
            await QueueFatalAsync(
                state,
                writer,
                parsed.ErrorCode!,
                parsed.ErrorMessage!,
                RealtimeCloseCode.ProtocolError,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var header = parsed.Header;
        if (state.StateMachine.State is not RealtimeSessionState.Listening and
            not RealtimeSessionState.UserSpeaking)
        {
            await QueueFatalAsync(
                state,
                writer,
                "input_audio_not_allowed",
                $"Input audio is not allowed while the session state is {state.StateName}.",
                RealtimeCloseCode.ProtocolError,
                cancellationToken,
                captureStreamId: header.Identifier.ToString("N")).ConfigureAwait(false);
            return false;
        }

        if (header.Kind != RealtimeBinaryFrameKind.InputAudio)
        {
            await QueueFatalAsync(
                state,
                writer,
                "binary_direction_invalid",
                "Clients may only send input audio binary frames.",
                RealtimeCloseCode.ProtocolError,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (header.PayloadLength != RealtimeProtocol.InputFramePayloadBytes)
        {
            await QueueFatalAsync(
                state,
                writer,
                "input_audio_frame_size_invalid",
                $"Input audio frames must contain exactly {RealtimeProtocol.InputFramePayloadBytes} PCM bytes.",
                RealtimeCloseCode.ProtocolError,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!state.TryAcceptAudioFrame(header, out var frameError))
        {
            state.DiscardAudio();
            await QueueFatalAsync(
                state,
                writer,
                frameError!.Code,
                frameError.Message,
                RealtimeCloseCode.ProtocolError,
                cancellationToken,
                frameError.ExpectedSequence,
                frameError.ReceivedSequence,
                header.Identifier.ToString("N")).ConfigureAwait(false);
            return false;
        }

        if (!state.AudioBuffer!.TryAppend(payload.Span[RealtimeProtocol.BinaryHeaderSize..]))
        {
            state.DiscardAudio();
            await QueueFatalAsync(
                state,
                writer,
                "input_audio_buffer_overflow",
                "The maximum input utterance duration was exceeded.",
                RealtimeCloseCode.QueueOverflow,
                cancellationToken,
                captureStreamId: header.Identifier.ToString("N")).ConfigureAwait(false);
            return false;
        }

        state.RecordAudioFrame(header.PayloadLength);
        if (state.StateMachine.State == RealtimeSessionState.Listening)
        {
            state.StateMachine.TransitionOrThrow(RealtimeSessionState.UserSpeaking);
            var metadata = state.NextServerEvent();
            await QueueJsonAsync(
                writer,
                new RealtimeInputAudioStartedEvent(
                    "input_audio_buffer.started",
                    metadata.EventId,
                    metadata.Sequence,
                    metadata.TimestampUs,
                    state.SessionId!,
                    state.TraceId,
                    state.StateName,
                    header.Identifier.ToString("N"),
                    header.Sequence),
                RealtimeJsonSerializerContext.Default.RealtimeInputAudioStartedEvent,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> ActivateAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (!state.Lease.TryActivate())
        {
            await QueueFatalAsync(
                state,
                writer,
                "session_busy",
                "Another Realtime session is already active.",
                RealtimeCloseCode.SessionBusy,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        state.Activate();
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeSessionCreatedEvent(
                "session.created",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                RealtimeProtocol.Name,
                state.Configuration,
                CreateCapabilities(),
                RealtimeLimitsResponse.Create()),
            RealtimeJsonSerializerContext.Default.RealtimeSessionCreatedEvent,
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Realtime session {SessionId} opened for trace {TraceId} and source {Source}.",
            state.SessionId,
            state.TraceId,
            state.Lease.Source);
        return true;
    }

    private async Task<bool> UpdateSessionAsync(
        RealtimeSessionUpdateEvent update,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (!TryValidateConfiguration(update.Session, out var error))
        {
            await QueueErrorAsync(state, writer, "invalid_session_configuration", error!, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (state.AudioBuffer!.Length != 0)
        {
            await QueueErrorAsync(
                state,
                writer,
                "session_update_during_utterance",
                "Session configuration cannot change while input audio is buffered.",
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        state.Configuration = update.Session!;
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeSessionUpdatedServerEvent(
                "session.updated",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                state.Configuration),
            RealtimeJsonSerializerContext.Default.RealtimeSessionUpdatedServerEvent,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> PongAsync(
        RealtimePingEvent ping,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimePongEvent(
                "session.pong",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                ping.EventId!),
            RealtimeJsonSerializerContext.Default.RealtimePongEvent,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CommitAudioAsync(
        RealtimeInputAudioCommitEvent commit,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (!TryParseRequiredId(commit.CaptureStreamId, out var captureStreamId) ||
            state.CaptureStreamId != captureStreamId)
        {
            await QueueErrorAsync(
                state,
                writer,
                "capture_stream_mismatch",
                "capture_stream_id does not match the active input stream.",
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (state.AudioBuffer!.Length == 0)
        {
            await QueueErrorAsync(
                state,
                writer,
                "input_audio_buffer_empty",
                "Input audio must be appended before commit.",
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        Guid utteranceId;
        if (string.IsNullOrWhiteSpace(commit.UtteranceId))
        {
            utteranceId = Guid.NewGuid();
        }
        else if (!TryParseRequiredId(commit.UtteranceId, out utteranceId))
        {
            await QueueErrorAsync(
                state,
                writer,
                "utterance_id_invalid",
                "utterance_id must be a non-empty UUID when provided.",
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        state.StateMachine.TransitionOrThrow(RealtimeSessionState.Transcribing);
        var bufferedBytes = state.AudioBuffer.Length;
        var durationMs = checked(bufferedBytes * 1000 /
            (RealtimeProtocol.InputSampleRate * RealtimeProtocol.InputChannels * sizeof(short)));
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeInputAudioCommittedEvent(
                "input_audio_buffer.committed",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                captureStreamId.ToString("N"),
                utteranceId.ToString("N"),
                bufferedBytes,
                durationMs),
            RealtimeJsonSerializerContext.Default.RealtimeInputAudioCommittedEvent,
            cancellationToken).ConfigureAwait(false);

        state.CommitAudio();
        state.StateMachine.TransitionOrThrow(RealtimeSessionState.Listening);
        await QueueErrorAsync(
            state,
            writer,
            "realtime_pipeline_unavailable",
            "The Realtime VAD, ASR and TTS session pipeline is not connected yet. No transcript or audio was produced.",
            false,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClearAudioAsync(
        RealtimeInputAudioClearEvent clear,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(clear.CaptureStreamId) &&
            (!TryParseRequiredId(clear.CaptureStreamId, out var requestedId) || state.CaptureStreamId != requestedId))
        {
            await QueueErrorAsync(
                state,
                writer,
                "capture_stream_mismatch",
                "capture_stream_id does not match the active input stream.",
                false,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var previous = state.CaptureStreamId?.ToString("N");
        var discarded = state.DiscardAudio();
        if (state.StateMachine.State == RealtimeSessionState.UserSpeaking)
        {
            state.StateMachine.TransitionOrThrow(RealtimeSessionState.Listening);
        }

        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeInputAudioClearedEvent(
                "input_audio_buffer.cleared",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                previous,
                discarded.Frames,
                discarded.Bytes,
                "client_requested"),
            RealtimeJsonSerializerContext.Default.RealtimeInputAudioClearedEvent,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CancelResponseAsync(
        long responseEpoch,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        if (responseEpoch <= 0)
        {
            await QueueErrorAsync(
                state,
                writer,
                "response_epoch_invalid",
                "response_epoch must be a positive integer.",
                false,
                cancellationToken,
                responseEpoch: responseEpoch).ConfigureAwait(false);
            return true;
        }

        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeResponseCancelledEvent(
                "response.cancelled",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId!,
                state.TraceId,
                state.StateName,
                responseEpoch,
                "not_active"),
            RealtimeJsonSerializerContext.Default.RealtimeResponseCancelledEvent,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RejectInactiveResponseAsync(
        long responseEpoch,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        await QueueErrorAsync(
            state,
            writer,
            "response_not_active",
            $"Response epoch {responseEpoch} is not active.",
            false,
            cancellationToken,
            responseEpoch: responseEpoch).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RejectAlreadyAuthenticatedAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        await QueueErrorAsync(
            state,
            writer,
            "already_authenticated",
            "The Realtime session is already authenticated.",
            false,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RejectUnsupportedStateAsync(
        string eventType,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        await QueueErrorAsync(
            state,
            writer,
            "event_not_allowed",
            $"The event '{eventType}' is not allowed while the session state is {state.StateName}.",
            false,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CloseFromClientAsync(
        RealtimeSessionCloseEvent close,
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        var reason = NormalizeCloseReason(close.Reason);
        await QueueClosedAsync(state, writer, reason, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> CloseFromPeerAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        CancellationToken cancellationToken)
    {
        state.Close();
        await QueueAsync(
            writer,
            RealtimeOutboundMessage.Close(WebSocketCloseStatus.NormalClosure, "peer_closed"),
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> FailInboundAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        string code,
        string message,
        RealtimeCloseCode closeCode,
        CancellationToken cancellationToken)
    {
        await QueueFatalAsync(state, writer, code, message, closeCode, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task QueueClosedAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        string reason,
        CancellationToken cancellationToken)
    {
        state.Close();
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeSessionClosedEvent(
                "session.closed",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId,
                state.TraceId,
                state.StateName,
                reason,
                state.ReceivedEvents,
                state.ReceivedAudioFrames,
                state.ReceivedAudioBytes,
                state.CommittedUtterances,
                state.DiscardedAudioFrames,
                state.DiscardedAudioBytes),
            RealtimeJsonSerializerContext.Default.RealtimeSessionClosedEvent,
            cancellationToken).ConfigureAwait(false);
        await QueueAsync(
            writer,
            RealtimeOutboundMessage.Close(WebSocketCloseStatus.NormalClosure, "session_closed"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueFatalAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        string code,
        string message,
        RealtimeCloseCode closeCode,
        CancellationToken cancellationToken,
        long? expectedSequence = null,
        long? receivedSequence = null,
        string? captureStreamId = null,
        long? responseEpoch = null)
    {
        state.Fail();
        await QueueErrorAsync(
            state,
            writer,
            code,
            message,
            true,
            cancellationToken,
            expectedSequence,
            receivedSequence,
            captureStreamId,
            responseEpoch).ConfigureAwait(false);
        await QueueAsync(
            writer,
            RealtimeOutboundMessage.Close(RealtimeCloseStatus.From(closeCode), code),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueErrorAsync(
        RealtimeConnectionState state,
        ChannelWriter<RealtimeOutboundMessage> writer,
        string code,
        string message,
        bool fatal,
        CancellationToken cancellationToken,
        long? expectedSequence = null,
        long? receivedSequence = null,
        string? captureStreamId = null,
        long? responseEpoch = null)
    {
        var metadata = state.NextServerEvent();
        await QueueJsonAsync(
            writer,
            new RealtimeErrorEvent(
                "error",
                metadata.EventId,
                metadata.Sequence,
                metadata.TimestampUs,
                state.SessionId,
                state.TraceId,
                state.StateName,
                code,
                message,
                fatal,
                expectedSequence,
                receivedSequence,
                captureStreamId,
                responseEpoch),
            RealtimeJsonSerializerContext.Default.RealtimeErrorEvent,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task QueueJsonAsync<T>(
        ChannelWriter<RealtimeOutboundMessage> writer,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await QueueAsync(writer, RealtimeOutboundMessage.Text(payload), cancellationToken).ConfigureAwait(false);
    }

    private static async Task QueueAsync(
        ChannelWriter<RealtimeOutboundMessage> writer,
        RealtimeOutboundMessage message,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RealtimeProtocol.SendTimeout);
        await writer.WriteAsync(message, timeout.Token).ConfigureAwait(false);
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<RealtimeOutboundMessage> reader,
        RealtimeConnectionState state,
        CancellationTokenSource connectionCancellation,
        CancellationToken cancellationToken)
    {
        var waitingForPeerClose = false;
        try
        {
            for (var sent = 0; sent < (RealtimeProtocol.MaxEventsPerSession * 2) + 4; sent++)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!reader.TryRead(out var message))
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RealtimeProtocol.SendTimeout);
                if (message.IsClose)
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(
                            message.CloseStatus!.Value,
                            message.CloseDescription,
                            timeout.Token).ConfigureAwait(false);
                    }

                    if (socket.State == WebSocketState.CloseSent)
                    {
                        waitingForPeerClose = true;
                        connectionCancellation.CancelAfter(RealtimeProtocol.GracefulCloseTimeout);
                    }

                    return;
                }

                var disposition = state.TryStartApplicationSend(
                    socket,
                    message.Payload,
                    timeout.Token,
                    out var sendOperation);
                if (disposition == RealtimeApplicationSendDisposition.PeerCloseObserved)
                {
                    // Preserve the queued close response, but never send application data
                    // after the peer's close frame has been observed.
                    continue;
                }

                if (disposition == RealtimeApplicationSendDisposition.TransportClosed)
                {
                    return;
                }

                await sendOperation.ConfigureAwait(false);
            }
        }
        finally
        {
            if (!waitingForPeerClose)
            {
                connectionCancellation.Cancel();
            }
        }
    }

    private static async Task ReceiveCloseHandshakeAsync(
        WebSocket socket,
        int receiveLimit,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RealtimeProtocol.MaxJsonMessageBytes);

        try
        {
            // Data already in flight cannot extend the close handshake indefinitely.
            for (var receiveAttempt = 0;
                receiveAttempt < receiveLimit &&
                socket.State == WebSocketState.CloseSent;
                receiveAttempt++)
            {
                var result = await socket.ReceiveAsync(
                    buffer.AsMemory(0, RealtimeProtocol.MaxJsonMessageBytes),
                    cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<RealtimeInboundMessage?> ReceiveMessageAsync(
        WebSocket socket,
        RealtimeConnectionState state,
        TimeSpan timeout,
        string timeoutCode,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RealtimeProtocol.MaxJsonMessageBytes);
        var length = 0;
        WebSocketMessageType? messageType = null;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            for (var fragment = 0; fragment < RealtimeProtocol.MaxMessageFragments; fragment++)
            {
                if (length == RealtimeProtocol.MaxJsonMessageBytes)
                {
                    return RealtimeInboundMessage.FromOwnedFailure(
                        buffer,
                        "message_too_large",
                        $"Realtime messages may not exceed {RealtimeProtocol.MaxJsonMessageBytes} bytes.");
                }

                ValueWebSocketReceiveResult result;
                try
                {
                    if (!state.TryStartApplicationReceive(
                            socket,
                            buffer.AsMemory(length, RealtimeProtocol.MaxJsonMessageBytes - length),
                            deadline.Token,
                            out var receiveOperation))
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                        return null;
                    }

                    try
                    {
                        result = await receiveOperation.ConfigureAwait(false);
                    }
                    finally
                    {
                        state.CompleteApplicationReceive();
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    return RealtimeInboundMessage.Timeout(timeoutCode, timeoutMessage);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    return RealtimeInboundMessage.PeerClose();
                }

                messageType ??= result.MessageType;
                if (messageType != result.MessageType)
                {
                    return RealtimeInboundMessage.FromOwnedFailure(
                        buffer,
                        "fragment_type_mismatch",
                        "All fragments in a Realtime message must use the same message type.");
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    return RealtimeInboundMessage.FromOwned(
                        result.MessageType == WebSocketMessageType.Text
                            ? RealtimeInboundMessageKind.Text
                            : RealtimeInboundMessageKind.Binary,
                        buffer,
                        length);
                }
            }

            return RealtimeInboundMessage.FromOwnedFailure(
                buffer,
                "fragment_limit_exceeded",
                $"Realtime messages may not contain more than {RealtimeProtocol.MaxMessageFragments} fragments.");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            throw;
        }
    }

    private static bool TryValidateConfiguration(
        RealtimeSessionConfiguration? configuration,
        out string? error)
    {
        if (configuration is null)
        {
            error = "session is required.";
            return false;
        }

        if (configuration.ConversationId is { Length: > 128 })
        {
            error = "conversation_id may not exceed 128 characters.";
            return false;
        }

        if (!string.Equals(configuration.TurnDetection, "manual", StringComparison.Ordinal) ||
            !string.Equals(configuration.InputAudioFormat, "pcm16le", StringComparison.Ordinal) ||
            configuration.InputSampleRate != RealtimeProtocol.InputSampleRate ||
            configuration.InputChannels != RealtimeProtocol.InputChannels ||
            configuration.InputFrameDurationMs != RealtimeProtocol.InputFrameDurationMilliseconds ||
            !string.Equals(configuration.OutputAudioFormat, "pcm16le", StringComparison.Ordinal) ||
            configuration.OutputSampleRate != RealtimeProtocol.OutputSampleRate ||
            configuration.OutputChannels != RealtimeProtocol.OutputChannels)
        {
            error = "Protocol v1 requires manual turn detection, 16 kHz mono PCM16LE 20 ms input frames and 24 kHz mono PCM16LE output.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseRequiredId(string? value, out Guid id)
        => Guid.TryParse(value, out id) && id != Guid.Empty;

    private static string NormalizeCloseReason(string? value)
        => value?.Trim() switch
        {
            "client_closed" => "client_closed",
            "user_requested" => "user_requested",
            "page_unload" => "page_unload",
            _ => "client_closed"
        };

    private static RealtimeCapabilityStatus CreateCapabilities()
        => new(
            "available_unverified",
            "unavailable",
            "not_connected",
            "not_connected",
            "not_connected",
            "not_implemented",
            "pending");

    private static void DrainInbound(
        ChannelReader<RealtimeInboundMessage> reader,
        RealtimeConnectionState state)
    {
        for (var drained = 0;
             drained <= RealtimeProtocol.InboundQueueCapacity && reader.TryRead(out var message);
             drained++)
        {
            state.ReleaseInboundQueueSlot();
            message.Dispose();
        }
    }

    private static bool TryWriteOrCompleteOverflow(
        ChannelWriter<RealtimeInboundMessage> writer,
        RealtimeInboundMessage message,
        RealtimeConnectionState state)
    {
        var queued = state.ReserveInboundQueueSlot();
        if (queued <= RealtimeProtocol.InboundQueueCapacity && writer.TryWrite(message))
        {
            return true;
        }

        message.Dispose();
        if (queued == RealtimeProtocol.InboundQueueCapacity + 1 &&
            writer.TryWrite(RealtimeInboundMessage.QueueOverflow()))
        {
            writer.TryComplete();
            return false;
        }

        state.ReleaseInboundQueueSlot();
        writer.TryComplete();
        return false;
    }

    private sealed class RealtimeConnectionState : IDisposable
    {
        private readonly HashSet<string> clientEventIds = new(StringComparer.Ordinal);
        private readonly object transportGate = new();
        private long nextClientSequence = 1;
        private long lastClientTimestampUs = -1;
        private long nextServerSequence = 1;
        private int authenticated;
        private int terminating;
        private int closeHandshakeReceiveLimit = RealtimeProtocol.MaxCloseHandshakeReceiveIterations;
        private int rateWindowCount;
        private int queuedInboundMessages;
        private bool applicationReceiveInProgress;
        private bool peerCloseObserved;
        private long rateWindowStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        public RealtimeConnectionState(
            RealtimeSessionRegistry.RealtimeConnectionLease lease,
            bool preAuthenticated)
        {
            Lease = lease;
            PreAuthenticated = preAuthenticated;
            TraceId = Guid.NewGuid().ToString("N");
        }

        public RealtimeSessionRegistry.RealtimeConnectionLease Lease { get; }

        public Task<bool> AuthenticationCompletion => authenticationCompletion.Task;

        public bool PreAuthenticated { get; }

        public string TraceId { get; }

        public string? SessionId => Lease.SessionId;

        public bool IsAuthenticated => Volatile.Read(ref authenticated) != 0;

        public bool IsTerminating => Volatile.Read(ref terminating) != 0;

        public int CloseHandshakeReceiveLimit => Volatile.Read(ref closeHandshakeReceiveLimit);

        public RealtimeStateMachine StateMachine { get; } = new();

        public string StateName => RealtimeProtocol.GetStateName(StateMachine.State);

        public RealtimeSessionConfiguration Configuration { get; set; } = RealtimeSessionConfiguration.CreateDefault();

        public RealtimeAudioBuffer? AudioBuffer { get; private set; }

        public Guid? CaptureStreamId { get; private set; }

        public ulong LastAudioSequence { get; private set; }

        public long LastAudioTimestampUs { get; private set; } = -1;

        public long ReceivedEvents { get; private set; }

        public long ReceivedAudioFrames { get; private set; }

        public long ReceivedAudioBytes { get; private set; }

        public long CommittedUtterances { get; private set; }

        public long DiscardedAudioFrames { get; private set; }

        public long DiscardedAudioBytes { get; private set; }

        public void Activate()
        {
            AudioBuffer = new RealtimeAudioBuffer();
            StateMachine.TransitionOrThrow(RealtimeSessionState.Listening);
            Volatile.Write(ref authenticated, 1);
            authenticationCompletion.TrySetResult(true);
        }

        public bool TryAcceptControlEnvelope(
            IRealtimeClientEvent clientEvent,
            out RealtimeSequenceError? error)
        {
            if (clientEvent.Sequence != nextClientSequence)
            {
                error = new RealtimeSequenceError(
                    "control_sequence_mismatch",
                    "Client control event sequence must be strictly consecutive.",
                    nextClientSequence,
                    clientEvent.Sequence);
                return false;
            }

            if (clientEvent.TimestampUs < lastClientTimestampUs)
            {
                error = new RealtimeSequenceError(
                    "control_timestamp_reordered",
                    "Client control event timestamps must not move backwards.",
                    null,
                    null);
                return false;
            }

            if (!clientEventIds.Add(clientEvent.EventId!))
            {
                error = new RealtimeSequenceError(
                    "duplicate_event_id",
                    "Client event_id values must be unique within a session.",
                    null,
                    null);
                return false;
            }

            nextClientSequence++;
            lastClientTimestampUs = clientEvent.TimestampUs;
            error = null;
            return true;
        }

        public bool TryAcceptAudioFrame(
            RealtimeBinaryFrameHeader header,
            out RealtimeSequenceError? error)
        {
            if (CaptureStreamId is null)
            {
                if (header.Sequence != 1)
                {
                    error = new RealtimeSequenceError(
                        "audio_sequence_mismatch",
                        "A new capture stream must start with audio sequence 1.",
                        1,
                        checked((long)header.Sequence));
                    return false;
                }

                CaptureStreamId = header.Identifier;
            }
            else if (CaptureStreamId != header.Identifier)
            {
                error = new RealtimeSequenceError(
                    "capture_stream_changed",
                    "The capture stream cannot change until the input audio buffer is committed or cleared.",
                    null,
                    null);
                return false;
            }

            var expected = LastAudioSequence + 1;
            if (header.Sequence != expected)
            {
                error = new RealtimeSequenceError(
                    "audio_sequence_mismatch",
                    "Input audio sequence contains a duplicate, reordering or gap.",
                    checked((long)expected),
                    checked((long)header.Sequence));
                return false;
            }

            if (header.TimestampUs < LastAudioTimestampUs)
            {
                error = new RealtimeSequenceError(
                    "audio_timestamp_reordered",
                    "Input audio timestamps must not move backwards.",
                    null,
                    null);
                return false;
            }

            LastAudioSequence = header.Sequence;
            LastAudioTimestampUs = header.TimestampUs;
            error = null;
            return true;
        }

        public bool TryRecordInboundEvent(long receivedTimestamp)
        {
            ReceivedEvents++;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(rateWindowStarted, receivedTimestamp) >= TimeSpan.FromSeconds(1))
            {
                rateWindowStarted = receivedTimestamp;
                rateWindowCount = 0;
            }

            rateWindowCount++;
            return rateWindowCount <= RealtimeProtocol.MaxEventsPerSecond;
        }

        public int ReserveInboundQueueSlot()
            => Interlocked.Increment(ref queuedInboundMessages);

        public void ReleaseInboundQueueSlot()
            => Interlocked.Decrement(ref queuedInboundMessages);

        public void ObservePeerClose()
        {
            lock (transportGate)
            {
                peerCloseObserved = true;
            }
        }

        public bool TryStartApplicationReceive(
            WebSocket socket,
            Memory<byte> destination,
            CancellationToken cancellationToken,
            out ValueTask<ValueWebSocketReceiveResult> receiveOperation)
        {
            lock (transportGate)
            {
                if (terminating != 0)
                {
                    receiveOperation = default;
                    return false;
                }

                if (applicationReceiveInProgress)
                {
                    throw new InvalidOperationException("A Realtime application receive is already in progress.");
                }

                applicationReceiveInProgress = true;
                try
                {
                    receiveOperation = socket.ReceiveAsync(destination, cancellationToken);
                    return true;
                }
                catch
                {
                    applicationReceiveInProgress = false;
                    throw;
                }
            }
        }

        public void CompleteApplicationReceive()
        {
            lock (transportGate)
            {
                applicationReceiveInProgress = false;
            }
        }

        public RealtimeApplicationSendDisposition TryStartApplicationSend(
            WebSocket socket,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken,
            out ValueTask sendOperation)
        {
            lock (transportGate)
            {
                if (peerCloseObserved || socket.State == WebSocketState.CloseReceived)
                {
                    sendOperation = default;
                    return RealtimeApplicationSendDisposition.PeerCloseObserved;
                }

                if (socket.State != WebSocketState.Open)
                {
                    sendOperation = default;
                    return RealtimeApplicationSendDisposition.TransportClosed;
                }

                sendOperation = socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                return RealtimeApplicationSendDisposition.Started;
            }
        }

        public void RecordAudioFrame(int payloadBytes)
        {
            ReceivedAudioFrames++;
            ReceivedAudioBytes += payloadBytes;
        }

        public (string EventId, long Sequence, long TimestampUs) NextServerEvent()
        {
            var sequence = nextServerSequence++;
            return ($"srv_{TraceId[..8]}_{sequence}", sequence, RealtimeProtocol.GetMonotonicTimestampMicroseconds());
        }

        public void CommitAudio()
        {
            CommittedUtterances++;
            ResetAudio();
        }

        public (int Frames, int Bytes) DiscardAudio()
        {
            var bytes = AudioBuffer?.Length ?? 0;
            var frames = bytes / RealtimeProtocol.InputFramePayloadBytes;
            DiscardedAudioFrames += frames;
            DiscardedAudioBytes += bytes;
            ResetAudio();
            return (frames, bytes);
        }

        private void ResetAudio()
        {
            AudioBuffer?.Clear();
            CaptureStreamId = null;
            LastAudioSequence = 0;
            LastAudioTimestampUs = -1;
        }

        public void Fail()
        {
            BeginTermination();
            RejectPendingAuthentication();
            DiscardAudio();
            if (StateMachine.State is not RealtimeSessionState.Failed and not RealtimeSessionState.Closed)
            {
                StateMachine.TransitionOrThrow(RealtimeSessionState.Failed);
            }
        }

        public void Close()
        {
            BeginTermination();
            RejectPendingAuthentication();
            DiscardAudio();
            if (StateMachine.State == RealtimeSessionState.Closed)
            {
                return;
            }

            if (!StateMachine.TryTransition(RealtimeSessionState.Closed))
            {
                StateMachine.TryTransition(RealtimeSessionState.Failed);
                StateMachine.TransitionOrThrow(RealtimeSessionState.Closed);
            }
        }

        public void Dispose()
        {
            RejectPendingAuthentication();
            DiscardAudio();
            AudioBuffer?.Dispose();
            AudioBuffer = null;
            Lease.Dispose();
        }

        public void RejectPendingAuthentication()
            => authenticationCompletion.TrySetResult(false);

        private void BeginTermination()
        {
            lock (transportGate)
            {
                if (terminating != 0)
                {
                    return;
                }

                if (applicationReceiveInProgress)
                {
                    Volatile.Write(
                        ref closeHandshakeReceiveLimit,
                        RealtimeProtocol.MaxCloseHandshakeReceiveIterations - 1);
                }

                Volatile.Write(ref terminating, 1);
            }
        }

        private readonly TaskCompletionSource<bool> authenticationCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RealtimeAudioBuffer : IDisposable
    {
        private byte[]? buffer = ArrayPool<byte>.Shared.Rent(RealtimeProtocol.MaxInputAudioBytes);

        public int Length { get; private set; }

        public bool TryAppend(ReadOnlySpan<byte> payload)
        {
            var destination = buffer;
            if (destination is null || payload.Length > RealtimeProtocol.MaxInputAudioBytes - Length)
            {
                return false;
            }

            payload.CopyTo(destination.AsSpan(Length));
            Length += payload.Length;
            return true;
        }

        public void Clear()
        {
            var destination = buffer;
            if (destination is not null && Length > 0)
            {
                CryptographicOperations.ZeroMemory(destination.AsSpan(0, Length));
            }

            Length = 0;
        }

        public void Dispose()
        {
            var destination = buffer;
            if (destination is null)
            {
                return;
            }

            Clear();
            buffer = null;
            ArrayPool<byte>.Shared.Return(destination, clearArray: true);
        }
    }

    private sealed record RealtimeSequenceError(
        string Code,
        string Message,
        long? ExpectedSequence,
        long? ReceivedSequence);

    private enum RealtimeApplicationSendDisposition
    {
        Started,
        PeerCloseObserved,
        TransportClosed
    }
}

internal enum RealtimeInboundMessageKind
{
    Text,
    Binary,
    PeerClose,
    Failure,
    QueueOverflow,
    Timeout
}

internal sealed class RealtimeInboundMessage : IDisposable
{
    private byte[]? ownedBuffer;

    private RealtimeInboundMessage(
        RealtimeInboundMessageKind kind,
        byte[]? ownedBuffer,
        int length,
        string? errorCode,
        string? errorMessage)
    {
        Kind = kind;
        this.ownedBuffer = ownedBuffer;
        Length = length;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ReceivedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public RealtimeInboundMessageKind Kind { get; }

    public int Length { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public long ReceivedTimestamp { get; }

    public ReadOnlyMemory<byte> Payload
        => ownedBuffer is null ? ReadOnlyMemory<byte>.Empty : ownedBuffer.AsMemory(0, Length);

    public static RealtimeInboundMessage FromOwned(
        RealtimeInboundMessageKind kind,
        byte[] buffer,
        int length)
        => new(kind, buffer, length, null, null);

    public static RealtimeInboundMessage FromOwnedFailure(
        byte[] buffer,
        string code,
        string message)
        => new(RealtimeInboundMessageKind.Failure, buffer, 0, code, message);

    public static RealtimeInboundMessage PeerClose()
        => new(RealtimeInboundMessageKind.PeerClose, null, 0, null, null);

    public static RealtimeInboundMessage Failure(string code, string message)
        => new(RealtimeInboundMessageKind.Failure, null, 0, code, message);

    public static RealtimeInboundMessage QueueOverflow()
        => new(
            RealtimeInboundMessageKind.QueueOverflow,
            null,
            0,
            "input_queue_overflow",
            "The bounded Realtime input queue is full.");

    public static RealtimeInboundMessage Timeout(string code, string message)
        => new(RealtimeInboundMessageKind.Timeout, null, 0, code, message);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref ownedBuffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal sealed record RealtimeOutboundMessage(
    ReadOnlyMemory<byte> Payload,
    WebSocketCloseStatus? CloseStatus,
    string? CloseDescription)
{
    public bool IsClose => CloseStatus is not null;

    public static RealtimeOutboundMessage Text(byte[] payload)
        => new(payload, null, null);

    public static RealtimeOutboundMessage Close(WebSocketCloseStatus status, string description)
        => new(ReadOnlyMemory<byte>.Empty, status, description);
}

internal sealed class RealtimeTransportException : Exception
{
    public RealtimeTransportException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
