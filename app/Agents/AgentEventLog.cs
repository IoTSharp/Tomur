using System.Buffers;
using System.Security.Cryptography;
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
        => await WriteToolInvocationAsync(
                response,
                invocationKind,
                null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 写入带模型调用标识和参数指纹的工具审计事件，不持久化原始参数。
    /// </summary>
    internal async ValueTask WriteToolInvocationAsync(
        AgentToolInvokeResponse response,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await WriteAsync(
                AgentEventLogEntry.FromToolInvocation(response, invocationKind, auditContext),
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
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("model_selected")]
    public bool ModelSelected { get; init; }

    [JsonPropertyName("arguments_sha256")]
    public string? ArgumentsSha256 { get; init; }

    [JsonPropertyName("confirmation_requested")]
    public bool? ConfirmationRequested { get; init; }

    [JsonPropertyName("confirmation_effective")]
    public bool? ConfirmationEffective { get; init; }

    [JsonPropertyName("confirmation_consumed")]
    public bool? ConfirmationConsumed { get; init; }

    [JsonPropertyName("confirmation_reused")]
    public bool? ConfirmationReused { get; init; }

    public static AgentEventLogEntry FromToolInvocation(
        AgentToolInvokeResponse response,
        string invocationKind)
        => FromToolInvocation(response, invocationKind, null);

    /// <summary>
    /// 从工具响应和显式调用上下文构造持久化事件，确保调用 ID 在响应组装前也不会丢失。
    /// </summary>
    internal static AgentEventLogEntry FromToolInvocation(
        AgentToolInvokeResponse response,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext)
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
            response.Audit.Actions)
        {
            CallId = auditContext?.CallId ?? response.Audit.CallId,
            ModelSelected = auditContext?.ModelSelected ?? response.Audit.ModelSelected,
            ArgumentsSha256 = auditContext?.ArgumentsSha256 ?? response.Audit.ArgumentsSha256,
            ConfirmationRequested = auditContext?.ConfirmationRequested ?? response.Audit.ConfirmationRequested,
            ConfirmationEffective = auditContext?.ConfirmationEffective ?? response.Audit.ConfirmationEffective,
            ConfirmationConsumed = auditContext?.ConfirmationConsumed ?? response.Audit.ConfirmationConsumed,
            ConfirmationReused = auditContext?.ConfirmationReused ?? response.Audit.ConfirmationReused
        };

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

internal static class AgentToolArgumentFingerprint
{
    /// <summary>
    /// 对规范化 JSON 计算 SHA-256，小写十六进制结果可稳定关联同一组参数。
    /// </summary>
    public static string ComputeSha256(JsonElement arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonicalJson(writer, arguments);
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// 递归排序对象属性并保留数组顺序，避免空白和属性顺序改变参数指纹。
    /// </summary>
    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
