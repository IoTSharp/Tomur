using Microsoft.Extensions.AI;
using Tomur.Inference;

namespace Tomur.Agents;

public sealed class ToolInvoker
{
    private readonly AgentRuntimeService agentRuntime;
    private readonly ToolFactory toolFactory;
    private readonly AgentEventLog eventLog;

    public ToolInvoker(
        AgentRuntimeService agentRuntime,
        ToolFactory toolFactory,
        AgentEventLog eventLog)
    {
        this.agentRuntime = agentRuntime;
        this.toolFactory = toolFactory;
        this.eventLog = eventLog;
    }

    public async Task<AgentToolInvokeResponse> InvokeAsync(
        AgentToolInvokeRequest request,
        CancellationToken cancellationToken)
        => await InvokeAsync(
                request,
                "manual-read-only",
                "manual",
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<AgentToolInvokeResponse> InvokeAsync(
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationKind);

        var toolName = request.Tool?.Trim();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InferenceException(
                "invalid_request",
                "The tool field is required.",
                ["Set tool to runtime.diagnose or tools.inspect for the current R9 read-only invocation path."]);
        }

        var descriptor = agentRuntime.GetToolMap().Tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new InferenceException(
                "tool_not_found",
                $"Tool '{toolName}' is not present in the Tomur Agent Framework tool map.",
                ["Use GET /api/agents/tools to inspect the current local tool map."]);
        }

        var safeTool = toolFactory.CreateSafeReadOnlyTools().FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (safeTool is ILocalAgentTool invokable)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await invokable.InvokeLocalAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
            var response = new AgentToolInvokeResponse(
                "ok",
                safeTool.Name,
                "Microsoft.Extensions.AI.AITool",
                safeTool.GetType().FullName ?? "AITool",
                descriptor.InputSchema,
                (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
                AgentToolResultJson.ToJsonElement(result),
                [
                    $"invocation: {invocationKind}",
                    "scope: r9-read-only",
                    "source: Microsoft.Extensions.AI.AITool"
                ],
                new AgentToolInvokeAudit(
                    started,
                    auditMode,
                    descriptor.SideEffect,
                    false,
                    descriptor.Actions));
            await eventLog.WriteToolInvocationAsync(response, invocationKind, cancellationToken).ConfigureAwait(false);
            return response;
        }

        var blocked = new BlockedToolResult(
            "tool_not_callable",
            descriptor.Name == "chat.respond"
                ? "chat.respond is exposed as POST /api/agents/chat, not as a nested tool call in R9."
                : $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not enabled for manual or automatic invocation yet.",
            descriptor.Actions.Count == 0
                ? [ResolveBlockedAction(descriptor)]
                : descriptor.Actions);

        var blockedResponse = new AgentToolInvokeResponse(
            "blocked",
            descriptor.Name,
            "Microsoft.Extensions.AI.AITool",
            "Tomur.Agents.BlockedToolFunction",
            descriptor.InputSchema,
            0,
            AgentToolResultJson.ToJsonElement(blocked),
            [
                $"invocation: {invocationKind}",
                "scope: r9-controlled-boundary",
                "reason: tool requires readiness, confirmation, or a later workflow loop"
            ],
            new AgentToolInvokeAudit(
                DateTimeOffset.UtcNow,
                $"{auditMode}-blocked",
                descriptor.SideEffect,
                descriptor.RequiresConfirmation,
                blocked.Actions));
        await eventLog.WriteToolInvocationAsync(blockedResponse, invocationKind, cancellationToken).ConfigureAwait(false);
        return blockedResponse;
    }

    private static string ResolveBlockedAction(AgentToolDescriptor descriptor)
        => descriptor.Name switch
        {
            "chat.respond" => "Use POST /api/agents/chat for the current plain text Agent Framework endpoint.",
            "image.generate" => "Use POST /v1/images/generations manually after the image backend passes smoke validation.",
            "vision.analyze" => "Use POST /api/vision/analyze manually with data URI input until R9 tool-calling is enabled.",
            "ocr.recognize" => "Use POST /api/ocr/analyze manually until R9 tool-calling is enabled.",
            "audio.transcribe" => "Use POST /v1/audio/transcriptions manually until R9 tool-calling is enabled.",
            "audio.speak" => "Use POST /v1/audio/speech manually after TTS smoke evidence is complete.",
            "files.search" => "Wait for the local file index and RAG flow before enabling files.search.",
            _ => "Inspect GET /api/agents/tools for the current route, readiness and diagnostic actions."
        };
}
