using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Agents;

public sealed class AgentRuntimeService
{
    private const string AgentName = "tomur-local-agent";
    private const string AgentRuntime = "Microsoft.Agents.AI.ChatClientAgent";
    private const string WorkflowRuntime = "Tomur read-only tool plan with optional Microsoft.Agents.AI.Workflows summary";
    private const string ChatRespondInputSchema = """{"type":"object","properties":{"message":{"type":"string"},"messages":{"type":"array","items":{"type":"object","properties":{"role":{"type":"string"},"content":{"type":"string"}}}},"tool_results":{"type":"array","items":{"type":"object","properties":{"tool":{"type":"string"},"content":{"type":"string"},"result":{"type":"object"}}}},"tool_mode":{"type":"string","enum":["none","read_only","auto_read_only","controlled","auto_controlled","model_auto_read_only","model_auto_controlled"]},"tools":{"type":"array","items":{"type":"object","required":["tool"],"properties":{"tool":{"type":"string","enum":["runtime.diagnose","tools.inspect","files.search","runtime.repair","vision.analyze","ocr.recognize","audio.transcribe","image.generate","audio.speak"]},"arguments":{"type":"object"},"confirm":{"type":"boolean"}}}},"max_tool_rounds":{"type":"integer"},"model":{"type":"string"},"instructions":{"type":"string"},"max_tokens":{"type":"integer"}}}""";

    private sealed record ModelToolSelection(
        AgentToolDescriptor Descriptor,
        AgentChatToolRequest? Request);

    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalRuntimeService multimodalRuntime;
    private readonly LocalChatClient chatClient;
    private readonly AgentEventLog eventLog;
    private readonly AgentTelemetry telemetry;
    private readonly IServiceProvider services;
    private readonly ILoggerFactory loggerFactory;

