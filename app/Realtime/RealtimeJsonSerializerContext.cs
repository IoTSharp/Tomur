using System.Text.Json.Serialization;

namespace Tomur.Realtime;

[JsonSerializable(typeof(RealtimeLimitsResponse))]
[JsonSerializable(typeof(RealtimeRegistrySnapshot))]
[JsonSerializable(typeof(RealtimeCapabilityStatus))]
[JsonSerializable(typeof(RealtimeStatusResponse))]
[JsonSerializable(typeof(RealtimeTicketResponse))]
[JsonSerializable(typeof(RealtimeHttpError))]
[JsonSerializable(typeof(RealtimeEventDiscriminator))]
[JsonSerializable(typeof(RealtimeAuthenticateEvent))]
[JsonSerializable(typeof(RealtimeSessionUpdateEvent))]
[JsonSerializable(typeof(RealtimePingEvent))]
[JsonSerializable(typeof(RealtimeSessionCloseEvent))]
[JsonSerializable(typeof(RealtimeInputAudioCommitEvent))]
[JsonSerializable(typeof(RealtimeInputAudioClearEvent))]
[JsonSerializable(typeof(RealtimeResponseCancelEvent))]
[JsonSerializable(typeof(RealtimeTextDisplayedEvent))]
[JsonSerializable(typeof(RealtimePlaybackConsumedEvent))]
[JsonSerializable(typeof(RealtimeSessionConfiguration))]
[JsonSerializable(typeof(RealtimeSessionCreatedEvent))]
[JsonSerializable(typeof(RealtimeSessionUpdatedServerEvent))]
[JsonSerializable(typeof(RealtimePongEvent))]
[JsonSerializable(typeof(RealtimeInputAudioStartedEvent))]
[JsonSerializable(typeof(RealtimeInputAudioCommittedEvent))]
[JsonSerializable(typeof(RealtimeInputAudioClearedEvent))]
[JsonSerializable(typeof(RealtimeResponseCancelledEvent))]
[JsonSerializable(typeof(RealtimeErrorEvent))]
[JsonSerializable(typeof(RealtimeSessionClosedEvent))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class RealtimeJsonSerializerContext : JsonSerializerContext;
