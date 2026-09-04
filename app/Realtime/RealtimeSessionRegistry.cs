namespace Tomur.Realtime;

internal sealed class RealtimeSessionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ConnectionEntry> connections = [];
    private readonly Dictionary<string, int> pendingBySource = new(StringComparer.Ordinal);
    private int activeSessions;
    private int pendingConnections;

    public bool TryReserve(string source, out RealtimeConnectionLease? lease, out string? errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        lock (gate)
        {
            if (pendingConnections >= RealtimeProtocol.MaxPendingConnections)
            {
                lease = null;
                errorCode = "connection_limit_reached";
                return false;
            }

            pendingBySource.TryGetValue(source, out var sourceCount);
            if (sourceCount >= RealtimeProtocol.MaxPendingConnectionsPerSource)
            {
                lease = null;
                errorCode = "source_connection_limit_reached";
                return false;
            }

            Guid connectionId = default;
            var added = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                connectionId = Guid.NewGuid();
                if (connections.TryAdd(connectionId, new ConnectionEntry(source)))
                {
                    added = true;
                    break;
                }
            }

            if (!added)
            {
                lease = null;
                errorCode = "connection_id_unavailable";
                return false;
            }

            pendingBySource[source] = sourceCount + 1;
            pendingConnections++;
            lease = new RealtimeConnectionLease(this, connectionId, source);
            errorCode = null;
            return true;
        }
    }

    public RealtimeRegistrySnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new RealtimeRegistrySnapshot(
                activeSessions,
                pendingConnections,
                RealtimeProtocol.MaxActiveSessions,
                RealtimeProtocol.MaxPendingConnections);
        }
    }

    private bool TryActivate(Guid connectionId, out string? sessionId)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var entry) || entry.Active)
            {
                sessionId = null;
                return false;
            }

            if (activeSessions >= RealtimeProtocol.MaxActiveSessions)
            {
                sessionId = null;
                return false;
            }

            entry.Active = true;
            entry.SessionId = Guid.NewGuid().ToString("N");
            activeSessions++;
            pendingConnections--;
            DecrementPendingSource(entry.Source);
            sessionId = entry.SessionId;
            return true;
        }
    }

    private void Release(Guid connectionId)
    {
        lock (gate)
        {
            if (!connections.Remove(connectionId, out var entry))
            {
                return;
            }

            if (entry.Active)
            {
                activeSessions--;
            }
            else
            {
                pendingConnections--;
                DecrementPendingSource(entry.Source);
            }
        }
    }

    private void DecrementPendingSource(string source)
    {
        if (!pendingBySource.TryGetValue(source, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            pendingBySource.Remove(source);
        }
        else
        {
            pendingBySource[source] = count - 1;
        }
    }

    private sealed class ConnectionEntry
    {
        public ConnectionEntry(string source)
        {
            Source = source;
        }

        public string Source { get; }

        public bool Active { get; set; }

        public string? SessionId { get; set; }
    }

    internal sealed class RealtimeConnectionLease : IDisposable
    {
        private readonly object gate = new();
        private RealtimeSessionRegistry? owner;
        private readonly Guid connectionId;

        internal RealtimeConnectionLease(RealtimeSessionRegistry owner, Guid connectionId, string source)
        {
            this.owner = owner;
            this.connectionId = connectionId;
            Source = source;
        }

        public string Source { get; }

        public string? SessionId { get; private set; }

        public bool TryActivate()
        {
            lock (gate)
            {
                var registry = owner;
                if (registry is null || SessionId is not null || !registry.TryActivate(connectionId, out var sessionId))
                {
                    return false;
                }

                SessionId = sessionId;
                return true;
            }
        }

        public void Dispose()
        {
            RealtimeSessionRegistry? registry;
            lock (gate)
            {
                registry = owner;
                owner = null;
            }

            registry?.Release(connectionId);
        }
    }
}
