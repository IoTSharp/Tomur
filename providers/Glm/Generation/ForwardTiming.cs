using System.Diagnostics;

namespace Tomur.Providers.Glm;

internal sealed class ForwardTiming
{
    private long embeddingTicks;
    private long attentionTicks;
    private long denseTicks;
    private long moeTicks;
    private long projectionTicks;
    private long batchCount;
    private long tokenCount;

    public void AddEmbedding(TimeSpan elapsed) => Interlocked.Add(ref embeddingTicks, elapsed.Ticks);

    public void AddAttention(TimeSpan elapsed) => Interlocked.Add(ref attentionTicks, elapsed.Ticks);

    public void AddDense(TimeSpan elapsed) => Interlocked.Add(ref denseTicks, elapsed.Ticks);

    public void AddMoe(TimeSpan elapsed) => Interlocked.Add(ref moeTicks, elapsed.Ticks);

    public void AddProjection(TimeSpan elapsed) => Interlocked.Add(ref projectionTicks, elapsed.Ticks);

    public void AddBatch(int tokens)
    {
        Interlocked.Increment(ref batchCount);
        Interlocked.Add(ref tokenCount, tokens);
    }

    public ForwardTimingSnapshot Snapshot()
        => new(
            TimeSpan.FromTicks(Interlocked.Read(ref embeddingTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref attentionTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref denseTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref moeTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref projectionTicks)),
            Interlocked.Read(ref batchCount),
            Interlocked.Read(ref tokenCount));
}

internal readonly record struct ForwardTimingSnapshot(
    TimeSpan Embedding,
    TimeSpan Attention,
    TimeSpan Dense,
    TimeSpan Moe,
    TimeSpan Projection,
    long BatchCount,
    long TokenCount)
{
    public bool IsEmpty => BatchCount == 0 && TokenCount == 0;

    public override string ToString()
        => $"timing embedding={Embedding.TotalMilliseconds:F2}ms attention={Attention.TotalMilliseconds:F2}ms " +
           $"dense={Dense.TotalMilliseconds:F2}ms moe={Moe.TotalMilliseconds:F2}ms " +
           $"projection={Projection.TotalMilliseconds:F2}ms batches={BatchCount} tokens={TokenCount}";
}

internal sealed record ForwardProgressSnapshot(
    string Stage,
    int Layer,
    int LayerCount,
    int BatchTokens,
    long CompletedTokens,
    TimeSpan Elapsed);

internal static class ForwardTimingScope
{
    public static long Start() => Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);
}
