using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tomur.Realtime;

internal sealed record RealtimeLimitsResponse(
    int MaxJsonMessageBytes,
    int MaxMessageFragments,
    int InboundQueueCapacity,
    int OutboundControlQueueCapacity,
    int MaxInputAudioBytes,
    int MaxEventsPerSecond,
    int MaxEventsPerSession,
    int AuthenticationTimeoutMilliseconds,
    int IdleTimeoutMilliseconds,
    int MaximumSessionDurationMilliseconds,
    int SendTimeoutMilliseconds,
    int GracefulCloseTimeoutMilliseconds,
    int MaximumActiveSessions,
    int MaximumPendingConnections,
    int MaximumPendingConnectionsPerSource,
    int MaximumTickets,
    int MaximumTicketsPerSource,
    int TicketLifetimeSeconds)
{
    public static RealtimeLimitsResponse Create()
        => new(
            RealtimeProtocol.MaxJsonMessageBytes,
            RealtimeProtocol.MaxMessageFragments,
            RealtimeProtocol.InboundQueueCapacity,
            RealtimeProtocol.OutboundControlQueueCapacity,
            RealtimeProtocol.MaxInputAudioBytes,
            RealtimeProtocol.MaxEventsPerSecond,
            RealtimeProtocol.MaxEventsPerSession,
            checked((int)RealtimeProtocol.AuthenticationTimeout.TotalMilliseconds),
            checked((int)RealtimeProtocol.IdleTimeout.TotalMilliseconds),
            checked((int)RealtimeProtocol.MaximumSessionDuration.TotalMilliseconds),
            checked((int)RealtimeProtocol.SendTimeout.TotalMilliseconds),
            checked((int)RealtimeProtocol.GracefulCloseTimeout.TotalMilliseconds),
            RealtimeProtocol.MaxActiveSessions,
            RealtimeProtocol.MaxPendingConnections,
            RealtimeProtocol.MaxPendingConnectionsPerSource,
            RealtimeProtocol.MaxTickets,
            RealtimeProtocol.MaxTicketsPerSource,
            checked((int)RealtimeProtocol.TicketLifetime.TotalSeconds));
}

internal sealed record RealtimeRegistrySnapshot(
    int ActiveSessions,
    int PendingConnections,
    int MaximumActiveSessions,
    int MaximumPendingConnections);

internal sealed record RealtimeCapabilityStatus(
    string Gateway,
    string Pipeline,
    string Vad,
    string Asr,
    string Tts,
    string FullDuplex,
    string Smoke);

internal sealed record RealtimeStatusResponse(
    string Status,
    string Protocol,
    string WebSocketPath,
    string TicketPath,
    RealtimeCapabilityStatus Capabilities,
    RealtimeRegistrySnapshot Sessions,
    RealtimeLimitsResponse Limits);

internal sealed record RealtimeTicketResponse(
    string Ticket,
    DateTimeOffset ExpiresAt,
    int ExpiresInSeconds,
    string Protocol,
    string WebSocketPath,
    string AuthenticateEventType);

internal sealed record RealtimeHttpError(
    string Code,
    string Message);

internal sealed record RealtimeEventDiscriminator(
    string? Type);

internal interface IRealtimeClientEvent
{
    string? Type { get; }

    string? EventId { get; }

    long Sequence { get; }

    long TimestampUs { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeAuthenticateEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    string? Ticket) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeSessionUpdateEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    RealtimeSessionConfiguration? Session) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimePingEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeSessionCloseEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    string? Reason) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeInputAudioCommitEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    string? CaptureStreamId,
    string? UtteranceId) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeInputAudioClearEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    string? CaptureStreamId) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeResponseCancelEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    long ResponseEpoch) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeTextDisplayedEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    long ResponseEpoch,
    string? ItemId,
    int CharacterCount) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimePlaybackConsumedEvent(
    string? Type,
    string? EventId,
    long Sequence,
    long TimestampUs,
    long ResponseEpoch,
    long AudioSequence,
    long PlayedThroughTimestampUs) : IRealtimeClientEvent;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RealtimeSessionConfiguration(
    string? ConversationId,
    string? TurnDetection,
    string? InputAudioFormat,
    int InputSampleRate,
    int InputChannels,
    int InputFrameDurationMs,
    string? OutputAudioFormat,
    int OutputSampleRate,
    int OutputChannels)
{
    public static RealtimeSessionConfiguration CreateDefault()
        => new(
            null,
            "manual",
            "pcm16le",
            RealtimeProtocol.InputSampleRate,
            RealtimeProtocol.InputChannels,
            RealtimeProtocol.InputFrameDurationMilliseconds,
            "pcm16le",
            RealtimeProtocol.OutputSampleRate,
            RealtimeProtocol.OutputChannels);
}

internal sealed record RealtimeSessionCreatedEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    string Protocol,
    RealtimeSessionConfiguration Session,
    RealtimeCapabilityStatus Capabilities,
    RealtimeLimitsResponse Limits);

internal sealed record RealtimeSessionUpdatedServerEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    RealtimeSessionConfiguration Session);

internal sealed record RealtimePongEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    string ClientEventId);

internal sealed record RealtimeInputAudioStartedEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    string CaptureStreamId,
    ulong FirstSequence);

internal sealed record RealtimeInputAudioCommittedEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    string CaptureStreamId,
    string UtteranceId,
    int BufferedAudioBytes,
    int DurationMs);

internal sealed record RealtimeInputAudioClearedEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    string? CaptureStreamId,
    int DiscardedAudioFrames,
    int DiscardedAudioBytes,
    string Reason);

internal sealed record RealtimeResponseCancelledEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string SessionId,
    string TraceId,
    string State,
    long ResponseEpoch,
    string Reason);

internal sealed record RealtimeErrorEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string? SessionId,
    string TraceId,
    string State,
    string Code,
    string Message,
    bool Fatal,
    long? ExpectedSequence = null,
    long? ReceivedSequence = null,
    string? CaptureStreamId = null,
    long? ResponseEpoch = null);

internal sealed record RealtimeSessionClosedEvent(
    string Type,
    string EventId,
    long Sequence,
    long TimestampUs,
    string? SessionId,
    string TraceId,
    string State,
    string Reason,
    long ReceivedEvents,
    long ReceivedAudioFrames,
    long ReceivedAudioBytes,
    long CommittedUtterances,
    long DiscardedAudioFrames,
    long DiscardedAudioBytes);

internal sealed record RealtimeClientEventParseResult(
    IRealtimeClientEvent? Event,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Success => Event is not null;
}

internal static class RealtimeClientEventParser
{
    public static RealtimeClientEventParseResult Parse(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("invalid_event", "The control event must be a JSON object.");
            }

            if (ContainsDuplicateProperty(document.RootElement, depth: 0))
            {
                return Invalid("invalid_event", "The control event must not contain duplicate JSON properties.");
            }

            var discriminator = JsonSerializer.Deserialize(
                utf8.Span,
                RealtimeJsonSerializerContext.Default.RealtimeEventDiscriminator);
            if (string.IsNullOrWhiteSpace(discriminator?.Type))
            {
                return Invalid("invalid_event", "The control event must include a non-empty type.");
            }

            IRealtimeClientEvent? parsed = discriminator.Type switch
            {
                "session.authenticate" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeAuthenticateEvent),
                "session.update" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeSessionUpdateEvent),
                "session.ping" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimePingEvent),
                "session.close" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeSessionCloseEvent),
                "input_audio_buffer.commit" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeInputAudioCommitEvent),
                "input_audio_buffer.clear" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeInputAudioClearEvent),
                "response.cancel" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeResponseCancelEvent),
                "response.text.displayed" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimeTextDisplayedEvent),
                "response.audio.playback_consumed" => Deserialize(utf8.Span, RealtimeJsonSerializerContext.Default.RealtimePlaybackConsumedEvent),
                _ => null
            };

            if (parsed is null)
            {
                return Invalid("unsupported_event", $"The control event type is not supported by {RealtimeProtocol.Name}.");
            }

            if (!IsValidEventId(parsed.EventId))
            {
                return Invalid("invalid_event_id", "event_id must contain 1 to 64 ASCII letters, digits, '.', '_' or '-'.");
            }

            if (parsed.Sequence <= 0)
            {
                return Invalid("invalid_sequence", "sequence must be a positive integer.");
            }

            if (parsed.TimestampUs < 0)
            {
                return Invalid("invalid_timestamp", "timestamp_us must be zero or greater.");
            }

            return new RealtimeClientEventParseResult(parsed, null, null);
        }
        catch (JsonException exception)
        {
            return Invalid("invalid_event", $"The control event is not valid {RealtimeProtocol.Name} JSON: {exception.Message}");
        }
    }

    private static T? Deserialize<T>(ReadOnlySpan<byte> utf8, JsonTypeInfo<T> typeInfo)
        where T : class, IRealtimeClientEvent
        => JsonSerializer.Deserialize(utf8, typeInfo);

    private static bool IsValidEventId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' ||
                  character is >= 'A' and <= 'Z' ||
                  character is >= '0' and <= '9' ||
                  character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsDuplicateProperty(JsonElement element, int depth)
    {
        if (depth > 16)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperty(property.Value, depth + 1))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsDuplicateProperty(item, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static RealtimeClientEventParseResult Invalid(string code, string message)
        => new(null, code, message);
}
