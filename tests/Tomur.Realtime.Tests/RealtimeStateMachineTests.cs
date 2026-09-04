using Tomur.Realtime;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeStateMachineTests
{
    [Fact]
    public void StateMachineFollowsTheManualTurnLifecycle()
    {
        var machine = new RealtimeStateMachine();

        Assert.Equal(RealtimeSessionState.Connecting, machine.State);
        Assert.True(machine.TryTransition(RealtimeSessionState.Listening));
        Assert.True(machine.TryTransition(RealtimeSessionState.UserSpeaking));
        Assert.True(machine.TryTransition(RealtimeSessionState.Transcribing));
        Assert.True(machine.TryTransition(RealtimeSessionState.Thinking));
        Assert.True(machine.TryTransition(RealtimeSessionState.Speaking));
        Assert.True(machine.TryTransition(RealtimeSessionState.Interrupted));
        Assert.True(machine.TryTransition(RealtimeSessionState.Listening));
        Assert.True(machine.TryTransition(RealtimeSessionState.Closed));
        Assert.Equal(RealtimeSessionState.Closed, machine.State);
    }

    [Fact]
    public void InvalidTransitionIsRejectedWithoutChangingState()
    {
        var machine = new RealtimeStateMachine();

        var transitioned = machine.TryTransition(RealtimeSessionState.Speaking);

        Assert.False(transitioned);
        Assert.Equal(RealtimeSessionState.Connecting, machine.State);
    }

    [Fact]
    public void TransitionOrThrowReportsStableStateNames()
    {
        var machine = new RealtimeStateMachine();

        var exception = Assert.Throws<InvalidOperationException>(
            () => machine.TransitionOrThrow(RealtimeSessionState.Speaking));

        Assert.Contains("connecting", exception.Message, StringComparison.Ordinal);
        Assert.Contains("speaking", exception.Message, StringComparison.Ordinal);
        Assert.Equal(RealtimeSessionState.Connecting, machine.State);
    }

    [Fact]
    public void SameStateTransitionsAreIdempotentIncludingClosed()
    {
        var machine = new RealtimeStateMachine();

        Assert.True(machine.TryTransition(RealtimeSessionState.Connecting));
        Assert.True(machine.TryTransition(RealtimeSessionState.Closed));
        Assert.True(machine.TryTransition(RealtimeSessionState.Closed));
        Assert.Equal(RealtimeSessionState.Closed, machine.State);
    }

    [Fact]
    public void FailedAndReconnectingStatesHaveOnlyFrozenRecoveryTransitions()
    {
        Assert.True(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Reconnecting,
            RealtimeSessionState.Listening));
        Assert.True(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Reconnecting,
            RealtimeSessionState.Failed));
        Assert.True(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Reconnecting,
            RealtimeSessionState.Closed));
        Assert.False(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Reconnecting,
            RealtimeSessionState.Speaking));

        Assert.True(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Failed,
            RealtimeSessionState.Closed));
        Assert.False(RealtimeStateMachine.IsAllowed(
            RealtimeSessionState.Failed,
            RealtimeSessionState.Listening));
    }

    [Fact]
    public void EveryProtocolStateHasItsFrozenWireName()
    {
        Assert.Equal("connecting", RealtimeProtocol.GetStateName(RealtimeSessionState.Connecting));
        Assert.Equal("listening", RealtimeProtocol.GetStateName(RealtimeSessionState.Listening));
        Assert.Equal("user_speaking", RealtimeProtocol.GetStateName(RealtimeSessionState.UserSpeaking));
        Assert.Equal("transcribing", RealtimeProtocol.GetStateName(RealtimeSessionState.Transcribing));
        Assert.Equal("thinking", RealtimeProtocol.GetStateName(RealtimeSessionState.Thinking));
        Assert.Equal("speaking", RealtimeProtocol.GetStateName(RealtimeSessionState.Speaking));
        Assert.Equal("interrupted", RealtimeProtocol.GetStateName(RealtimeSessionState.Interrupted));
        Assert.Equal("reconnecting", RealtimeProtocol.GetStateName(RealtimeSessionState.Reconnecting));
        Assert.Equal("failed", RealtimeProtocol.GetStateName(RealtimeSessionState.Failed));
        Assert.Equal("closed", RealtimeProtocol.GetStateName(RealtimeSessionState.Closed));
    }
}
