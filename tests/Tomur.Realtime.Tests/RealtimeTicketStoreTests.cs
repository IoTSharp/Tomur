using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeTicketStoreTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TicketCanBeRedeemedExactlyOnce()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);

        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);
        Assert.NotNull(issue);
        Assert.StartsWith("rtt_", issue.Ticket);
        Assert.Equal(StartTime + RealtimeProtocol.TicketLifetime, issue.ExpiresAt);
        Assert.Equal(1, store.Count);

        Assert.True(store.TryRedeem(issue.Ticket, "browser:127.0.0.1"));
        Assert.False(store.TryRedeem(issue.Ticket, "browser:127.0.0.1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void TicketRemainsValidImmediatelyBeforeItsDeadline()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);
        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);

        clock.Advance(RealtimeProtocol.TicketLifetime - TimeSpan.FromTicks(1));

        Assert.True(store.TryRedeem(issue!.Ticket, "browser:127.0.0.1"));
    }

    [Fact]
    public void TicketExpiresAtItsExactDeadline()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);
        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);

        clock.Advance(RealtimeProtocol.TicketLifetime);

        Assert.False(store.TryRedeem(issue!.Ticket, "browser:127.0.0.1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void WallClockRollbackCannotExtendTicketLifetime()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);
        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);

        clock.ShiftUtc(-TimeSpan.FromHours(1));
        clock.Advance(RealtimeProtocol.TicketLifetime);

        Assert.Equal(0, store.Count);
        Assert.False(store.TryRedeem(issue!.Ticket, "browser:127.0.0.1"));
    }

    [Fact]
    public void WallClockDeadlineExpiresTicketWhenMonotonicClockDoesNotAdvance()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);
        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);

        clock.ShiftUtc(RealtimeProtocol.TicketLifetime);

        Assert.Equal(0, store.Count);
        Assert.False(store.TryRedeem(issue!.Ticket, "browser:127.0.0.1"));
    }

    [Fact]
    public void SourceMismatchRejectsAndConsumesOneTimeTicket()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);
        Assert.True(store.TryIssue("browser:127.0.0.1", out var issue, out var issueError));
        Assert.Null(issueError);

        Assert.False(store.TryRedeem(issue!.Ticket, "browser:127.0.0.2"));
        Assert.False(store.TryRedeem(issue.Ticket, "browser:127.0.0.1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void StoreEnforcesCapacityAndRecoversAfterExpiration()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);

        for (var index = 0; index < RealtimeProtocol.MaxTickets; index++)
        {
            Assert.True(store.TryIssue($"source:{index}", out var issue, out var issueError));
            Assert.NotNull(issue);
            Assert.Null(issueError);
        }

        Assert.Equal(RealtimeProtocol.MaxTickets, store.Count);
        Assert.False(store.TryIssue("source:overflow", out var rejected, out var rejectedError));
        Assert.Null(rejected);
        Assert.Equal("ticket_capacity_exceeded", rejectedError);

        clock.Advance(RealtimeProtocol.TicketLifetime);

        Assert.Equal(0, store.Count);
        Assert.True(store.TryIssue("source:after-expiry", out var replacement, out var replacementError));
        Assert.NotNull(replacement);
        Assert.Null(replacementError);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void StoreEnforcesPerSourceCapacityAndRecoversAfterExpiration()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new RealtimeTicketStore(clock);

        for (var index = 0; index < RealtimeProtocol.MaxTicketsPerSource; index++)
        {
            Assert.True(store.TryIssue("same-source", out var issue, out var issueError));
            Assert.NotNull(issue);
            Assert.Null(issueError);
        }

        Assert.False(store.TryIssue("same-source", out var rejected, out var rejectedError));
        Assert.Null(rejected);
        Assert.Equal("ticket_source_limit_reached", rejectedError);

        clock.Advance(RealtimeProtocol.TicketLifetime);

        Assert.True(store.TryIssue("same-source", out var replacement, out var replacementError));
        Assert.NotNull(replacement);
        Assert.Null(replacementError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-realtime-ticket")]
    public void MalformedTicketsAreRejected(string? ticket)
    {
        var store = new RealtimeTicketStore(new ManualTimeProvider(StartTime));

        Assert.False(store.TryRedeem(ticket, "browser:127.0.0.1"));
    }
}
