using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tomur.Diagnostics;

/// <summary>
/// Bridges the host <see cref="ILogger"/> pipeline into a shared
/// <see cref="LogBroadcastService"/>. This is the only disposable half of the pair:
/// the <c>LoggerFactory</c> disposes it exactly once on shutdown, which completes
/// every live subscriber channel so streaming SSE loops exit cleanly.
/// </summary>
[ProviderAlias("TomurRingBuffer")]
public sealed class RingBufferLoggerProvider(LogBroadcastService store) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, RingBufferLogger> loggers = new();

    public ILogger CreateLogger(string categoryName)
        => loggers.GetOrAdd(categoryName, name => new RingBufferLogger(name, store));

    public void Dispose() => store.CompleteAll();

    private sealed class RingBufferLogger(string category, LogBroadcastService store) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        // Provider-scoped filters registered in ServeCommand gate categories at the
        // factory level, so this only needs to reject the disabled sentinel.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            try
            {
                // Only the rendered string is persisted — TState is never reflected over,
                // keeping the whole path trim/AOT-safe.
                var message = formatter(state, exception);
                store.Append(logLevel, category, eventId, message, exception?.ToString());
            }
            catch
            {
                // Logging must never throw back into the caller's hot path.
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
