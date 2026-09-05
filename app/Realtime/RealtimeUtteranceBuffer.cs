namespace Tomur.Realtime;

internal sealed class RealtimeUtteranceBuffer
{
    private readonly byte[] audio = new byte[RealtimeProtocol.MaxInputAudioBytes];
    private int length;
    private int voiced;
    private int silent;
    private const int PreRollBytes = 9600;
    public bool Speaking { get; private set; }
    public int Length => length;

    public (bool Started, bool Stopped) Push(ReadOnlySpan<byte> frame, float probability, bool manual)
    {
        if (length + frame.Length > audio.Length)
            throw new RealtimeTransportException("utterance_too_long", "The maximum utterance duration is 30 seconds.");
        frame.CopyTo(audio.AsSpan(length));
        length += frame.Length;
        voiced = probability >= 0.6f ? voiced + 20 : 0;
        silent = probability < 0.35f ? silent + 20 : 0;
        var started = !Speaking && (manual || voiced >= 100);
        if (started) { Speaking = true; silent = 0; }
        if (!Speaking && length > PreRollBytes)
        {
            audio.AsSpan(length - PreRollBytes, PreRollBytes).CopyTo(audio);
            Array.Clear(audio, PreRollBytes, length - PreRollBytes);
            length = PreRollBytes;
        }
        return (started, Speaking && !manual && silent >= 600);
    }

    public byte[] Snapshot(int maxBytes = RealtimeProtocol.MaxInputAudioBytes)
        => audio.AsSpan(Math.Max(0, length - maxBytes), Math.Min(length, maxBytes)).ToArray();

    public void Clear()
    {
        Array.Clear(audio, 0, length);
        length = voiced = silent = 0;
        Speaking = false;
    }
}

internal sealed class RealtimeTextSegmenter
{
    private readonly System.Text.StringBuilder pending = new();
    public int Length => pending.Length;
    public void Append(string delta) => pending.Append(delta);
    public string? Take(bool flush)
    {
        if (pending.Length == 0) return null;
        var count = 0;
        for (var i = 0; i < Math.Min(pending.Length, 160); i++)
        {
            if (i >= 11 && ".!?;\n\u3002\uff01\uff1f\uff1b".Contains(pending[i])) { count = i + 1; break; }
        }
        if (count == 0 && (flush || pending.Length >= 160)) count = Math.Min(pending.Length, 160);
        if (count == 0) return null;
        if (char.IsHighSurrogate(pending[count - 1])) count--;
        if (count == 0) return null;
        var text = pending.ToString(0, count);
        pending.Remove(0, count);
        return text;
    }
}
