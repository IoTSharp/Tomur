using System.Net.WebSockets;
using System.Text;

namespace Tomur.Realtime.Tests;

internal sealed record ScriptedWebSocketFrame(
    byte[] Payload,
    WebSocketMessageType MessageType,
    bool WaitForServerClose = false)
{
    public static ScriptedWebSocketFrame Text(string json)
        => new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    public static ScriptedWebSocketFrame Binary(byte[] payload)
        => new(payload, WebSocketMessageType.Binary);

    public static ScriptedWebSocketFrame Close()
        => new([], WebSocketMessageType.Close);

    public static ScriptedWebSocketFrame TextAfterServerClose(string json)
        => new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, WaitForServerClose: true);
}

internal sealed record SentWebSocketFrame(
    byte[] Payload,
    WebSocketMessageType MessageType,
    bool EndOfMessage);

internal sealed class ScriptedWebSocket : WebSocket
{
    private static readonly TimeSpan MaximumScriptWait = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly ScriptedWebSocketFrame[] inbound;
    private readonly List<SentWebSocketFrame> sent = [];
    private readonly bool acknowledgeServerClose;
    private readonly bool coordinatePeerClose;
    private readonly TaskCompletionSource<bool> closeOutputCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> firstApplicationSendStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> firstApplicationSendRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> peerCloseReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> applicationReceiveWaitingForServerClose =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextInbound;
    private int receiveInProgress;
    private int applicationSendCount;
    private WebSocketState state = WebSocketState.Open;
    private WebSocketCloseStatus? closeStatus;
    private string? closeStatusDescription;

    public ScriptedWebSocket(params ScriptedWebSocketFrame[] inbound)
        : this(inbound, acknowledgeServerClose: true, coordinatePeerClose: false)
    {
    }