    public AgentRuntimeService(
        LocalModelCatalog modelCatalog,
        MultimodalRuntimeService multimodalRuntime,
        LocalChatClient chatClient,
        AgentEventLog eventLog,
        AgentTelemetry telemetry,
        IServiceProvider services,
        ILoggerFactory loggerFactory)
    {
        this.modelCatalog = modelCatalog;
        this.multimodalRuntime = multimodalRuntime;
        this.chatClient = chatClient;
        this.eventLog = eventLog;
        this.telemetry = telemetry;
        this.services = services;
        this.loggerFactory = loggerFactory;
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
                "Agent Framework packages and Tomur-local AI boundaries are present. Local models can select declared Tomur tools through bounded model_auto_* modes; read-only calls run automatically, while controlled side effects keep explicit allowlist and confirmation requirements.",
                [
                    "POST /api/agents/chat runs plain text, planned context, and bounded model-selected tool loops.",
                    "GET /api/agents/tools exposes the Tomur tool map.",
                    "GET /api/agents/tool-bindings exposes the current AITool binding set.",
                    "GET /api/agents/events exposes recent local Agent Framework event summaries.",
                    "GET /api/agents/telemetry exposes the local ActivitySource span and attribute draft for future OpenTelemetry wiring.",
                    "POST /api/agents/tools/invoke can invoke runtime.diagnose and tools.inspect as read-only tools, or explicit controlled R8 tools when mode=controlled.",
                    "POST /api/agents/tools/invoke can invoke files.search as a read-only SQLite/local files tool.",
                    "POST /api/agents/tools/invoke can invoke runtime.repair only with mode=controlled and confirm=true.",
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
                    : "A ChatClientAgent can run plain local text conversations, bounded model-selected tool loops, and read-only workflow summaries through Microsoft.Extensions.AI.IChatClient."),
            tools,
            [
                "Community keeps Agent Framework optional and local-first.",
                "Tool schemas are represented by source-generated JSON contracts before full workflow persistence is added.",
                "Model-selected side-effect tools require an explicit controlled allowlist and confirmation.",
                "Runtime repair actions require explicit user confirmation and stay outside automatic read-only planning."
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
        ValidateChatRequestCollections(request);

        var messages = BuildMessages(request);
        var requestedToolMode = ResolveToolMode(request);
        var modelSelectedMode = IsModelSelectedMode(requestedToolMode);
        var maxToolRounds = NormalizeMaxToolRounds(request.MaxToolRounds);
        var started = DateTimeOffset.UtcNow;
        var toolCalls = new List<AgentChatToolCall>();
        using var activity = telemetry.StartChat(request, AgentRuntime);

        if (!modelSelectedMode &&
            requestedToolMode is "read_only" or "auto_read_only" or "controlled" or "auto_controlled")
        {
            var requestedTools = requestedToolMode is "auto_read_only" or "auto_controlled"
                ? ResolveAutoTools(request.Tools, messages, maxToolRounds)
                : NormalizeRequestedTools(request.Tools, maxToolRounds);
            foreach (var toolRequest in requestedTools)
            {
                var invokeResponse = await toolInvoker.InvokeAsync(
                        CreateToolInvokeRequest(toolRequest, requestedToolMode),
                        ResolveAuditMode(requestedToolMode),
                        ResolveInvocationKind(requestedToolMode),
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
        string responseText;
        if (modelSelectedMode)
        {
            responseText = await RunModelSelectedChatAsync(
                    request,
                    messages,
                    options,
                    modelId,
                    requestedToolMode,
                    maxToolRounds,
                    toolInvoker,
                    toolCalls,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var agent = CreateAgent(modelId);
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var response = await agent.RunAsync(
                    messages,
                    session,
                    new ChatClientAgentRunOptions(options),
                    cancellationToken)
                .ConfigureAwait(false);
            responseText = response.Text ?? string.Empty;
        }

        var agentResponse = new AgentChatResponse(
            toolCalls.Any(static tool => IsNonOkToolStatus(tool.Status)) ? "partial" : "ok",
            AgentName,
            AgentRuntime,
            modelId,
            requestedToolMode,
            toolCalls.Count,
            toolCalls,
            responseText,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            BuildChatDiagnostics(requestedToolMode, toolCalls));
        telemetry.CompleteChat(activity, agentResponse);
        await eventLog.WriteChatAsync(agentResponse, cancellationToken).ConfigureAwait(false);
        return agentResponse;
    }

    public async Task<AgentReadOnlyWorkflowResponse> RunReadOnlyWorkflowAsync(
        AgentReadOnlyWorkflowRequest request,
        ToolInvoker toolInvoker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolInvoker);
        ValidateWorkflowRequestCollections(request);

        var started = DateTimeOffset.UtcNow;
        var messages = BuildMessagesForWorkflow(request);
        var maxToolRounds = NormalizeMaxToolRounds(request.MaxToolRounds);
        using var activity = telemetry.StartWorkflow(request, WorkflowRuntime);
        var toolRequests = request.Tools is { Count: > 0 }
            ? NormalizeRequestedTools(request.Tools, maxToolRounds)
            : ResolveAutoTools(null, messages, maxToolRounds);

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

        var workflowResponse = new AgentReadOnlyWorkflowResponse(
            steps.Any(static step => IsNonOkToolStatus(step.Status)) ? "partial" : "ok",
            "read-only-tools",
            WorkflowRuntime,
            model,
            steps.Count,
            steps,
            text,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            BuildWorkflowDiagnostics(steps, shouldRespond));
        telemetry.CompleteWorkflow(activity, workflowResponse);
        await eventLog.WriteWorkflowAsync(workflowResponse, cancellationToken).ConfigureAwait(false);
        return workflowResponse;
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
        else if (toolMode is "controlled" or "auto_controlled")
        {
            diagnostics.Add(toolMode == "auto_controlled"
                ? "tool-scope: automatic bounded Tomur context plus explicit controlled R8 tool requests"
                : "tool-scope: explicit controlled R8 tool context");
            diagnostics.Add("tool-boundary: artifact-generating tools require confirm=true");
            diagnostics.Add($"tool-rounds: {toolCalls.Count}");
        }
        else if (toolMode is "model_auto_read_only" or "model_auto_controlled")
        {
            diagnostics.Add("tool-planner: local model-selected function calls");
            diagnostics.Add("tool-loop: Microsoft.Extensions.AI.FunctionInvokingChatClient");
            diagnostics.Add(toolMode == "model_auto_controlled"
                ? "tool-boundary: explicit controlled allowlist; side effects require confirm=true"
                : "tool-boundary: callable read-only tools only");
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

        if (steps.Any(static step => IsNonOkToolStatus(step.Status)))
        {
            diagnostics.Add("status: one or more requested tools were blocked or returned a diagnostic error");
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
            loggerFactory,
            services);
    }

    /// <summary>
    /// 使用指定聊天管线创建 Agent，模型自主模式会传入函数调用装饰器。
    /// </summary>
    private ChatClientAgent CreateAgent(string modelId, IChatClient client, ChatOptions options)
    {
        options.ModelId = modelId;
        return new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Id = "tomur.local",
                Name = AgentName,
                Description = "Local-first Tomur Community agent backed by the monolithic Tomur runtime.",
                // 工具和 instructions 只通过 run options 传入，避免 Agent 默认选项再次合并造成重复。
                ChatOptions = new ChatOptions
                {
                    ModelId = modelId,
                    ToolMode = ChatToolMode.None
                },
                UseProvidedChatClientAsIs = true
            },
            loggerFactory,
            services);
    }

    /// <summary>
    /// 运行模型选择、工具执行、结果回灌和继续推理的有界服务端循环。
    /// </summary>
    private async Task<string> RunModelSelectedChatAsync(
        AgentChatRequest request,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        string modelId,
        string toolMode,
        int maxToolRounds,
        ToolInvoker toolInvoker,
        List<AgentChatToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var selections = ResolveModelSelectedTools(request.Tools, toolMode);
        var selectionByName = selections.ToDictionary(
            static selection => selection.Descriptor.Name,
            StringComparer.Ordinal);
        options.Tools = selections
            .Select(static selection => (AITool)new ModelSelectedToolFunction(
                selection.Descriptor,
                selection.Request?.Confirm == true ? selection.Request.Arguments : null))
            .ToArray();
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = false;

        var functionClient = new FunctionInvokingChatClient(chatClient, loggerFactory, services)
        {
            AllowConcurrentInvocation = false,
            // 多留一次迭代让自定义执行器把超限调用转换为明确诊断，同时仍只执行 maxToolRounds 次工具。
            MaximumIterationsPerRequest = maxToolRounds + 2,
            MaximumConsecutiveErrorsPerRequest = 0,
            TerminateOnUnknownCalls = true
        };
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var approvalTracker = new ModelToolApprovalTracker();
        functionClient.FunctionInvoker = async (context, invocationToken) =>
        {
            var call = context.CallContent;
            if (!selectionByName.TryGetValue(call.Name, out var selection))
            {
                context.Terminate = true;
                throw new InferenceException(
                    "tool_not_declared",
                    $"The model requested tool '{call.Name}' outside the current allowlist.",
                    ["Use only tools declared for the current Agent request."]);
            }

            if (toolCalls.Count >= maxToolRounds)
            {
                context.Terminate = true;
                var roundLimit = new InferenceException(
                    "tool_round_limit_exceeded",
                    $"The model exceeded max_tool_rounds={maxToolRounds}.",
                    ["Increase max_tool_rounds up to 4 or ask the model to complete with fewer tool calls."]);
                var roundLimitAudit = AgentToolInvocationAuditContext.CreateModelSelected(
                    call.CallId,
                    null,
                    selection.Request?.Confirm == true,
                    confirmationEffective: false,
                    confirmationConsumed: false,
                    confirmationReused: false);
                var roundLimitResponse = await toolInvoker.RecordModelSelectedFailureAsync(
                        selection.Descriptor,
                        toolMode == "model_auto_controlled" ? "model-auto-controlled" : "model-auto-read-only",
                        toolMode == "model_auto_controlled" ? "auto-controlled" : "model-auto",
                        roundLimitAudit,
                        roundLimit,
                        invocationToken)
                    .ConfigureAwait(false);
                toolCalls.Add(ToModelSelectedToolCall(roundLimitResponse, call.CallId, null));
                return roundLimitResponse.Result.HasValue ? roundLimitResponse.Result.Value : null;
            }

            var confirmationRequested = selection.Request?.Confirm == true;
            if (!seenCallIds.Add(call.CallId))
            {
                context.Terminate = true;
                var duplicate = new InferenceException(
                    "duplicate_tool_call_id",
                    $"The model reused tool call id '{call.CallId}' across Agent rounds.",
                    ["Return a unique call id for every tool invocation in the current request."]);
                var duplicateAudit = AgentToolInvocationAuditContext.CreateModelSelected(
                    call.CallId,
                    null,
                    confirmationRequested,
                    confirmationEffective: false,
                    confirmationConsumed: false,
                    confirmationReused: false);
                var duplicateResponse = await toolInvoker.RecordModelSelectedFailureAsync(
                        selection.Descriptor,
                        toolMode == "model_auto_controlled" ? "model-auto-controlled" : "model-auto-read-only",
                        toolMode == "model_auto_controlled" ? "auto-controlled" : "model-auto",
                        duplicateAudit,
                        duplicate,
                        invocationToken)
                    .ConfigureAwait(false);
                toolCalls.Add(ToModelSelectedToolCall(duplicateResponse, call.CallId, null));
                return duplicateResponse.Result.HasValue ? duplicateResponse.Result.Value : null;
            }

            JsonElement arguments;
            try
            {
                arguments = ModelToolProtocol.ToJsonElement(call.Arguments);
            }
            catch (Exception exception) when (IsRecoverableModelToolException(exception))
            {
                var failureAudit = AgentToolInvocationAuditContext.CreateModelSelected(
                    call.CallId,
                    null,
                    confirmationRequested,
                    confirmationEffective: false,
                    confirmationConsumed: false,
                    confirmationReused: false);
                var failureResponse = await toolInvoker.RecordModelSelectedFailureAsync(
                        selection.Descriptor,
                        toolMode == "model_auto_controlled" ? "model-auto-controlled" : "model-auto-read-only",
                        toolMode == "model_auto_controlled" ? "auto-controlled" : "model-auto",
                        failureAudit,
                        exception,
                        invocationToken)
                    .ConfigureAwait(false);
                toolCalls.Add(ToModelSelectedToolCall(failureResponse, call.CallId, null));
                return failureResponse.Result.HasValue ? failureResponse.Result.Value : null;
            }

            var controlled = toolMode == "model_auto_controlled";
            var hasSideEffect = HasModelToolSideEffect(selection.Descriptor);
            var approval = approvalTracker.Evaluate(
                selection.Descriptor.Name,
                hasSideEffect,
                selection.Request,
                arguments);
            var confirmed = !hasSideEffect || approval.Effective;
            var auditContext = AgentToolInvocationAuditContext.CreateModelSelected(
                call.CallId,
                arguments,
                approval.Requested,
                approval.Effective,
                approval.Consumed,
                approval.Reused);
            var invokeRequest = new AgentToolInvokeRequest(call.Name, arguments)
            {
                Mode = controlled ? "auto_controlled" : "read_only",
                Confirm = confirmed
            };
            var invokeResponse = await toolInvoker.InvokeAsync(
                    invokeRequest,
                    controlled ? "model-auto-controlled" : "model-auto-read-only",
                    controlled ? "auto-controlled" : "model-auto",
                    auditContext,
                    invocationToken)
                .ConfigureAwait(false);
            toolCalls.Add(ToModelSelectedToolCall(invokeResponse, call.CallId, arguments));

            if (string.Equals(invokeResponse.Status, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                context.Terminate = true;
            }

            return invokeResponse.Result.HasValue
                ? invokeResponse.Result.Value
                : null;
        };

        var agent = CreateAgent(modelId, functionClient, options);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await agent.RunAsync(
                messages,
                session,
                new ChatClientAgentRunOptions(options),
                cancellationToken)
            .ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// 将副作用工具的确认绑定到 allowlist 中预批准的完整参数，模型不得自行替换具体动作。
    /// </summary>
    internal static bool IsConfirmedModelToolArguments(
        AgentChatToolRequest? requested,
        JsonElement modelArguments)
        => requested?.Confirm == true &&
            requested.Arguments is { ValueKind: JsonValueKind.Object } approvedArguments &&
            JsonElement.DeepEquals(approvedArguments, modelArguments);

    /// <summary>
    /// 判断模型工具是否具有副作用，确认边界以 side_effect 为准而不是依赖可误配的提示标志。
    /// </summary>
    internal static bool HasModelToolSideEffect(AgentToolDescriptor descriptor)
        => !string.Equals(descriptor.SideEffect, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 仅把可转换成工具错误结果的异常留在模型循环内，致命进程异常继续向上传播。
    /// </summary>
    private static bool IsRecoverableModelToolException(Exception exception)
        => exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException;

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

    private static List<ChatMessage> BuildMessages(AgentChatRequest request)
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

    /// <summary>
    /// 在进入消息和工具规划前拒绝 JSON 数组中的空元素，避免无效请求退化为服务器空引用错误。
    /// </summary>
    internal static void ValidateChatRequestCollections(AgentChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages?.Any(static message => message is null) == true)
        {
            throw new InferenceException(
                "invalid_request",
                "The agent messages field cannot contain null entries.",
                ["Remove null entries from messages and retry."]);
        }

        if (request.ToolResults?.Any(static result => result is null) == true)
        {
            throw new InferenceException(
                "invalid_request",
                "The agent tool_results field cannot contain null entries.",
                ["Remove null entries from tool_results and retry."]);
        }

        ValidateToolRequestEntries(request.Tools);
    }

    /// <summary>
    /// 校验只读工作流的消息和工具集合，保证所有后续规划代码只处理有效对象。
    /// </summary>
    internal static void ValidateWorkflowRequestCollections(AgentReadOnlyWorkflowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages?.Any(static message => message is null) == true)
        {
            throw new InferenceException(
                "invalid_request",
                "The workflow messages field cannot contain null entries.",
                ["Remove null entries from messages and retry."]);
        }

        ValidateToolRequestEntries(request.Tools);
    }

    /// <summary>
    /// 拒绝工具 allowlist 中的空元素，避免手工和模型自主两条执行路径产生不一致诊断。
    /// </summary>
    private static void ValidateToolRequestEntries(IReadOnlyList<AgentChatToolRequest>? tools)
    {
        if (tools?.Any(static tool => tool is null) == true)
        {
            throw new InferenceException(
                "invalid_request",
                "The tools field cannot contain null entries.",
                ["Remove null entries from tools and retry."]);
        }
    }

    private static List<ChatMessage> BuildMessagesForWorkflow(AgentReadOnlyWorkflowRequest request)
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

    /// <summary>
    /// 将工具响应关联到模型生成的调用 ID 和已解析参数，持久化日志仍只记录参数指纹。
    /// </summary>
    private static AgentChatToolCall ToModelSelectedToolCall(
        AgentToolInvokeResponse response,
        string callId,
        JsonElement? arguments)
        => ToChatToolCall(response) with
        {
            Id = callId,
            Arguments = arguments?.Clone(),
            ModelSelected = true
        };

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

        if (normalized is "read" or "read_only" or "manual_read_only" or "context")
        {
            return "read_only";
        }

        if (normalized is "auto" or "auto_read_only" or "automatic_read_only" or "read_only_auto")
        {
            return "auto_read_only";
        }

        if (normalized is "controlled" or "manual_controlled" or "tool" or "tools")
        {
            if (!hasToolRequests)
            {
                throw new InferenceException(
                    "invalid_request",
                    "tool_mode controlled requires explicit tools[].",
                    ["Provide tools[] with controlled R8 tool arguments, or use tool_mode auto_controlled for bounded Tomur planning."]);
            }

            return "controlled";
        }

        if (normalized is "auto_controlled" or "controlled_auto" or "automatic_controlled")
        {
            return "auto_controlled";
        }

        if (normalized is "model_auto_read_only" or "model_read_only" or "model_auto")
        {
            return "model_auto_read_only";
        }

        if (normalized is "model_auto_controlled" or "model_controlled")
        {
            if (!hasToolRequests)
            {
                throw new InferenceException(
                    "invalid_request",
                    "tool_mode model_auto_controlled requires an explicit tools[] allowlist.",
                    ["List the controlled tools the model may select and set confirm=true where required."]);
            }

            return "model_auto_controlled";
        }

        throw new InferenceException(
            "invalid_request",
            $"Unsupported tool_mode '{request.ToolMode}'.",
            [
            "Use tool_mode none for plain text.",
                "Use tool_mode read_only with tools[] to invoke runtime.diagnose, tools.inspect or files.search before the agent answers.",
                "Use tool_mode auto_read_only to let Tomur select bounded read-only tools from the current request.",
                "Use tool_mode controlled with explicit tools[] to invoke connected R8 tools or runtime.repair; artifact generation and repair actions require confirm=true.",
                "Use tool_mode model_auto_read_only for bounded model-selected read-only tools.",
                "Use tool_mode model_auto_controlled with an explicit tools[] allowlist; side-effect tools still require confirm=true."
            ]);
    }

    private static int NormalizeMaxToolRounds(int? value)
        => value is > 0 ? Math.Clamp(value.Value, 1, 4) : 2;

    /// <summary>
    /// 判断当前模式是否由本地模型逐轮选择工具。
    /// </summary>
    private static bool IsModelSelectedMode(string toolMode)
        => toolMode is "model_auto_read_only" or "model_auto_controlled";

    /// <summary>
    /// 解析模型可见工具；受控模式只接受调用方显式列出的本地工具。
    /// </summary>
    private IReadOnlyList<ModelToolSelection> ResolveModelSelectedTools(
        IReadOnlyList<AgentChatToolRequest>? requestedTools,
        string toolMode)
    {
        var controlled = toolMode == "model_auto_controlled";
        var descriptors = BuildToolDescriptors(modelCatalog.ListModels())
            .Where(static descriptor => descriptor.Name != "chat.respond")
            .ToDictionary(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);

        if (requestedTools is null || requestedTools.Count == 0)
        {
            var defaults = descriptors.Values
                .Where(static descriptor =>
                    descriptor.Callable &&
                    string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase))
                .Select(static descriptor => new ModelToolSelection(descriptor, null))
                .ToArray();
            if (defaults.Length > 0 && !controlled)
            {
                return defaults;
            }

            throw new InferenceException(
                "no_model_tools_available",
                "No callable tools are available for the requested model-selected mode.",
                ["Inspect GET /api/agents/tools and provide an explicit tools[] allowlist."]);
        }

        if (requestedTools.Count > 32)
        {
            throw new InferenceException(
                "too_many_tools",
                "A model-selected Agent request can declare at most 32 tools.",
                ["Reduce the tools[] allowlist for this request."]);
        }

        var selections = new List<ModelToolSelection>(requestedTools.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedTools)
        {
            var name = requested.Tool?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                throw new InferenceException(
                    "invalid_request",
                    string.IsNullOrWhiteSpace(name)
                        ? "Every model-selected tools[] entry requires a tool name."
                        : $"Tool '{name}' appears more than once in the allowlist.",
                    ["Provide unique tool names from GET /api/agents/tools."]);
            }

            if (!descriptors.TryGetValue(name, out var descriptor) || !descriptor.Callable)
            {
                throw new InferenceException(
                    "tool_not_callable",
                    $"Tool '{name}' is not ready and callable for model-selected execution.",
                    ["Inspect GET /api/agents/tools for current readiness and supported actions."]);
            }

            if (!controlled && !string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase))
            {
                throw new InferenceException(
                    "tool_requires_controlled_mode",
                    $"Tool '{name}' is not read-only and cannot be declared in model_auto_read_only mode.",
                    ["Use model_auto_controlled with an explicit allowlist and confirm=true where required."]);
            }

            if (controlled &&
                HasModelToolSideEffect(descriptor) &&
                requested.Confirm == true &&
                requested.Arguments is not { ValueKind: JsonValueKind.Object })
            {
                throw new InferenceException(
                    "invalid_request",
                    $"Confirmed tool '{name}' requires an object arguments value that identifies the approved action.",
                    ["Provide the exact arguments to approve, or omit confirm so the call remains blocked."]);
            }

            selections.Add(new ModelToolSelection(descriptor, requested));
        }

        return selections;
    }

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

