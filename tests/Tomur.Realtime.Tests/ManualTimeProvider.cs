namespace Tomur.Realtime.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;
    private long timestamp;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        this.utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        utcNow = utcNow.Add(elapsed);
        timestamp = checked(timestamp + elapsed.Ticks);
    }

    public void ShiftUtc(TimeSpan offset)
        => utcNow = utcNow.Add(offset);
}
