using System.Net.WebSockets;

namespace Tomur.Realtime;

internal static class RealtimeProtocol
{
    public const string Name = "tomur.realtime.v1";
    public const string WebSocketPath = "/api/realtime/v1";
    public const string TicketPath = "/api/realtime/tickets";
    public const string StatusPath = "/api/realtime/status";
    public const int Version = 1;

    public const int BinaryHeaderSize = 44;
    public const int InputSampleRate = 16_000;
    public const int InputChannels = 1;
    public const int InputFrameDurationMilliseconds = 20;
    public const int InputFramePayloadBytes = 640;
    public const int OutputSampleRate = 24_000;
    public const int OutputChannels = 1;

    public const int MaxJsonMessageBytes = 16 * 1024;
    public const int MaxMessageFragments = 32;
    public const int MaxCloseHandshakeReceiveIterations = MaxMessageFragments;
    public const int InboundQueueCapacity = 64;
    public const int OutboundControlQueueCapacity = 64;
    public const int MaxInputAudioBytes = 960_000;
    public const int MaxEventsPerSecond = 100;
    public const int MaxEventsPerSession = 50_000;
    public const int MaxPendingConnections = 8;
    public const int MaxPendingConnectionsPerSource = 2;
    public const int MaxActiveSessions = 1;
    public const int MaxTickets = 128;
    public const int MaxTicketsPerSource = 16;

    public static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumSessionDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    public static long GetMonotonicTimestampMicroseconds()
        => checked((long)(System.Diagnostics.Stopwatch.GetTimestamp()
            * (1_000_000d / System.Diagnostics.Stopwatch.Frequency)));

    public static string GetStateName(RealtimeSessionState state)
        => state switch
        {
            RealtimeSessionState.Connecting => "connecting",
            RealtimeSessionState.Listening => "listening",
            RealtimeSessionState.UserSpeaking => "user_speaking",
            RealtimeSessionState.Transcribing => "transcribing",
            RealtimeSessionState.Thinking => "thinking",
            RealtimeSessionState.Speaking => "speaking",
            RealtimeSessionState.Interrupted => "interrupted",
            RealtimeSessionState.Reconnecting => "reconnecting",
            RealtimeSessionState.Failed => "failed",
            RealtimeSessionState.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
}

internal enum RealtimeSessionState
{
    Connecting,
    Listening,
    UserSpeaking,
    Transcribing,
    Thinking,
    Speaking,
    Interrupted,
    Reconnecting,
    Failed,
    Closed
}

internal sealed class RealtimeStateMachine
{
    public RealtimeSessionState State { get; private set; } = RealtimeSessionState.Connecting;

    public bool TryTransition(RealtimeSessionState next)
    {
        if (!IsAllowed(State, next))
        {
            return false;
        }

        State = next;
        return true;
    }

    public void TransitionOrThrow(RealtimeSessionState next)
    {
        if (!TryTransition(next))
        {
            throw new InvalidOperationException(
                $"Realtime state cannot transition from {RealtimeProtocol.GetStateName(State)} to {RealtimeProtocol.GetStateName(next)}.");
        }
    }

    internal static bool IsAllowed(RealtimeSessionState current, RealtimeSessionState next)
        => current == next || (current, next) switch
        {
            (RealtimeSessionState.Connecting, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Connecting, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Connecting, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Listening, RealtimeSessionState.UserSpeaking) => true,
            (RealtimeSessionState.Listening, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Listening, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.UserSpeaking, RealtimeSessionState.Transcribing) => true,
            (RealtimeSessionState.UserSpeaking, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.UserSpeaking, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.UserSpeaking, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Transcribing, RealtimeSessionState.Thinking) => true,
            (RealtimeSessionState.Transcribing, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Transcribing, RealtimeSessionState.Interrupted) => true,
            (RealtimeSessionState.Transcribing, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Transcribing, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Thinking, RealtimeSessionState.Speaking) => true,
            (RealtimeSessionState.Thinking, RealtimeSessionState.Interrupted) => true,
            (RealtimeSessionState.Thinking, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Thinking, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Thinking, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Speaking, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Speaking, RealtimeSessionState.Interrupted) => true,
            (RealtimeSessionState.Speaking, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Speaking, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Interrupted, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Interrupted, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Interrupted, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Reconnecting, RealtimeSessionState.Listening) => true,
            (RealtimeSessionState.Reconnecting, RealtimeSessionState.Failed) => true,
            (RealtimeSessionState.Reconnecting, RealtimeSessionState.Closed) => true,
            (RealtimeSessionState.Failed, RealtimeSessionState.Closed) => true,
            _ => false
        };
}

internal enum RealtimeBinaryFrameKind : byte
{
    InputAudio = 1,
    OutputAudio = 2
}

internal enum RealtimeCloseCode
{
    AuthenticationFailed = 4001,
    ProtocolError = 4002,
    PolicyViolation = 4003,
    SessionBusy = 4004,
    Timeout = 4008,
    QueueOverflow = 4009
}

internal static class RealtimeCloseStatus
{
    public static WebSocketCloseStatus From(RealtimeCloseCode code)
        => (WebSocketCloseStatus)(int)code;
}