    private static IReadOnlyList<AgentChatToolRequest> ResolveAutoTools(
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

        if (MentionsFileSearch(text))
        {
            planned.Add(new AgentChatToolRequest(
                FileSearchFunction.ToolName,
                JsonSerializer.SerializeToElement(
                    new FileSearchToolArguments(CreateFileSearchQuery(text), null, 5, true, null, null),
                    AppJsonSerializerContext.Default.FileSearchToolArguments)));
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

    private static AgentToolInvokeRequest CreateToolInvokeRequest(
        AgentChatToolRequest toolRequest,
        string requestedToolMode)
        => new(toolRequest.Tool, toolRequest.Arguments)
        {
            Mode = requestedToolMode is "controlled" or "auto_controlled" ? "controlled" : "read_only",
            Confirm = toolRequest.Confirm
        };

    private static string ResolveAuditMode(string requestedToolMode)
        => requestedToolMode switch
        {
            "auto_read_only" => "auto-read-only",
            "controlled" => "chat-controlled",
            "auto_controlled" => "auto-controlled",
            _ => "chat-context-read-only"
        };

    private static string ResolveInvocationKind(string requestedToolMode)
        => requestedToolMode switch
        {
            "auto_read_only" => "auto",
            "controlled" => "controlled",
            "auto_controlled" => "auto-controlled",
            _ => "chat-context"
        };

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

    private static bool MentionsFileSearch(string value)
        => ContainsAny(
            value,
            "file",
            "files",
            "document",
            "documents",
            "rag",
            "search",
            "lookup",
            "local knowledge",
            "文件",
            "文档",
            "资料",
            "检索",
            "搜索",
            "问答");

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

    private static string CreateFileSearchQuery(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 240)
        {
            normalized = normalized[..240];
        }

        return normalized;
    }

    private static bool IsNonOkToolStatus(string status)
        => !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);

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
            "ready",
            "SQLite/local files",
            null,
            "/api/agents/tools/invoke",
            """{"type":"object","required":["query"],"properties":{"query":{"type":"string"},"root":{"type":"string"},"top_k":{"type":"integer","minimum":1,"maximum":20},"refresh":{"type":"boolean"},"max_files":{"type":"integer","minimum":1,"maximum":4096},"max_file_bytes":{"type":"integer","minimum":1,"maximum":5242880}}}""",
            "read",
            true,
            false,
            ["manual-read-only", "chat-context", "auto-read-only", "read-only-workflow", "model-auto-read-only", "model-auto-controlled"],
            "Local file Q&A searches Tomur-managed text files through SQLite FTS without PostgreSQL.",
            ["Place text documents under the Tomur data files directory before invoking files.search."]);

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
            ["manual-read-only", "chat-context", "auto-read-only", "read-only-workflow", "model-auto-read-only", "model-auto-controlled"],
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
            ["manual-read-only", "chat-context", "auto-read-only", "read-only-workflow", "model-auto-read-only", "model-auto-controlled"],
            "Agent Framework tool declarations can be inspected without invoking side-effect tools.",
            []);

        yield return new AgentToolDescriptor(
            "runtime.repair",
            "Runtime Repair",
            "ready",
            "tomur doctor/native/session APIs",
            null,
            "/api/agents/tools/invoke",
            """{"type":"object","required":["action"],"properties":{"action":{"type":"string","enum":["native.prepare","session.unload"]},"reason":{"type":"string"}}}""",
            "repairs-local-runtime",
            true,
            true,
            ["manual-controlled", "chat-controlled", "model-auto-controlled", "requires-confirmation"],
            "Runtime repair actions are explicit controlled tools and require confirm=true; read-only diagnosis remains runtime.diagnose.",
            ["Confirm the specific repair action with the user before invoking runtime.repair."]);
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
            ResolveToolCallable(backendId, status),
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
            ? ["dedicated-endpoint", "manual-controlled", "chat-controlled", "model-auto-controlled", "requires-confirmation"]
            : ["dedicated-endpoint", "manual-controlled", "chat-controlled", "model-auto-read-only", "model-auto-controlled", "planned-agent-tool"];

    private static string ResolveExecutableToolStatus(string backendId)
        => "ready";

    private static bool ResolveToolCallable(string backendId, string status)
        => string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) &&
            backendId is "image-generation" or "vlm" or "ocr" or "asr" or "tts";

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
            "asr" => """{"type":"object","properties":{"audio_data_uri":{"type":"string"},"audio_base64":{"type":"string"},"media_type":{"type":"string"},"language":{"type":"string"}}}""",
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

