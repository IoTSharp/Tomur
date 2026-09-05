using System.Text.Json.Serialization;

namespace Tomur.Realtime;

public sealed record RealtimeRuntimeSnapshot(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("models_resident")] bool ModelsResident,
    [property: JsonPropertyName("asr_warm")] bool AsrWarm,
    [property: JsonPropertyName("tts_warm")] bool TtsWarm,
    [property: JsonPropertyName("full_duplex")] string FullDuplex,
    [property: JsonPropertyName("smoke")] string Smoke,
    [property: JsonPropertyName("input_buffered_bytes")] int InputBufferedBytes,
    [property: JsonPropertyName("output_unconsumed_ms")] long OutputUnconsumedMs,
    [property: JsonPropertyName("turns")] int Turns,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("asr_final_ms")] long? AsrFinalMs,
    [property: JsonPropertyName("first_audio_ms")] long? FirstAudioMs);

internal static class RealtimeDiagnostics
{
    private static readonly object Gate = new();
    private static RealtimeRuntimeSnapshot current = Empty();
    private static RealtimeRuntimeSnapshot Empty() => new("closed", false, false, false,
        "degraded_unverified", "pending", 0, 0, 0, null, null, null);
    public static RealtimeRuntimeSnapshot Snapshot { get { lock (Gate) return current; } }
    public static void Start() { lock (Gate) current = Empty() with { State = "connecting" }; }
    public static void Update(Func<RealtimeRuntimeSnapshot, RealtimeRuntimeSnapshot> update)
    {
        lock (Gate) current = update(current);
    }
}
