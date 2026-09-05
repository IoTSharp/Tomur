using System.Security.Cryptography;
using System.Text;

namespace Tomur.Realtime;

internal sealed record RealtimeTicketIssue(
    string Ticket,
    DateTimeOffset ExpiresAt);

internal sealed class RealtimeTicketStore
{
    private const string TicketPrefix = "rtt_";

    private readonly object gate = new();
    private readonly Dictionary<string, TicketEntry> tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public RealtimeTicketStore(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public bool TryIssue(
        string source,
        out RealtimeTicketIssue? issue,
        out string? errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var timestamp = timeProvider.GetTimestamp();
            RemoveExpired(now, timestamp);

            var sourceTicketCount = 0;
            var inspected = 0;
            foreach (var entry in tickets.Values)
            {
                if (++inspected > RealtimeProtocol.MaxTickets)
                {
                    break;
                }

                if (string.Equals(entry.Source, source, StringComparison.Ordinal))
                {
                    sourceTicketCount++;
                }
            }

            if (sourceTicketCount >= RealtimeProtocol.MaxTicketsPerSource)
            {
                issue = null;
                errorCode = "ticket_source_limit_reached";
                return false;
            }

            if (tickets.Count >= RealtimeProtocol.MaxTickets)
            {
                issue = null;
                errorCode = "ticket_capacity_exceeded";
                return false;
            }

            Span<byte> random = stackalloc byte[32];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    RandomNumberGenerator.Fill(random);
                }
                catch (CryptographicException)
                {
                    issue = null;
                    errorCode = "ticket_generation_failed";
                    return false;
                }

                var ticket = TicketPrefix + Base64UrlEncode(random);
                var expiresAt = now.Add(RealtimeProtocol.TicketLifetime);
                if (tickets.TryAdd(Hash(ticket), new TicketEntry(source, expiresAt, timestamp)))
                {
                    issue = new RealtimeTicketIssue(ticket, expiresAt);
                    errorCode = null;
                    return true;
                }
            }

            issue = null;
            errorCode = "ticket_generation_failed";
            return false;
        }
    }

    public bool TryRedeem(string? ticket, string source)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 96 ||
            string.IsNullOrWhiteSpace(source) || !ticket.StartsWith(TicketPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var timestamp = timeProvider.GetTimestamp();
            RemoveExpired(now, timestamp);
            var tokenHash = Hash(ticket);
            if (!tickets.Remove(tokenHash, out var entry))
            {
                return false;
            }

            return !IsExpired(entry, now, timestamp) &&
                   string.Equals(entry.Source, source, StringComparison.Ordinal);
        }
    }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                RemoveExpired(timeProvider.GetUtcNow(), timeProvider.GetTimestamp());
                return tickets.Count;
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now, long timestamp)
    {
        List<string>? expired = null;
        var inspected = 0;
        foreach (var pair in tickets)
        {
            if (++inspected > RealtimeProtocol.MaxTickets)
            {
                break;
            }

            if (IsExpired(pair.Value, now, timestamp))
            {
                (expired ??= []).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            tickets.Remove(key);
        }
    }

    private bool IsExpired(TicketEntry entry, DateTimeOffset now, long timestamp)
        => entry.ExpiresAt <= now ||
           timeProvider.GetElapsedTime(entry.IssuedTimestamp, timestamp) >= RealtimeProtocol.TicketLifetime;

    private static string Hash(string ticket)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record TicketEntry(
        string Source,
        DateTimeOffset ExpiresAt,
        long IssuedTimestamp);
}