internal readonly record struct ModelToolApprovalDecision(
    bool Requested,
    bool Effective,
    bool Consumed,
    bool Reused);

internal sealed class ModelToolApprovalTracker
{
    private readonly HashSet<string> consumedTools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 计算本次模型调用的确认状态；匹配的副作用批准会在执行尝试前原子式消费一次。
    /// </summary>
    public ModelToolApprovalDecision Evaluate(
        string toolName,
        bool hasSideEffect,
        AgentChatToolRequest? requested,
        JsonElement modelArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var confirmationRequested = requested?.Confirm == true;
        if (!hasSideEffect)
        {
            return new ModelToolApprovalDecision(
                confirmationRequested,
                Effective: false,
                Consumed: false,
                Reused: false);
        }

        if (!AgentRuntimeService.IsConfirmedModelToolArguments(requested, modelArguments))
        {
            return new ModelToolApprovalDecision(
                confirmationRequested,
                Effective: false,
                Consumed: false,
                Reused: false);
        }

        if (!consumedTools.Add(toolName))
        {
            return new ModelToolApprovalDecision(
                confirmationRequested,
                Effective: false,
                Consumed: false,
                Reused: true);
        }

        return new ModelToolApprovalDecision(
            confirmationRequested,
            Effective: true,
            Consumed: true,
            Reused: false);
    }
}
