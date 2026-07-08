using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tomur.Diagnostics;

/// <summary>
/// In-memory, bounded ring buffer of host log entries with live fan-out to SSE
/// subscribers. Registered as a DI singleton and shared with
/// <see cref="RingBufferLoggerProvider"/>. Deliberately NOT <see cref="IDisposable"/>:
/// the logging provider owns disposal, so the DI container never double-disposes it.
/// </summary>
public sealed class LogBroadcastService
{
    private const int DefaultCapacity = 2000;
    private const int SubscriberChannelCapacity = 512;

    private readonly object gate = new();
    private readonly BufferedEntry[] ring;
    private int head;
    private int count;
    private long sequence;
    private long dropped;
    private readonly ConcurrentDictionary<Guid, Subscriber> subscribers = new();

    public LogBroadcastService(int capacity = DefaultCapacity)
    {
        Capacity = capacity <= 0 ? DefaultCapacity : capacity;
        ring = new BufferedEntry[Capacity];
    }

    public int Capacity { get; }

    /// <summary>
    /// Records one log entry. Called from many threads on the logging hot path:
    /// O(1) buffer write plus O(subscribers) non-blocking fan-out, never awaits.
    /// </summary>
    internal void Append(LogLevel level, string category, EventId eventId, string message, string? exception)
    {
        lock (gate)
        {
            var seq = ++sequence;
            var entry = new LogStreamEntry(
                seq,
                DateTimeOffset.UtcNow,
                level.ToString(),
                category,
                eventId.Id,
                eventId.Name,
                message,
                exception);
            var buffered = new BufferedEntry(entry, level);

            if (count == Capacity)
            {
                dropped++;
            }
            else
            {
                count++;
            }

            ring[head] = buffered;
            head = (head + 1) % Capacity;

            // Fan out inside the lock so Subscribe()'s register-then-snapshot is airtight:
            // every entry is either in a subscriber's backlog or its channel, never both/neither.
            foreach (var subscriber in subscribers.Values)
            {
                if (subscriber.Passes(level, category))
                {
                    _ = subscriber.Channel.Writer.TryWrite(entry);
                }
            }
        }
    }

    public LogRecentResponse GetRecent(int? requestedLimit, LogLevel? minLevel, string? categoryPrefix)
    {
        var limit = requestedLimit is > 0 ? Math.Clamp(requestedLimit.Value, 1, Capacity) : 200;
        long droppedSnapshot;
        var result = new List<LogStreamEntry>(Math.Min(limit, Capacity));

        lock (gate)
        {
            droppedSnapshot = dropped;
            CollectLocked(result, minLevel, categoryPrefix);
        }

        if (result.Count > limit)
        {
            result.RemoveRange(0, result.Count - limit);
        }

        return new LogRecentResponse("ok", result.Count, Capacity, droppedSnapshot, result);
    }

    /// <summary>
    /// Registers a live subscriber and captures its filtered backlog under the same
    /// lock <see cref="Append"/> uses, so the backlog-to-live handoff never drops or
    /// duplicates an entry. Dispose the returned subscription to unregister.
    /// </summary>
    public LogSubscription Subscribe(int backlogLimit, LogLevel? minLevel, string? categoryPrefix)
    {
        var channel = Channel.CreateBounded<LogStreamEntry>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriber = new Subscriber(channel, minLevel, categoryPrefix);
        var id = Guid.NewGuid();
        var backlog = new List<LogStreamEntry>(Math.Min(Math.Max(backlogLimit, 1), Capacity));

        lock (gate)
        {
            subscribers[id] = subscriber;
            CollectLocked(backlog, minLevel, categoryPrefix);
        }

        if (backlogLimit > 0 && backlog.Count > backlogLimit)
        {
            backlog.RemoveRange(0, backlog.Count - backlogLimit);
        }

        return new LogSubscription(this, id, channel.Reader, backlog);
    }

    internal void Unsubscribe(Guid id)
    {
        if (subscribers.TryRemove(id, out var subscriber))
        {
            _ = subscriber.Channel.Writer.TryComplete();
        }
    }

    /// <summary>Completes every live channel. Called by the provider on host shutdown.</summary>
    internal void CompleteAll()
    {
        foreach (var pair in subscribers)
        {
            _ = pair.Value.Channel.Writer.TryComplete();
        }

        subscribers.Clear();
    }

    public long Clear()
    {
        lock (gate)
        {
            var cleared = count;
            count = 0;
            head = 0;
            Array.Clear(ring);
            // `sequence` intentionally not reset so client-side dedupe stays monotonic.
            return cleared;
        }
    }

    // Walks the ring oldest -> newest under the lock, appending entries that pass the filter.
    private void CollectLocked(List<LogStreamEntry> target, LogLevel? minLevel, string? categoryPrefix)
    {
        var start = (head - count + Capacity) % Capacity;
        for (var i = 0; i < count; i++)
        {
            var buffered = ring[(start + i) % Capacity];
            if (Passes(buffered.Level, buffered.Entry.Category, minLevel, categoryPrefix))
            {
                target.Add(buffered.Entry);
            }
        }
    }

    private static bool Passes(LogLevel level, string category, LogLevel? minLevel, string? categoryPrefix)
        => (minLevel is null || level >= minLevel.Value)
            && (string.IsNullOrEmpty(categoryPrefix)
                || category.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase));

    private readonly record struct BufferedEntry(LogStreamEntry Entry, LogLevel Level);

    private sealed class Subscriber(Channel<LogStreamEntry> channel, LogLevel? minLevel, string? categoryPrefix)
    {
        public Channel<LogStreamEntry> Channel { get; } = channel;

        public bool Passes(LogLevel level, string category)
            => LogBroadcastService.Passes(level, category, minLevel, categoryPrefix);
    }
}

/// <summary>A live log subscription; dispose to unregister from the broadcast service.</summary>
public sealed class LogSubscription : IDisposable
{
    private readonly LogBroadcastService owner;
    private readonly Guid id;
    private int disposed;

    internal LogSubscription(
        LogBroadcastService owner,
        Guid id,
        ChannelReader<LogStreamEntry> reader,
        IReadOnlyList<LogStreamEntry> backlog)
    {
        this.owner = owner;
        this.id = id;
        Reader = reader;
        Backlog = backlog;
    }

    public ChannelReader<LogStreamEntry> Reader { get; }

    public IReadOnlyList<LogStreamEntry> Backlog { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            owner.Unsubscribe(id);
        }
    }
}

public sealed record LogStreamEntry(
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("event_id")] int EventId,
    [property: JsonPropertyName("event_name")] string? EventName,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("exception")] string? Exception);

public sealed record LogRecentResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("dropped")] long Dropped,
    [property: JsonPropertyName("entries")] IReadOnlyList<LogStreamEntry> Entries);

public sealed record LogClearResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("cleared")] long Cleared);
