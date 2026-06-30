using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tomur.Config;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Agents;

public sealed class AgentEventLog
{
    private const string LogFileName = "agents.jsonl";
    private readonly DataPaths paths;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AgentEventLog(DataPaths paths)
    {
        this.paths = paths;
    }

    public string LogPath => Path.Combine(paths.LogsDirectory, LogFileName);

    public async ValueTask WriteToolInvocationAsync(
        AgentToolInvokeResponse response,
        string invocationKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await WriteAsync(
                AgentEventLogEntry.FromToolInvocation(response, invocationKind),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask WriteChatAsync(
        AgentChatResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await WriteAsync(
                AgentEventLogEntry.FromChat(response),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask WriteWorkflowAsync(
        AgentReadOnlyWorkflowResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await WriteAsync(
                AgentEventLogEntry.FromWorkflow(response),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask WriteErrorAsync(
        string eventName,
        string? mode,
        string? tool,
        string? runtime,
        string? model,
        RuntimeDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(diagnostic);

        await WriteAsync(
                AgentEventLogEntry.FromError(eventName, mode, tool, runtime, model, diagnostic),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public AgentEventLogRecentResponse ReadRecent(int? requestedLimit)
    {
        var limit = requestedLimit is > 0
            ? Math.Clamp(requestedLimit.Value, 1, 200)
            : 50;

        try
        {
            if (!File.Exists(LogPath))
            {
                return new AgentEventLogRecentResponse("empty", LogPath, 0, []);
            }

            var lines = new Queue<string>(limit);
            foreach (var line in File.ReadLines(LogPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (lines.Count == limit)
                {
                    _ = lines.Dequeue();
                }

                lines.Enqueue(line);
            }

            var entries = new List<AgentEventLogEntry>(lines.Count);
            foreach (var line in lines)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(
                        line,
                        AppJsonSerializerContext.Default.AgentEventLogEntry);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // Keep the endpoint useful even if a previous process wrote a partial line.
                }
            }

            return new AgentEventLogRecentResponse("ok", LogPath, entries.Count, entries);
        }
        catch (Exception exception) when (IsNonCriticalLogException(exception))
        {
            var entry = new AgentEventLogEntry(
                AgentEventLogIds.NewId(),
                DateTimeOffset.UtcNow,
                "event_log_read",
                "error",
                "read",
                null,
                "Tomur.Agents.AgentEventLog",
                null,
                0,
                false,
                "read",
                false,
                null,
                null,
                [$"event-log: {exception.Message}"],
                ["Inspect the configured logs directory permissions and available disk space."]);
            return new AgentEventLogRecentResponse("error", LogPath, 1, [entry]);
        }
    }

    private async ValueTask WriteAsync(
        AgentEventLogEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            var json = JsonSerializer.Serialize(
                entry,
                AppJsonSerializerContext.Default.AgentEventLogEntry);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(
                        LogPath,
                        json + Environment.NewLine,
                        Encoding.UTF8,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _ = gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsNonCriticalLogException(exception))
        {
        }
    }

    private static bool IsNonCriticalLogException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}

public sealed record AgentEventLogRecentResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("events")] IReadOnlyList<AgentEventLogEntry> Events);

public sealed record AgentEventLogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("blocked")] bool Blocked,
    [property: JsonPropertyName("side_effect")] string? SideEffect,
    [property: JsonPropertyName("requires_confirmation")] bool? RequiresConfirmation,
    [property: JsonPropertyName("tool_rounds")] int? ToolRounds,
    [property: JsonPropertyName("step_count")] int? StepCount,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions)
{
    public static AgentEventLogEntry FromToolInvocation(
        AgentToolInvokeResponse response,
        string invocationKind)
        => new(
            AgentEventLogIds.NewId(),
            DateTimeOffset.UtcNow,
            "tool_invocation",
            response.Status,
            string.IsNullOrWhiteSpace(invocationKind) ? response.Audit.Mode : invocationKind,
            response.Tool,
            response.Implementation,
            null,
            response.ElapsedMs,
            string.Equals(response.Status, "blocked", StringComparison.OrdinalIgnoreCase),
            response.Audit.SideEffect,
            response.Audit.RequiresConfirmation,
            null,
            null,
            response.Diagnostics,
            response.Audit.Actions);

    public static AgentEventLogEntry FromChat(AgentChatResponse response)
        => new(
            AgentEventLogIds.NewId(),
            DateTimeOffset.UtcNow,
            "agent_chat",
            response.Status,
            response.ToolMode,
            null,
            response.Runtime,
            response.Model,
            response.ElapsedMs,
            response.ToolCalls.Any(static tool => string.Equals(tool.Status, "blocked", StringComparison.OrdinalIgnoreCase)),
            null,
            null,
            response.ToolRounds,
            null,
            response.Diagnostics,
            []);

    public static AgentEventLogEntry FromWorkflow(AgentReadOnlyWorkflowResponse response)
        => new(
            AgentEventLogIds.NewId(),
            DateTimeOffset.UtcNow,
            "read_only_workflow",
            response.Status,
            "workflow",
            null,
            response.Runtime,
            response.Model,
            response.ElapsedMs,
            response.Steps.Any(static step => string.Equals(step.Status, "blocked", StringComparison.OrdinalIgnoreCase)),
            null,
            null,
            null,
            response.StepCount,
            response.Diagnostics,
            []);

    public static AgentEventLogEntry FromError(
        string eventName,
        string? mode,
        string? tool,
        string? runtime,
        string? model,
        RuntimeDiagnostic diagnostic)
        => new(
            AgentEventLogIds.NewId(),
            DateTimeOffset.UtcNow,
            eventName,
            "error",
            mode,
            tool,
            runtime,
            model,
            0,
            false,
            "none",
            false,
            null,
            null,
            [$"{diagnostic.Code}: {diagnostic.Message}"],
            diagnostic.Actions);

}

internal static class AgentEventLogIds
{
    public static string NewId()
        => Guid.NewGuid().ToString("n");
}