    private ScriptedWebSocket(
        ScriptedWebSocketFrame[] inbound,
        bool acknowledgeServerClose,
        bool coordinatePeerClose)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        if (inbound.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(inbound));
        }

        this.inbound = inbound;
        this.acknowledgeServerClose = acknowledgeServerClose;
        this.coordinatePeerClose = coordinatePeerClose;
    }

    public static ScriptedWebSocket CreateSilentPeer(params ScriptedWebSocketFrame[] inbound)
        => new(inbound, acknowledgeServerClose: false, coordinatePeerClose: false);

    public static ScriptedWebSocket CreateCoordinatedPeerClose(params ScriptedWebSocketFrame[] inbound)
        => new(inbound, acknowledgeServerClose: true, coordinatePeerClose: true);

    public override WebSocketCloseStatus? CloseStatus
    {
        get
        {
            lock (gate)
            {
                return closeStatus;
            }
        }
    }

    public override string? CloseStatusDescription
    {
        get
        {
            lock (gate)
            {
                return closeStatusDescription;
            }
        }
    }

    public override WebSocketState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public override string? SubProtocol => Tomur.Realtime.RealtimeProtocol.Name;

    public bool WasAborted { get; private set; }

    public bool WasDisposed { get; private set; }

    public int ReceiveCallsAfterCloseOutput { get; private set; }

    public int ApplicationSendsAfterPeerClose { get; private set; }

    public int CloseOutputCalls { get; private set; }

    public bool SilentReceiveWasCanceled { get; private set; }

    public Task FirstApplicationSendStarted => firstApplicationSendStarted.Task;

    public Task PeerCloseReceived => peerCloseReceived.Task;

    public Task ApplicationReceiveWaitingForServerClose => applicationReceiveWaitingForServerClose.Task;

    public IReadOnlyList<SentWebSocketFrame> SentFrames
    {
        get
        {
            lock (gate)
            {
                return sent.ToArray();
            }
        }
    }

    public override void Abort()
    {
        lock (gate)
        {
            WasAborted = true;
            state = WebSocketState.Aborted;
        }

        closeOutputCompletion.TrySetResult(true);
        firstApplicationSendStarted.TrySetResult(true);
        firstApplicationSendRelease.TrySetResult(true);
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseCore(closeStatus, statusDescription, cancellationToken, closeOutputOnly: false);
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseCore(closeStatus, statusDescription, cancellationToken, closeOutputOnly: true);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        lock (gate)
        {
            WasDisposed = true;
            if (state != WebSocketState.Aborted)
            {
                state = WebSocketState.Closed;
            }
        }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        var array = buffer.Array ??
            throw new ArgumentException("The receive buffer must reference an array.", nameof(buffer));
        var result = await ReceiveCoreAsync(
                array.AsMemory(buffer.Offset, buffer.Count),
                cancellationToken)
            .ConfigureAwait(false);
        return new WebSocketReceiveResult(
            result.Count,
            result.MessageType,
            result.EndOfMessage,
            result.MessageType == WebSocketMessageType.Close
                ? WebSocketCloseStatus.NormalClosure
                : null,
            result.MessageType == WebSocketMessageType.Close
                ? "script_complete"
                : null);
    }

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
        => ReceiveCoreAsync(buffer, cancellationToken);

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        var array = buffer.Array ??
            throw new ArgumentException("The send buffer must reference an array.", nameof(buffer));
        return SendCoreAsync(
                array.AsMemory(buffer.Offset, buffer.Count),
                messageType,
                endOfMessage,
                cancellationToken)
            .AsTask();
    }

    public override ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
        => SendCoreAsync(buffer, messageType, endOfMessage, cancellationToken);

    public void ReleaseFirstApplicationSend()
    {
        // Also release deferred close delivery when setup failed before a send began.
        firstApplicationSendStarted.TrySetResult(true);
        firstApplicationSendRelease.TrySetResult(true);
    }

    private async ValueTask<ValueWebSocketReceiveResult> ReceiveCoreAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref receiveInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent receive operations are not allowed.");
        }

        try
        {
            lock (gate)
            {
                if (state == WebSocketState.CloseSent)
                {
                    ReceiveCallsAfterCloseOutput++;
                }
            }

            // At most one wait is needed before a frame or server close becomes observable.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Task? waitOperation = null;
                var waitForSilentPeer = false;
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (state is WebSocketState.Aborted or WebSocketState.Closed or WebSocketState.CloseReceived)
                    {
                        throw new WebSocketException($"Receive is not valid while the scripted socket is {state}.");
                    }

                    if (nextInbound < inbound.Length)
                    {
                        var nextFrame = inbound[nextInbound];
                        if (nextFrame.WaitForServerClose &&
                            !closeOutputCompletion.Task.IsCompleted)
                        {
                            applicationReceiveWaitingForServerClose.TrySetResult(true);
                            waitOperation = closeOutputCompletion.Task;
                        }
                        else if (coordinatePeerClose &&
                            nextFrame.MessageType == WebSocketMessageType.Close &&
                            !firstApplicationSendStarted.Task.IsCompleted)
                        {
                            waitOperation = firstApplicationSendStarted.Task;
                        }
                        else
                        {
                            return ConsumeInboundFrame(destination.Span);
                        }
                    }
                    else if (state == WebSocketState.CloseSent)
                    {
                        if (acknowledgeServerClose)
                        {
                            state = WebSocketState.Closed;
                            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                        }

                        waitForSilentPeer = true;
                    }
                    else
                    {
                        waitOperation = closeOutputCompletion.Task;
                    }
                }

                if (waitForSilentPeer)
                {
                    try
                    {
                        await Task.Delay(MaximumScriptWait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        lock (gate)
                        {
                            SilentReceiveWasCanceled = true;
                        }

                        throw;
                    }

                    throw new InvalidOperationException("The silent scripted peer wait completed without cancellation.");
                }

                if (waitOperation is null)
                {
                    throw new InvalidOperationException("The scripted receive wait was not initialized.");
                }

                await waitOperation.WaitAsync(MaximumScriptWait, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The scripted receive exceeded its bounded coordination attempts.");
        }
        finally
        {
            Volatile.Write(ref receiveInProgress, 0);
        }
    }

    private ValueWebSocketReceiveResult ConsumeInboundFrame(Span<byte> destination)
    {
        var frame = inbound[nextInbound++];
        if (frame.Payload.Length > destination.Length)
        {
            throw new InvalidOperationException("A scripted frame exceeds the receive buffer.");
        }

        if (frame.MessageType == WebSocketMessageType.Close)
        {
            state = state == WebSocketState.CloseSent
                ? WebSocketState.Closed
                : WebSocketState.CloseReceived;
            closeStatus = WebSocketCloseStatus.NormalClosure;
            closeStatusDescription = "scripted_peer_close";
            peerCloseReceived.TrySetResult(true);
        }

        frame.Payload.CopyTo(destination);
        return new ValueWebSocketReceiveResult(
            frame.Payload.Length,
            frame.MessageType,
            true);
    }

    private async ValueTask SendCoreAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        Task? waitForRelease = null;
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (messageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
            {
                applicationSendCount++;
                if (state != WebSocketState.Open)
                {
                    ApplicationSendsAfterPeerClose++;
                }

                if (coordinatePeerClose && applicationSendCount == 1)
                {
                    firstApplicationSendStarted.TrySetResult(true);
                    waitForRelease = firstApplicationSendRelease.Task;
                }
            }

            sent.Add(new SentWebSocketFrame(payload.ToArray(), messageType, endOfMessage));
        }

        if (waitForRelease is not null)
        {
            await waitForRelease.WaitAsync(MaximumScriptWait, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CloseCore(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken cancellationToken,
        bool closeOutputOnly)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closeOutputOnly)
            {
                CloseOutputCalls++;
            }

            closeStatus = status;
            closeStatusDescription = description;
            state = closeOutputOnly && state != WebSocketState.CloseReceived
                ? WebSocketState.CloseSent
                : WebSocketState.Closed;
        }

        closeOutputCompletion.TrySetResult(true);
    }
}
