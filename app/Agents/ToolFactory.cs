using Microsoft.Extensions.AI;
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
                "Multimodal tool declarations are exposed for schema inspection but remain blocked from automatic tool-calling.",
                "Image generation and TTS are available through their dedicated OpenAI-compatible endpoints when the backend is ready.",
                "POST /api/agents/chat keeps ChatToolMode.None for the initial local text path."
            ]);
    }

    private static AgentFrameworkToolBinding ToToolBinding(AITool tool)
        => new(
            tool.Name,
            tool.Description ?? string.Empty,
            tool is AIFunction ? "Microsoft.Extensions.AI.AIFunction" : tool.GetType().FullName ?? "AITool",
            tool is BlockedToolFunction ? "blocked" : "ready");

    private static bool IsCallableInR9(AgentToolDescriptor descriptor)
        => descriptor.Name is RuntimeDiagnoseFunction.ToolName or ToolMapFunction.ToolName;
}
