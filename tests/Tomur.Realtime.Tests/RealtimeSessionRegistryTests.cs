using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeSessionRegistryTests
{
    [Fact]
    public void ReservationIsPendingUntilActivated()
    {
        var registry = new RealtimeSessionRegistry();

        Assert.True(registry.TryReserve("source-a", out var lease, out var errorCode));
        using (var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease))
        {
            Assert.Null(errorCode);
            var pending = registry.GetSnapshot();
            Assert.Equal(0, pending.ActiveSessions);
            Assert.Equal(1, pending.PendingConnections);

            Assert.True(reservation.TryActivate());
            Assert.NotNull(reservation.SessionId);
            Assert.Equal(32, reservation.SessionId.Length);
            var active = registry.GetSnapshot();
            Assert.Equal(1, active.ActiveSessions);
            Assert.Equal(0, active.PendingConnections);
        }

        AssertEmpty(registry);
    }

    [Fact]
    public void RegistryEnforcesPendingConnectionsPerSource()
    {
        var registry = new RealtimeSessionRegistry();
        var leases = new List<RealtimeSessionRegistry.RealtimeConnectionLease>();
        try
        {
            for (var index = 0; index < RealtimeProtocol.MaxPendingConnectionsPerSource; index++)
            {
                Assert.True(registry.TryReserve("same-source", out var lease, out var errorCode));
                Assert.Null(errorCode);
                leases.Add(Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease));
            }

            Assert.False(registry.TryReserve("same-source", out var rejected, out var rejectedCode));
            Assert.Null(rejected);
            Assert.Equal("source_connection_limit_reached", rejectedCode);
            Assert.Equal(
                RealtimeProtocol.MaxPendingConnectionsPerSource,
                registry.GetSnapshot().PendingConnections);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        AssertEmpty(registry);
    }

    [Fact]
    public void RegistryEnforcesTotalPendingConnectionCapacity()
    {
        var registry = new RealtimeSessionRegistry();
        var leases = new List<RealtimeSessionRegistry.RealtimeConnectionLease>();
        try
        {
            for (var index = 0; index < RealtimeProtocol.MaxPendingConnections; index++)
            {
                Assert.True(registry.TryReserve($"source-{index}", out var lease, out var errorCode));
                Assert.Null(errorCode);
                leases.Add(Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease));
            }

            Assert.False(registry.TryReserve("overflow-source", out var rejected, out var rejectedCode));
            Assert.Null(rejected);
            Assert.Equal("connection_limit_reached", rejectedCode);
            Assert.Equal(
                RealtimeProtocol.MaxPendingConnections,
                registry.GetSnapshot().PendingConnections);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        AssertEmpty(registry);
    }

    [Fact]
    public void OnlyOneSessionCanBeActiveAndReleaseAllowsNextActivation()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var first, out _));
        Assert.True(registry.TryReserve("source-b", out var second, out _));
        using (var firstReservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(first))
        using (var secondReservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(second))
        {
            Assert.True(firstReservation.TryActivate());
            Assert.False(secondReservation.TryActivate());
            Assert.Null(secondReservation.SessionId);

            firstReservation.Dispose();

            Assert.True(secondReservation.TryActivate());
            Assert.NotNull(secondReservation.SessionId);
            var snapshot = registry.GetSnapshot();
            Assert.Equal(1, snapshot.ActiveSessions);
            Assert.Equal(0, snapshot.PendingConnections);
        }

        AssertEmpty(registry);
    }

    [Fact]
    public void LeaseActivationAndDisposalAreIdempotent()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("source-a", out var lease, out _));
        var reservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(lease);

        Assert.True(reservation.TryActivate());
        var sessionId = reservation.SessionId;
        Assert.False(reservation.TryActivate());
        Assert.Equal(sessionId, reservation.SessionId);

        reservation.Dispose();
        reservation.Dispose();
        Assert.False(reservation.TryActivate());
        AssertEmpty(registry);
    }

    [Fact]
    public void DisposingPendingLeaseRestoresPerSourceCapacity()
    {
        var registry = new RealtimeSessionRegistry();
        Assert.True(registry.TryReserve("same-source", out var first, out _));
        Assert.True(registry.TryReserve("same-source", out var second, out _));
        var firstReservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(first);
        var secondReservation = Assert.IsType<RealtimeSessionRegistry.RealtimeConnectionLease>(second);

        firstReservation.Dispose();

        Assert.True(registry.TryReserve("same-source", out var replacement, out var errorCode));
        Assert.Null(errorCode);
        replacement!.Dispose();
        secondReservation.Dispose();
        AssertEmpty(registry);
    }

    private static void AssertEmpty(RealtimeSessionRegistry registry)
    {
        var snapshot = registry.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(0, snapshot.PendingConnections);
        Assert.Equal(RealtimeProtocol.MaxActiveSessions, snapshot.MaximumActiveSessions);
        Assert.Equal(RealtimeProtocol.MaxPendingConnections, snapshot.MaximumPendingConnections);
    }
}
