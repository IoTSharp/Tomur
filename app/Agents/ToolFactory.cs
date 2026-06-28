using Microsoft.Extensions.AI;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class ToolFactory
{
    private readonly AgentRuntimeService agentRuntime;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;

    public ToolFactory(
        AgentRuntimeService agentRuntime,
        RuntimeDiagnosticsProvider diagnosticsProvider)
    {
        this.agentRuntime = agentRuntime;
        this.diagnosticsProvider = diagnosticsProvider;
    }

    public IReadOnlyList<AITool> CreateSafeReadOnlyTools()
    {
        return
        [
            new RuntimeDiagnoseFunction(diagnosticsProvider),
            new ToolMapFunction(agentRuntime)
        ];
    }

    public IReadOnlyList<AITool> CreateDeclarationTools()
    {
        var tools = new List<AITool>(CreateSafeReadOnlyTools());
        foreach (var descriptor in agentRuntime.GetToolMap().Tools)
        {
            if (descriptor.Name == "chat.respond")
            {
                continue;
            }

            if (descriptor.Name is RuntimeDiagnoseFunction.ToolName or ToolMapFunction.ToolName)
            {
                continue;
            }

            if (!IsCallableInR9(descriptor))
            {
                tools.Add(new BlockedToolFunction(
                    descriptor,
                    "tool_not_callable",
                    $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not enabled for automatic Agent Framework execution yet.",
                    descriptor.Actions.Count == 0
                        ? ["Inspect /api/agents/tools and use the dedicated HTTP endpoint manually when ready."]
                        : descriptor.Actions));
            }
        }

        return tools;
    }

    public AgentFrameworkToolBindingResponse GetBindingStatus()
    {
        var safeTools = CreateSafeReadOnlyTools()
            .Select(static tool => ToToolBinding(tool))
            .ToArray();
        var declarationTools = CreateDeclarationTools()
            .Select(static tool => ToToolBinding(tool))
            .ToArray();

        return new AgentFrameworkToolBindingResponse(
            "partial",
            DateTimeOffset.UtcNow,
            "Microsoft.Extensions.AI.AITool",
            safeTools,
            declarationTools,
            [
                "Only read-only runtime tools are invokable in R9 without user confirmation.",
                "POST /api/agents/tools/invoke can execute runtime.diagnose and tools.inspect with audit metadata.",
                "Multimodal tool declarations are exposed for schema inspection but remain blocked from automatic tool-calling.",
                "Image generation and TTS are available through their dedicated OpenAI-compatible endpoints when the backend is ready.",
                "POST /api/agents/chat keeps ChatToolMode.None for the initial local text path."
            ]);
    }

    public async Task<AgentToolInvokeResponse> InvokeAsync(
        AgentToolInvokeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        var safeTool = CreateSafeReadOnlyTools().FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (safeTool is ILocalAgentTool invokable)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await invokable.InvokeLocalAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
            return new AgentToolInvokeResponse(
                "ok",
                safeTool.Name,
                "Microsoft.Extensions.AI.AITool",
                safeTool.GetType().FullName ?? "AITool",
                descriptor.InputSchema,
                (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
                AgentToolResultJson.ToJsonElement(result),
                [
                    "invocation: manual",
                    "scope: r9-read-only",
                    "source: Microsoft.Extensions.AI.AITool"
                ],
                new AgentToolInvokeAudit(
                    started,
                    "manual-read-only",
                    descriptor.SideEffect,
                    false,
                    descriptor.Actions));
        }

        var blocked = new BlockedToolResult(
            "tool_not_callable",
            descriptor.Name == "chat.respond"
                ? "chat.respond is exposed as POST /api/agents/chat, not as a nested tool call in R9."
                : $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not enabled for manual or automatic invocation yet.",
            descriptor.Actions.Count == 0
                ? [ResolveBlockedAction(descriptor)]
                : descriptor.Actions);

        return new AgentToolInvokeResponse(
            "blocked",
            descriptor.Name,
            "Microsoft.Extensions.AI.AITool",
            "Tomur.Agents.BlockedToolFunction",
            descriptor.InputSchema,
            0,
            AgentToolResultJson.ToJsonElement(blocked),
            [
                "invocation: manual",
                "scope: r9-controlled-boundary",
                "reason: tool requires readiness, confirmation, or a later workflow loop"
            ],
            new AgentToolInvokeAudit(
                DateTimeOffset.UtcNow,
                "manual-blocked",
                descriptor.SideEffect,
                RequiresConfirmation(descriptor),
                blocked.Actions));
    }

    private static AgentFrameworkToolBinding ToToolBinding(AITool tool)
        => new(
            tool.Name,
            tool.Description ?? string.Empty,
            tool is AIFunction ? "Microsoft.Extensions.AI.AIFunction" : tool.GetType().FullName ?? "AITool",
            tool is BlockedToolFunction ? "blocked" : "ready");

    private static bool IsCallableInR9(AgentToolDescriptor descriptor)
        => descriptor.Name is RuntimeDiagnoseFunction.ToolName or ToolMapFunction.ToolName;

    private static bool RequiresConfirmation(AgentToolDescriptor descriptor)
        => !string.Equals(descriptor.SideEffect, "none", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase);

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
