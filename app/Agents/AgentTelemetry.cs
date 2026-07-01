using System.Diagnostics;
using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class AgentTelemetry : IDisposable
{
    public const string SourceName = "Tomur.Agents";

    private readonly ActivitySource source = new(SourceName);

    public Activity? StartChat(AgentChatRequest request, string runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);

        var activity = source.StartActivity("tomur.agent.chat", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("tomur.agent.event", "agent_chat");
        activity.SetTag("tomur.agent.runtime", runtime);
        activity.SetTag("tomur.agent.model", NormalizeTagValue(request.Model));
        activity.SetTag("tomur.agent.tool_mode", NormalizeTagValue(request.ToolMode));
        activity.SetTag("tomur.agent.requested_tool_count", request.Tools?.Count ?? 0);
        activity.SetTag("tomur.agent.max_tool_rounds", request.MaxToolRounds);
        return activity;
    }

    public void CompleteChat(Activity? activity, AgentChatResponse response)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("tomur.agent.status", response.Status);
        activity.SetTag("tomur.agent.model", NormalizeTagValue(response.Model));
        activity.SetTag("tomur.agent.tool_mode", response.ToolMode);
        activity.SetTag("tomur.agent.tool_rounds", response.ToolRounds);
        activity.SetTag("tomur.agent.elapsed_ms", response.ElapsedMs);
        activity.SetTag("tomur.agent.blocked", HasBlockedToolCalls(response.ToolCalls));
        SetActivityStatus(activity, response.Status);
    }

    public Activity? StartWorkflow(AgentReadOnlyWorkflowRequest request, string runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);

        var activity = source.StartActivity("tomur.agent.read_only_workflow", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("tomur.agent.event", "read_only_workflow");
        activity.SetTag("tomur.agent.runtime", runtime);
        activity.SetTag("tomur.agent.model", NormalizeTagValue(request.Model));
        activity.SetTag("tomur.agent.requested_tool_count", request.Tools?.Count ?? 0);
        activity.SetTag("tomur.agent.max_tool_rounds", request.MaxToolRounds);
        activity.SetTag("tomur.agent.respond", request.Respond);
        return activity;
    }

    public void CompleteWorkflow(Activity? activity, AgentReadOnlyWorkflowResponse response)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("tomur.agent.status", response.Status);
        activity.SetTag("tomur.agent.model", NormalizeTagValue(response.Model));
        activity.SetTag("tomur.agent.step_count", response.StepCount);
        activity.SetTag("tomur.agent.elapsed_ms", response.ElapsedMs);
        activity.SetTag("tomur.agent.blocked", HasBlockedToolCalls(response.Steps));
        SetActivityStatus(activity, response.Status);
    }

    public Activity? StartToolInvocation(string toolName, string invocationKind, string auditMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditMode);

        var activity = source.StartActivity("tomur.agent.tool_invocation", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("tomur.agent.event", "tool_invocation");
        activity.SetTag("tomur.agent.tool.name", toolName);
        activity.SetTag("tomur.agent.invocation_kind", invocationKind);
        activity.SetTag("tomur.agent.audit_mode", auditMode);
        return activity;
    }

    public void CompleteToolInvocation(Activity? activity, AgentToolInvokeResponse response)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("tomur.agent.status", response.Status);
        activity.SetTag("tomur.agent.tool.name", response.Tool);
        activity.SetTag("tomur.agent.tool.type", response.ToolType);
        activity.SetTag("tomur.agent.tool.implementation", response.Implementation);
        activity.SetTag("tomur.agent.elapsed_ms", response.ElapsedMs);
        activity.SetTag("tomur.agent.blocked", string.Equals(response.Status, "blocked", StringComparison.OrdinalIgnoreCase));
        activity.SetTag("tomur.agent.side_effect", response.Audit.SideEffect);
        activity.SetTag("tomur.agent.requires_confirmation", response.Audit.RequiresConfirmation);
        SetActivityStatus(activity, response.Status);
    }

    public void RecordError(
        string eventName,
        string? mode,
        string? tool,
        string? runtime,
        string? model,
        RuntimeDiagnostic diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(diagnostic);

        using var activity = source.StartActivity("tomur.agent.error", ActivityKind.Internal);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("tomur.agent.event", eventName);
        activity.SetTag("tomur.agent.status", "error");
        activity.SetTag("tomur.agent.mode", NormalizeTagValue(mode));
        activity.SetTag("tomur.agent.tool.name", NormalizeTagValue(tool));
        activity.SetTag("tomur.agent.runtime", NormalizeTagValue(runtime));
        activity.SetTag("tomur.agent.model", NormalizeTagValue(model));
        activity.SetTag("tomur.agent.diagnostic.code", diagnostic.Code);
        activity.SetTag("tomur.agent.diagnostic.status", diagnostic.Status);
        activity.SetStatus(ActivityStatusCode.Error, diagnostic.Code);
    }

    public AgentTelemetryStatus GetStatus(string localEventLogPath)
        => new(
            "draft",
            DateTimeOffset.UtcNow,
            SourceName,
            "System.Diagnostics.ActivitySource",
            string.IsNullOrWhiteSpace(localEventLogPath) ? null : localEventLogPath,
            [
                new AgentTelemetrySpanDescriptor(
                    "tomur.agent.chat",
                    "agent_chat",
                    "Agent Framework text chat request through the local IChatClient boundary.",
                    [
                        "tomur.agent.runtime",
                        "tomur.agent.model",
                        "tomur.agent.tool_mode",
                        "tomur.agent.tool_rounds",
                        "tomur.agent.elapsed_ms",
                        "tomur.agent.blocked",
                        "tomur.agent.status"
                    ]),
                new AgentTelemetrySpanDescriptor(
                    "tomur.agent.tool_invocation",
                    "tool_invocation",
                    "Controlled local tool invocation, currently limited to R9 read-only tools unless blocked.",
                    [
                        "tomur.agent.tool.name",
                        "tomur.agent.invocation_kind",
                        "tomur.agent.side_effect",
                        "tomur.agent.requires_confirmation",
                        "tomur.agent.elapsed_ms",
                        "tomur.agent.blocked",
                        "tomur.agent.status"
                    ]),
                new AgentTelemetrySpanDescriptor(
                    "tomur.agent.read_only_workflow",
                    "read_only_workflow",
                    "Bounded read-only workflow plan with optional Agent Framework summary step.",
                    [
                        "tomur.agent.runtime",
                        "tomur.agent.model",
                        "tomur.agent.step_count",
                        "tomur.agent.elapsed_ms",
                        "tomur.agent.blocked",
                        "tomur.agent.status"
                    ]),
                new AgentTelemetrySpanDescriptor(
                    "tomur.agent.error",
                    "agent_error",
                    "Structured failure span for agent endpoints and tool invocation failures.",
                    [
                        "tomur.agent.event",
                        "tomur.agent.mode",
                        "tomur.agent.tool.name",
                        "tomur.agent.runtime",
                        "tomur.agent.model",
                        "tomur.agent.diagnostic.code",
                        "tomur.agent.diagnostic.status"
                    ])
            ],
            [
                new AgentTelemetryAttributeDescriptor("tomur.agent.model", "string", "bounded", "Requested or selected local model id."),
                new AgentTelemetryAttributeDescriptor("tomur.agent.tool.name", "string", "bounded", "Tomur tool-map name, such as runtime.diagnose or tools.inspect."),
                new AgentTelemetryAttributeDescriptor("tomur.agent.status", "string", "low", "ok, partial, blocked or error."),
                new AgentTelemetryAttributeDescriptor("tomur.agent.blocked", "boolean", "low", "Whether the operation hit the R9 safety boundary."),
                new AgentTelemetryAttributeDescriptor("tomur.agent.elapsed_ms", "int64", "high", "Operation duration in milliseconds."),
                new AgentTelemetryAttributeDescriptor("tomur.agent.diagnostic.code", "string", "bounded", "Structured Tomur diagnostic code for failures.")
            ],
            [
                "No user message body, prompt text or full tool result is added to ActivitySource tags.",
                "The local JSONL event log remains available through GET /api/agents/events.",
                "External OpenTelemetry exporters are not configured by Tomur by default.",
                "Side-effect multimodal tools remain blocked from automatic Agent Framework execution until their R8 smoke evidence and R9 confirmation loop are complete."
            ]);

    public void Dispose()
        => source.Dispose();

    private static bool HasBlockedToolCalls(IReadOnlyList<AgentChatToolCall> toolCalls)
        => toolCalls.Any(static tool => string.Equals(tool.Status, "blocked", StringComparison.OrdinalIgnoreCase));

    private static void SetActivityStatus(Activity activity, string status)
    {
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            activity.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, status);
    }

    private static string? NormalizeTagValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AgentTelemetryStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("source_name")] string SourceName,
    [property: JsonPropertyName("instrumentation")] string Instrumentation,
    [property: JsonPropertyName("local_event_log")] string? LocalEventLog,
    [property: JsonPropertyName("spans")] IReadOnlyList<AgentTelemetrySpanDescriptor> Spans,
    [property: JsonPropertyName("attributes")] IReadOnlyList<AgentTelemetryAttributeDescriptor> Attributes,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

public sealed record AgentTelemetrySpanDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("attributes")] IReadOnlyList<string> Attributes);

public sealed record AgentTelemetryAttributeDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("cardinality")] string Cardinality,
    [property: JsonPropertyName("description")] string Description);
