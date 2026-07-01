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
            .Select(tool => ToToolBinding(
                tool,
                agentRuntime.GetToolMap().Tools.FirstOrDefault(descriptor =>
                    string.Equals(descriptor.Name, tool.Name, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        var declarationTools = CreateDeclarationTools()
            .Select(tool => ToToolBinding(
                tool,
                agentRuntime.GetToolMap().Tools.FirstOrDefault(descriptor =>
                    string.Equals(descriptor.Name, tool.Name, StringComparison.OrdinalIgnoreCase))))
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
                "POST /api/agents/chat supports tool_mode auto_read_only for bounded Tomur-planned read-only context.",
                "POST /api/agents/workflows/read-only executes a bounded Tomur read-only tool plan and can ask an Agent Framework workflow-hosted ChatClientAgent to summarize the results.",
                "GET /api/agents/telemetry exposes the local ActivitySource span and attribute draft for future OpenTelemetry wiring.",
                "Multimodal tool declarations are exposed for schema inspection but remain blocked from automatic tool-calling.",
                "Image generation and TTS are available through their dedicated OpenAI-compatible endpoints when the backend is ready.",
                "POST /api/agents/chat defaults to ChatToolMode.None and can run explicit read-only tool context when tool_mode is read_only."
            ]);
    }

    private static AgentFrameworkToolBinding ToToolBinding(
        AITool tool,
        AgentToolDescriptor? descriptor)
        => new(
            tool.Name,
            tool.Description ?? string.Empty,
            tool is AIFunction ? "Microsoft.Extensions.AI.AIFunction" : tool.GetType().FullName ?? "AITool",
            tool is BlockedToolFunction ? "blocked" : "ready",
            descriptor?.Route,
            descriptor?.InputSchema ?? ResolveToolSchema(tool),
            descriptor?.SideEffect ?? "read",
            descriptor?.Callable ?? tool is not BlockedToolFunction,
            descriptor?.RequiresConfirmation ?? false,
            descriptor?.InvocationModes ?? (tool is BlockedToolFunction ? ["blocked-agent-tool"] : ["manual-read-only", "chat-context"]));

    private static bool IsCallableInR9(AgentToolDescriptor descriptor)
        => descriptor.Name is RuntimeDiagnoseFunction.ToolName or ToolMapFunction.ToolName;

    private static string ResolveToolSchema(AITool tool)
        => tool is AIFunctionDeclaration declaration
            ? declaration.JsonSchema.GetRawText()
            : """{"type":"object","properties":{}}""";
}
