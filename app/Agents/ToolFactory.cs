using Microsoft.Extensions.AI;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class ToolFactory
{
    private readonly AgentRuntimeService agentRuntime;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;
    private readonly FileIndexStore fileIndex;

    public ToolFactory(
        AgentRuntimeService agentRuntime,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        FileIndexStore fileIndex)
    {
        this.agentRuntime = agentRuntime;
        this.diagnosticsProvider = diagnosticsProvider;
        this.fileIndex = fileIndex;
    }

    public IReadOnlyList<AITool> CreateSafeReadOnlyTools()
    {
        return
        [
            new RuntimeDiagnoseFunction(diagnosticsProvider),
            new ToolMapFunction(agentRuntime),
            new FileSearchFunction(fileIndex)
        ];
    }

    public IReadOnlyList<AITool> CreateDeclarationTools()
    {
        return CreateDeclarationTools(agentRuntime.GetToolMap().Tools);
    }

    private IReadOnlyList<AITool> CreateDeclarationTools(IReadOnlyList<AgentToolDescriptor> toolMap)
    {
        var tools = new List<AITool>(CreateSafeReadOnlyTools());
        foreach (var descriptor in toolMap)
        {
            if (descriptor.Name == "chat.respond")
            {
                continue;
            }

            if (descriptor.Name is RuntimeDiagnoseFunction.ToolName or ToolMapFunction.ToolName or FileSearchFunction.ToolName)
            {
                continue;
            }

            if (!IsCallableInR9(descriptor))
            {
                tools.Add(new BlockedToolFunction(
                    descriptor,
                    "tool_not_callable",
                    $"Tool '{descriptor.Name}' is visible in the local tool map but is not ready or callable in the current binding.",
                    descriptor.Actions.Count == 0
                        ? ["Inspect /api/agents/tools for current readiness and use the dedicated HTTP endpoint when supported."]
                        : descriptor.Actions));
                continue;
            }

            tools.Add(new ControlledToolDeclarationFunction(descriptor));
        }

        return tools;
    }

    public AgentFrameworkToolBindingResponse GetBindingStatus()
    {
        var toolMap = agentRuntime.GetToolMap().Tools;
        var safeTools = CreateSafeReadOnlyTools()
            .Select(tool => ToToolBinding(
                tool,
                toolMap.FirstOrDefault(descriptor =>
                    string.Equals(descriptor.Name, tool.Name, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        var declarationTools = CreateDeclarationTools(toolMap)
            .Select(tool => ToToolBinding(
                tool,
                toolMap.FirstOrDefault(descriptor =>
                    string.Equals(descriptor.Name, tool.Name, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        return new AgentFrameworkToolBindingResponse(
            "partial",
            DateTimeOffset.UtcNow,
            "Microsoft.Extensions.AI.AITool",
            safeTools,
            declarationTools,
            [
                "Read-only runtime tools are invokable without user confirmation.",
                "Ready multimodal tools are exposed as declarations; model-selected side-effect execution requires model_auto_controlled, an explicit tools[] allowlist, and confirmation when required.",
                "POST /api/agents/tools/invoke can execute runtime.diagnose and tools.inspect with audit metadata.",
                "POST /api/agents/tools/invoke can execute files.search as a read-only SQLite/local files tool.",
                "POST /api/agents/tools/invoke can execute runtime.repair only when mode=controlled and confirm=true.",
                "POST /api/agents/tools/invoke can execute ready R8 tools when mode=controlled; image.generate and audio.speak require confirm=true.",
                "POST /api/agents/chat supports tool_mode auto_read_only for bounded Tomur-planned read-only runtime, tool-map and file-search context.",
                "POST /api/agents/chat supports model_auto_read_only for bounded model-selected read-only calls and model_auto_controlled with an explicit tools[] allowlist; side-effect calls still require confirm=true.",
                "POST /api/agents/workflows/read-only executes a bounded Tomur read-only tool plan and can ask an Agent Framework workflow-hosted ChatClientAgent to summarize the results.",
                "GET /api/agents/telemetry exposes the local ActivitySource span and exporter configuration boundary for OpenTelemetry wiring.",
                "Image generation and TTS can write local artifacts only after explicit confirmation.",
                "POST /api/agents/chat defaults to ChatToolMode.None and can run explicit read-only tool context when tool_mode is read_only."
            ]);
    }

    private static AgentFrameworkToolBinding ToToolBinding(
        AITool tool,
        AgentToolDescriptor? descriptor)
        => new(
            tool.Name,
            tool.Description ?? string.Empty,
            ResolveImplementation(tool),
            ResolveBindingStatus(tool),
            descriptor?.Route,
            descriptor?.InputSchema ?? ResolveToolSchema(tool),
            descriptor?.SideEffect ?? "read",
            ResolveBindingCallable(tool, descriptor),
            descriptor?.RequiresConfirmation ?? false,
            descriptor?.InvocationModes ?? (tool is BlockedToolFunction ? ["blocked-agent-tool"] : ["manual-read-only", "chat-context"]));

    private static bool IsCallableInR9(AgentToolDescriptor descriptor)
        => descriptor.Callable &&
            descriptor.Name is
                RuntimeDiagnoseFunction.ToolName or
                ToolMapFunction.ToolName or
                FileSearchFunction.ToolName or
                "runtime.repair" or
                "image.generate" or
                "vision.analyze" or
                "ocr.recognize" or
                "audio.transcribe" or
                "audio.speak";

    private static string ResolveToolSchema(AITool tool)
        => tool is AIFunctionDeclaration declaration
            ? declaration.JsonSchema.GetRawText()
            : """{"type":"object","properties":{}}""";

    private static string ResolveImplementation(AITool tool)
        => tool is ControlledToolDeclarationFunction
            ? "Tomur.Agents.ControlledToolDeclarationFunction"
            : tool is AIFunction
                ? "Microsoft.Extensions.AI.AIFunction"
                : tool.GetType().FullName ?? "AITool";

    private static string ResolveBindingStatus(AITool tool)
        => tool switch
        {
            BlockedToolFunction => "blocked",
            ControlledToolDeclarationFunction => "controlled",
            _ => "ready"
        };

    private static bool ResolveBindingCallable(AITool tool, AgentToolDescriptor? descriptor)
        => tool is ControlledToolDeclarationFunction
            ? true
            : descriptor?.Callable ?? tool is not BlockedToolFunction;
}
