using Tomur.Inference;

namespace Tomur.Realtime;

// Acquired before any model/session lock. Realtime owns a reservation for its
// entire lifetime, including native cancellation and handle destruction.
internal static class RealtimeResourceCoordinator
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Reservation?> Current = new();
    private static Reservation? active;
    private static int operations;

    public static Reservation Reserve(CancellationTokenSource cancellation)
    {
        lock (Gate)
        {
            if (active is not null || operations != 0)
                throw Busy();
            return active = new Reservation(cancellation);
        }
    }

    public static IDisposable EnterOperation(bool interrupt = false)
    {
        lock (Gate)
        {
            if (active is not null && Current.Value != active)
            {
                if (interrupt) active.Cancellation.Cancel();
                throw Busy();
            }
            operations++;
            return new Operation();
        }
    }

    private static InferenceException Busy() => new("realtime_resource_busy",
        "Local inference resources are reserved or still being released. End the active operation and retry.",
        ["Use /api/realtime/status to inspect the active voice session."]);

    internal sealed class Reservation(CancellationTokenSource cancellation) : IDisposable
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        public IDisposable Enter()
        {
            var previous = Current.Value;
            Current.Value = this;
            return new Scope(previous);
        }
        public void Dispose()
        {
            lock (Gate)
            {
                if (active == this) active = null;
            }
        }
    }

    private sealed class Scope(Reservation? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class Operation : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lock (Gate) operations--;
        }
    }
}
