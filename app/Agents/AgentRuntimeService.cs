using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class AgentRuntimeService
{
    private const string AgentName = "tomur-local-agent";
    private const string AgentRuntime = "Microsoft.Agents.AI.ChatClientAgent";
    private const string WorkflowRuntime = "Tomur read-only tool plan with optional Microsoft.Agents.AI.Workflows summary";
    private const string ChatRespondInputSchema = """{"type":"object","properties":{"message":{"type":"string"},"messages":{"type":"array","items":{"type":"object","properties":{"role":{"type":"string"},"content":{"type":"string"}}}},"tool_results":{"type":"array","items":{"type":"object","properties":{"tool":{"type":"string"},"content":{"type":"string"},"result":{"type":"object"}}}},"tool_mode":{"type":"string","enum":["none","read_only","auto_read_only"]},"tools":{"type":"array","items":{"type":"object","required":["tool"],"properties":{"tool":{"type":"string","enum":["runtime.diagnose","tools.inspect"]},"arguments":{"type":"object"}}}},"max_tool_rounds":{"type":"integer"},"model":{"type":"string"},"instructions":{"type":"string"},"max_tokens":{"type":"integer"}}}""";

    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalRuntimeService multimodalRuntime;
    private readonly LocalChatClient chatClient;
    private readonly IServiceProvider services;

    public AgentRuntimeService(
        LocalModelCatalog modelCatalog,
        MultimodalRuntimeService multimodalRuntime,
        LocalChatClient chatClient,
        IServiceProvider services)
    {
        this.modelCatalog = modelCatalog;
        this.multimodalRuntime = multimodalRuntime;
        this.chatClient = chatClient;
        this.services = services;
    }

    public AgentRuntimeStatus GetStatus()
    {
        var models = modelCatalog.ListModels();
        var defaultChatModel = models.FirstOrDefault(IsChatModel);
        var tools = BuildToolStatuses(models).ToArray();
        var chatStatus = defaultChatModel is null ? "not_found" : "ready";
        var overallStatus = ResolveRuntimeStatus(chatStatus, tools);

        return new AgentRuntimeStatus(
            overallStatus,
            DateTimeOffset.UtcNow,
            new AgentChatClientStatus(
                chatStatus,
                "Microsoft.Extensions.AI.IChatClient",
                defaultChatModel?.Id,
                defaultChatModel is null
                    ? "No local chat model is visible yet."
                    : "Tomur local chat runtime is available through Microsoft.Extensions.AI.IChatClient."),
            new AgentFrameworkStatus(
                "wired",
                "Microsoft.Agents.AI.ChatClientAgent / Microsoft.Agents.AI.Workflows",
                "Agent Framework packages and Tomur-local AI boundaries are present. Plain local ChatClientAgent execution is wired, and read-only Tomur diagnostics are exposed as Microsoft.Extensions.AI.AITool objects with controlled manual, chat-context and bounded read-only workflow paths. Automatic multimodal tool-calling remains an R9 follow-up; image generation and TTS stay available through their dedicated OpenAI-compatible endpoints when backend readiness allows.",
                [
                    "POST /api/agents/chat runs the local ChatClientAgent text path.",
                    "GET /api/agents/tools exposes the Tomur tool map.",
                    "GET /api/agents/tool-bindings exposes the current AITool binding set.",
                    "POST /api/agents/tools/invoke can invoke runtime.diagnose and tools.inspect as read-only tools.",
                    "POST /api/agents/workflows/read-only runs a bounded Tomur read-only tool plan and can ask an Agent Framework workflow-hosted ChatClientAgent to summarize the results.",
                    "Use /api/agents/runtime to inspect the local tool map.",
                    "Use /api/runtime/multimodal to inspect backend readiness.",
                    "Plain OpenAI/Ollama-compatible text APIs continue to work without Agent Framework."
                ]),
            new AgentOrchestrationStatus(
                defaultChatModel is null ? "not_ready" : "wired",
                AgentRuntime,
                "POST /api/agents/chat",
                defaultChatModel is null
                    ? "A ChatClientAgent can be constructed only after a local chat model is visible."
                    : "A ChatClientAgent can run plain local text conversations and summarize bounded read-only tool results through Microsoft.Extensions.AI.IChatClient."),
            tools,
            [
                "Community keeps Agent Framework optional and local-first.",
                "Tool schemas are represented by source-generated JSON contracts before full workflow persistence is added.",
                "R9 does not make side-effect multimodal tools callable automatically by default.",
                "Repair or download actions still require explicit user confirmation in later R10/R11 flows."
            ]);
    }

    public AgentToolMapResponse GetToolMap()
    {
        var models = modelCatalog.ListModels();
        var tools = BuildToolDescriptors(models).ToArray();
        var status = ResolveToolMapStatus(tools);

        return new AgentToolMapResponse(status, DateTimeOffset.UtcNow, tools);
    }

    public async Task<AgentChatResponse> RunChatAsync(
        AgentChatRequest request,
        ToolInvoker toolInvoker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolInvoker);

        var messages = BuildMessages(request);
        var requestedToolMode = ResolveToolMode(request);
        var maxToolRounds = NormalizeMaxToolRounds(request.MaxToolRounds);
        var started = DateTimeOffset.UtcNow;
        var toolCalls = new List<AgentChatToolCall>();

        if (requestedToolMode is "read_only" or "auto_read_only")
        {
            var requestedTools = requestedToolMode == "auto_read_only"
                ? ResolveAutoReadOnlyTools(request.Tools, messages, maxToolRounds)
                : NormalizeRequestedTools(request.Tools, maxToolRounds);
            foreach (var toolRequest in requestedTools)
            {
                var invokeResponse = await toolInvoker.InvokeAsync(
                        new AgentToolInvokeRequest(toolRequest.Tool, toolRequest.Arguments),
                        requestedToolMode == "auto_read_only" ? "auto-read-only" : "chat-context-read-only",
                        requestedToolMode == "auto_read_only" ? "auto" : "chat-context",
                        cancellationToken)
                    .ConfigureAwait(false);
                toolCalls.Add(ToChatToolCall(invokeResponse));
                AddToolInvokeMessage(messages, invokeResponse);
            }
        }

        var options = CreateChatOptions(
            request.Model,
            request.Instructions,
            request.MaxTokens,
            request.Temperature,
            request.TopP);
        var modelId = ResolveChatModelId(options.ModelId);
        options.ModelId = modelId;
        var agent = CreateAgent(modelId);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await agent.RunAsync(
                messages,
                session,
                new ChatClientAgentRunOptions(options),
                cancellationToken)
            .ConfigureAwait(false);

        return new AgentChatResponse(
            toolCalls.Any(static tool => tool.Status == "blocked") ? "partial" : "ok",
            AgentName,
            AgentRuntime,
            modelId,
            requestedToolMode,
            toolCalls.Count,
            toolCalls,
            response.Text ?? string.Empty,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            BuildChatDiagnostics(requestedToolMode, toolCalls));
    }

    public async Task<AgentReadOnlyWorkflowResponse> RunReadOnlyWorkflowAsync(
        AgentReadOnlyWorkflowRequest request,
        ToolInvoker toolInvoker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolInvoker);

        var started = DateTimeOffset.UtcNow;
        var messages = BuildMessagesForWorkflow(request);
        var maxToolRounds = NormalizeMaxToolRounds(request.MaxToolRounds);
        var toolRequests = request.Tools is { Count: > 0 }
            ? NormalizeRequestedTools(request.Tools, maxToolRounds)
            : ResolveAutoReadOnlyTools(null, messages, maxToolRounds);

        if (toolRequests.Count == 0)
        {
            throw new InferenceException(
                "invalid_request",
                "The read-only workflow requires tools[] or a message that asks for runtime or tool-map context.",
                [
                    "Set tools to runtime.diagnose or tools.inspect.",
                    "Ask for runtime diagnostics or tool availability when using automatic read-only planning."
                ]);
        }

        var steps = new List<AgentChatToolCall>();
        foreach (var toolRequest in toolRequests)
        {
            var invokeResponse = await toolInvoker.InvokeAsync(
                    new AgentToolInvokeRequest(toolRequest.Tool, toolRequest.Arguments),
                    "workflow-read-only",
                    "workflow",
                    cancellationToken)
                .ConfigureAwait(false);
            steps.Add(ToChatToolCall(invokeResponse));
        }

        string text = string.Empty;
        string? model = null;
        var shouldRespond = request.Respond ??
            modelCatalog.ListModels().Any(IsChatModel);
        if (shouldRespond)
        {
            var summary = await RunWorkflowSummaryAsync(
                    request,
                    steps,
                    cancellationToken)
                .ConfigureAwait(false);
            text = summary.Text;
            model = summary.Model;
        }

        return new AgentReadOnlyWorkflowResponse(
            steps.Any(static step => step.Status == "blocked") ? "partial" : "ok",
            "read-only-tools",
            WorkflowRuntime,
            model,
            steps.Count,
            steps,
            text,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            BuildWorkflowDiagnostics(steps, shouldRespond));
    }

    private async Task<WorkflowSummary> RunWorkflowSummaryAsync(
        AgentReadOnlyWorkflowRequest request,
        IReadOnlyList<AgentChatToolCall> steps,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessages(
            new AgentChatRequest(
                request.Model,
                request.Message,
                request.Messages,
                steps.Select(ToToolResult).ToArray(),
                "none",
                null,
                null,
                null,
                request.MaxTokens,
                request.Temperature,
                request.TopP));
        var options = CreateChatOptions(
            request.Model,
            request.Instructions ?? "Summarize the local read-only tool results clearly. Do not claim that side-effect tools were executed.",
            request.MaxTokens,
            request.Temperature,
            request.TopP);
        var modelId = ResolveChatModelId(options.ModelId);
        options.ModelId = modelId;

        var summaryAgent = CreateAgent(modelId);
        var workflow = AgentWorkflowBuilder.BuildSequential(
            "tomur-read-only-summary",
            [summaryAgent]);
        var workflowAgent = workflow.AsAIAgent(
            "tomur.readonly.workflow",
            "tomur-read-only-workflow",
            "Sequential Agent Framework workflow used to summarize Tomur read-only tool results.",
            InProcessExecution.Default,
            includeExceptionDetails: false,
            includeWorkflowOutputsInResponse: true);
        var response = await workflowAgent.RunAsync(
                messages,
                null,
                new ChatClientAgentRunOptions(options),
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowSummary(modelId, response.Text ?? string.Empty);
    }

    private static IReadOnlyList<string> BuildChatDiagnostics(
        string toolMode,
        IReadOnlyList<AgentChatToolCall> toolCalls)
    {
        var diagnostics = new List<string>
        {
                "source: Microsoft.Agents.AI.ChatClientAgent",
                "chat-client: Microsoft.Extensions.AI.IChatClient",
                $"tool-mode: {toolMode}",
                "scope: local text orchestration"
        };

        if (toolMode is "read_only" or "auto_read_only")
        {
            diagnostics.Add(toolMode == "auto_read_only"
                ? "tool-scope: automatic bounded read-only Tomur AITool context"
                : "tool-scope: controlled read-only Tomur AITool context");
            diagnostics.Add($"tool-rounds: {toolCalls.Count}");
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> BuildWorkflowDiagnostics(
        IReadOnlyList<AgentChatToolCall> steps,
        bool responseRequested)
    {
        var diagnostics = new List<string>
        {
            "workflow: read-only-tools",
            responseRequested
                ? "source: Tomur bounded tool planner + Microsoft.Agents.AI.Workflows"
                : "source: Tomur bounded tool planner",
            "tool-boundary: Microsoft.Extensions.AI.AITool",
            "scope: bounded local read-only workflow",
            $"step-count: {steps.Count}",
            responseRequested
                ? "response: workflow-hosted ChatClientAgent summary requested"
                : "response: tool results only"
        };

        if (steps.Any(static step => step.Status == "blocked"))
        {
            diagnostics.Add("status: one or more requested tools were blocked by the R9 safety boundary");
        }

        return diagnostics;
    }

    private ChatClientAgent CreateAgent(string modelId)
    {
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "tomur.local",
                Name = AgentName,
                Description = "Local-first Tomur Community agent backed by the monolithic Tomur runtime.",
                ChatOptions = new ChatOptions
                {
                    ModelId = modelId,
                    ToolMode = ChatToolMode.None
                },
                UseProvidedChatClientAsIs = true
            },
            NullLoggerFactory.Instance,
            services);
    }

    private ChatOptions CreateChatOptions(
        string? model,
        string? instructions,
        int? maxTokens,
        double? temperature,
        double? topP)
        => new()
        {
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim(),
            Temperature = NormalizeFloat(temperature),
            TopP = NormalizeFloat(topP),
            MaxOutputTokens = maxTokens is > 0 ? Math.Clamp(maxTokens.Value, 1, 4096) : null,
            ToolMode = ChatToolMode.None
        };

    private string ResolveChatModelId(string? requestedModel)
    {
        var modelId = requestedModel ?? modelCatalog.ListModels().FirstOrDefault(IsChatModel)?.Id;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        throw new InferenceException(
            "model_not_downloaded",
            "No local chat model is available for the Agent Framework chat endpoint.",
            [
                "Run tomur pull recommended to install the default local assistant model.",
                "Use /v1/models or /api/tags to inspect models currently visible to Tomur."
            ]);
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(AgentChatRequest request)
    {
        var messages = new List<ChatMessage>();
        if (request.Messages is { Count: > 0 })
        {
            messages.AddRange(request.Messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => new ChatMessage(NormalizeChatRole(message.Role), message.Content!.Trim())));
        }

        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Message))
        {
            messages.Add(new ChatMessage(ChatRole.User, request.Message.Trim()));
        }

        AddToolResultMessages(messages, request.ToolResults);

        if (messages.Count > 0)
        {
            return messages;
        }

        throw new InferenceException(
            "invalid_request",
            "The agent chat request requires message, messages[].content or tool_results[].content.",
            ["Provide a user message or a prior read-only tool result before invoking /api/agents/chat."]);
    }

    private static IReadOnlyList<ChatMessage> BuildMessagesForWorkflow(AgentReadOnlyWorkflowRequest request)
    {
        var chatRequest = new AgentChatRequest(
            request.Model,
            request.Message,
            request.Messages,
            null,
            null,
            null,
            null,
            request.Instructions,
            request.MaxTokens,
            request.Temperature,
            request.TopP);

        if (request.Tools is { Count: > 0 } &&
            string.IsNullOrWhiteSpace(request.Message) &&
            (request.Messages is null || request.Messages.Count == 0))
        {
            return [new ChatMessage(ChatRole.User, "Run the requested read-only Tomur workflow tools.")];
        }

        return BuildMessages(chatRequest);
    }

    private static void AddToolResultMessages(
        List<ChatMessage> messages,
        IReadOnlyList<AgentChatToolResult>? toolResults)
    {
        if (toolResults is null || toolResults.Count == 0)
        {
            return;
        }

        foreach (var toolResult in toolResults)
        {
            var content = NormalizeToolResultContent(toolResult);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            messages.Add(new ChatMessage(ChatRole.Tool, content));
        }
    }

    private static void AddToolInvokeMessage(
        List<ChatMessage> messages,
        AgentToolInvokeResponse invokeResponse)
    {
        var result = invokeResponse.Result?.GetRawText() ?? "null";
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            $"tool:{invokeResponse.Tool}\nstatus:{invokeResponse.Status}\nelapsed_ms:{invokeResponse.ElapsedMs}\nresult:{result}"));
    }

    private static AgentChatToolCall ToChatToolCall(AgentToolInvokeResponse response)
        => new(
            response.Tool,
            response.Status,
            response.ElapsedMs,
            response.Result,
            response.Diagnostics,
            response.Audit);

    private static string ResolveToolMode(AgentChatRequest request)
    {
        var hasToolRequests = request.Tools is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(request.ToolMode))
        {
            return hasToolRequests ? "read_only" : "none";
        }

        var normalized = request.ToolMode.Trim().ToLowerInvariant().Replace('-', '_');
        if (normalized is "none" or "off")
        {
            if (hasToolRequests)
            {
                throw new InferenceException(
                    "invalid_request",
                    "The tools field cannot be used when tool_mode is none.",
                    ["Set tool_mode to read_only, or remove tools from the request."]);
            }

            return "none";
        }

        if (normalized is "read" or "read_only" or "manual_read_only" or "controlled" or "context")
        {
            return "read_only";
        }

        if (normalized is "auto" or "auto_read_only" or "automatic_read_only" or "read_only_auto")
        {
            return "auto_read_only";
        }

        throw new InferenceException(
            "invalid_request",
            $"Unsupported tool_mode '{request.ToolMode}'.",
            [
                "Use tool_mode none for plain text.",
                "Use tool_mode read_only with tools[] to invoke runtime.diagnose or tools.inspect before the agent answers.",
                "Use tool_mode auto_read_only to let Tomur select bounded read-only tools from the current request.",
                "Model-selected multimodal tool-calling is not enabled in this R9 boundary."
            ]);
    }

    private static int NormalizeMaxToolRounds(int? value)
        => value is > 0 ? Math.Clamp(value.Value, 1, 4) : 2;

    private static IReadOnlyList<AgentChatToolRequest> NormalizeRequestedTools(
        IReadOnlyList<AgentChatToolRequest>? tools,
        int maxToolRounds)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        var normalized = tools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool.Tool))
            .Select(static tool => tool with { Tool = tool.Tool!.Trim() })
            .ToArray();

        if (normalized.Length == 0)
        {
            return [];
        }

        if (normalized.Length > maxToolRounds)
        {
            throw new InferenceException(
                "tool_round_limit_exceeded",
                $"The request asked for {normalized.Length} tool calls, but max_tool_rounds is {maxToolRounds}.",
                ["Reduce tools[] or increase max_tool_rounds up to 4."]);
        }

        return normalized;
    }

    private static IReadOnlyList<AgentChatToolRequest> ResolveAutoReadOnlyTools(
        IReadOnlyList<AgentChatToolRequest>? requestedTools,
        IReadOnlyList<ChatMessage> messages,
        int maxToolRounds)
    {
        var normalized = NormalizeRequestedTools(requestedTools, maxToolRounds);
        if (normalized.Count > 0)
        {
            return normalized;
        }

        var text = string.Join("\n", messages.Select(static message => message.Text));
        var planned = new List<AgentChatToolRequest>();
        if (MentionsRuntimeDiagnostics(text))
        {
            planned.Add(new AgentChatToolRequest(RuntimeDiagnoseFunction.ToolName, null));
        }

        if (MentionsToolMap(text))
        {
            planned.Add(new AgentChatToolRequest(ToolMapFunction.ToolName, null));
        }

        if (planned.Count == 0 && MentionsLocalStatus(text))
        {
            planned.Add(new AgentChatToolRequest(RuntimeDiagnoseFunction.ToolName, null));
            planned.Add(new AgentChatToolRequest(ToolMapFunction.ToolName, null));
        }

        return planned.Count > maxToolRounds
            ? planned.Take(maxToolRounds).ToArray()
            : planned;
    }

    private static bool MentionsRuntimeDiagnostics(string value)
        => ContainsAny(
            value,
            "runtime",
            "diagnostic",
            "diagnostics",
            "doctor",
            "health",
            "status",
            "native",
            "运行时",
            "诊断",
            "健康",
            "状态");

    private static bool MentionsToolMap(string value)
        => ContainsAny(
            value,
            "tool",
            "tools",
            "capability",
            "capabilities",
            "agent",
            "workflow",
            "工具",
            "能力",
            "编排");

    private static bool MentionsLocalStatus(string value)
        => ContainsAny(
            value,
            "tomur",
            "local",
            "ready",
            "available",
            "本地",
            "可用");

    private static bool ContainsAny(string value, params string[] terms)
        => !string.IsNullOrWhiteSpace(value) &&
            terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeToolResultContent(AgentChatToolResult toolResult)
    {
        var toolName = string.IsNullOrWhiteSpace(toolResult.Tool)
            ? "unknown"
            : toolResult.Tool.Trim();
        var content = !string.IsNullOrWhiteSpace(toolResult.Content)
            ? toolResult.Content.Trim()
            : toolResult.Result?.GetRawText();

        return string.IsNullOrWhiteSpace(content)
            ? string.Empty
            : $"tool:{toolName}\n{content}";
    }

    private static AgentChatToolResult ToToolResult(AgentChatToolCall toolCall)
        => new(
            toolCall.Tool,
            $"status:{toolCall.Status}\nelapsed_ms:{toolCall.ElapsedMs}\nresult:{toolCall.Result?.GetRawText() ?? "null"}",
            toolCall.Result);

    private IEnumerable<AgentToolStatus> BuildToolStatuses(IReadOnlyList<LocalModelDescriptor> models)
        => BuildToolDescriptors(models)
            .Select(static tool => new AgentToolStatus(
                tool.Name,
                tool.DisplayName,
                tool.Status,
                tool.Backend,
                tool.Model,
                tool.Route,
                tool.InputSchema,
                tool.SideEffect,
                tool.Callable,
                tool.RequiresConfirmation,
                tool.InvocationModes,
                tool.Message,
                tool.Actions));

    private static string ResolveRuntimeStatus(string chatStatus, IReadOnlyList<AgentToolStatus> tools)
    {
        if (!string.Equals(chatStatus, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return tools.Any(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
                ? "partial"
                : "not_ready";
        }

        return tools.All(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
            ? "ready"
            : "partial";
    }

    private static string ResolveToolMapStatus(IReadOnlyList<AgentToolDescriptor> tools)
    {
        if (tools.All(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase)))
        {
            return "ready";
        }

        return tools.Any(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
            ? "partial"
            : "not_ready";
    }

    private IEnumerable<AgentToolDescriptor> BuildToolDescriptors(IReadOnlyList<LocalModelDescriptor> models)
    {
        var defaultChatModel = models.FirstOrDefault(IsChatModel);
        yield return new AgentToolDescriptor(
            "chat.respond",
            "Local Chat",
            defaultChatModel is null ? "not_found" : "ready",
            "llama.cpp via IChatClient",
            defaultChatModel?.Id,
            "/api/agents/chat",
            ChatRespondInputSchema,
            "none",
            true,
            false,
            ["endpoint"],
            defaultChatModel is null
                ? "No local chat model is visible."
                : "Local chat model can answer through Microsoft.Extensions.AI.IChatClient.",
            defaultChatModel is null ? ["Run tomur pull recommended to install the default assistant model."] : []);

        yield return CreateMultimodalTool("image.generate", "Image Generation", "image-generation", "stable-diffusion.cpp");
        yield return CreateMultimodalTool("vision.analyze", "Vision Analysis", "vlm", "llama.cpp mtmd VLM");
        yield return CreateMultimodalTool("ocr.recognize", "OCR", "ocr", "Tomur OCR native bridge");
        yield return CreateMultimodalTool("audio.transcribe", "Speech To Text", "asr", "whisper.cpp");
        yield return CreateMultimodalTool("audio.speak", "Text To Speech", "tts", "llama.cpp GGUF TTS");

        yield return new AgentToolDescriptor(
            "files.search",
            "Local Files",
            "planned",
            "SQLite/local files",
            null,
            null,
            """{"type":"object","required":["query"],"properties":{"query":{"type":"string"},"top_k":{"type":"integer"}}}""",
            "read",
            false,
            false,
            ["planned-agent-tool"],
            "Local file Q&A tool is reserved for R9/R10 and will not require PostgreSQL in Community.",
            ["Index local files in a later R10/R11 flow before enabling this tool."]);

        yield return new AgentToolDescriptor(
            "runtime.diagnose",
            "Runtime Diagnostics",
            "ready",
            "tomur doctor/runtime APIs",
            null,
            "/api/runtime/status",
            """{"type":"object","properties":{}}""",
            "read",
            true,
            false,
            ["manual-read-only", "chat-context", "auto-read-only", "read-only-workflow"],
            "Runtime status can be exposed as a read-only local tool; repair actions remain user-confirmed.",
            []);

        yield return new AgentToolDescriptor(
            "tools.inspect",
            "Tool Map",
            "ready",
            "Microsoft.Extensions.AI.AITool metadata",
            null,
            "/api/agents/tool-bindings",
            """{"type":"object","properties":{}}""",
            "read",
            true,
            false,
            ["manual-read-only", "chat-context", "auto-read-only", "read-only-workflow"],
            "Agent Framework tool declarations can be inspected without invoking side-effect tools.",
            []);
    }

    private AgentToolDescriptor CreateMultimodalTool(
        string name,
        string displayName,
        string backendId,
        string backendName)
    {
        var backend = multimodalRuntime.GetBackendStatus(backendId);
        var model = backend.VisibleModelIds.FirstOrDefault();
        var status = string.Equals(backend.Status, "ready", StringComparison.OrdinalIgnoreCase)
            ? ResolveExecutableToolStatus(backendId)
            : backend.Status;

        return new AgentToolDescriptor(
            name,
            displayName,
            status,
            backendName,
            model,
            ResolveToolRoute(backendId),
            ResolveToolInputSchema(backendId),
            ResolveToolSideEffect(backendId),
            false,
            RequiresConfirmation(backendId),
            ResolveInvocationModes(backendId),
            backend.Message,
            backend.Actions);
    }

    private static bool RequiresConfirmation(string backendId)
    {
        var sideEffect = ResolveToolSideEffect(backendId);
        return sideEffect is not "none" and not "read";
    }

    private static IReadOnlyList<string> ResolveInvocationModes(string backendId)
        => RequiresConfirmation(backendId)
            ? ["dedicated-endpoint", "blocked-agent-tool"]
            : ["dedicated-endpoint", "planned-agent-tool"];

    private static string ResolveExecutableToolStatus(string backendId)
        => "ready";

    private static bool IsChatModel(LocalModelDescriptor model)
        => model.Capabilities.Any(static capability =>
            string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));

    private static ChatRole NormalizeChatRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };
    }

    private static float? NormalizeFloat(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return (float)value.Value;
    }

    private static string? ResolveToolRoute(string backendId)
        => backendId switch
        {
            "image-generation" => "/v1/images/generations",
            "vlm" => "/api/vision/analyze",
            "ocr" => "/api/ocr/analyze",
            "asr" => "/v1/audio/transcriptions",
            "tts" => "/v1/audio/speech",
            _ => null
        };

    private static string ResolveToolInputSchema(string backendId)
        => backendId switch
        {
            "image-generation" => """{"type":"object","required":["prompt"],"properties":{"prompt":{"type":"string"},"size":{"type":"string"},"steps":{"type":"integer"}}}""",
            "vlm" => """{"type":"object","required":["prompt","images"],"properties":{"prompt":{"type":"string"},"images":{"type":"array"}}}""",
            "ocr" => """{"type":"object","required":["image"],"properties":{"image":{"type":"object"},"language":{"type":"string"},"prompt":{"type":"string"}}}""",
            "asr" => """{"type":"object","required":["file"],"properties":{"file":{"type":"string","description":"multipart/form-data file field"},"language":{"type":"string"}}}""",
            "tts" => """{"type":"object","required":["input"],"properties":{"input":{"type":"string"},"voice":{"type":"string"},"response_format":{"type":"string"}}}""",
            _ => """{"type":"object","properties":{}}"""
        };

    private static string ResolveToolSideEffect(string backendId)
        => backendId switch
        {
            "image-generation" or "tts" => "generates-local-artifact",
            _ => "read"
        };

    private sealed record WorkflowSummary(string Model, string Text);
}
