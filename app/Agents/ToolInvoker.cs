using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class ToolInvoker
{
    private static readonly TimeSpan SideEffectAuditTimeout = TimeSpan.FromSeconds(5);
    private readonly AgentRuntimeService agentRuntime;
    private readonly ToolFactory toolFactory;
    private readonly ToolExecutionService toolExecution;
    private readonly AgentEventLog eventLog;
    private readonly AgentTelemetry telemetry;
    private readonly ILogger<ToolInvoker> logger;

    public ToolInvoker(
        AgentRuntimeService agentRuntime,
        ToolFactory toolFactory,
        ToolExecutionService toolExecution,
        AgentEventLog eventLog,
        AgentTelemetry telemetry,
        ILogger<ToolInvoker> logger)
    {
        this.agentRuntime = agentRuntime;
        this.toolFactory = toolFactory;
        this.toolExecution = toolExecution;
        this.eventLog = eventLog;
        this.telemetry = telemetry;
        this.logger = logger;
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
        => await InvokeAsync(
                request,
                auditMode,
                invocationKind,
                null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 在显式审计上下文下执行工具，模型调用的标识、参数指纹和确认状态会随事件持久化。
    /// </summary>
    internal async Task<AgentToolInvokeResponse> InvokeAsync(
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext,
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
                ["Set tool to runtime.diagnose, tools.inspect, or a callable R8 tool exposed by GET /api/agents/tools."]);
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

        using var activity = telemetry.StartToolInvocation(toolName, invocationKind, auditMode);
        if (auditContext?.ConfirmationReused == true)
        {
            var reusedConfirmationResult = new BlockedToolResult(
                "tool_confirmation_already_consumed",
                $"The one-use confirmation for tool '{descriptor.Name}' was already consumed by an earlier model-selected call.",
                ["Ask the user for a new explicit confirmation before attempting this side effect again."]);
            var repeatedResponse = new AgentToolInvokeResponse(
                "blocked",
                descriptor.Name,
                "Microsoft.Extensions.AI.AITool",
                "Tomur.Agents.ModelSelectedToolFunction",
                descriptor.InputSchema,
                0,
                AgentToolResultJson.ToJsonElement(reusedConfirmationResult),
                [
                    $"invocation: {invocationKind}",
                    "scope: r16-model-selected-confirmation",
                    "reason: one-use confirmation was already consumed"
                ],
                CreateAudit(
                    new AgentToolInvokeAudit(
                        DateTimeOffset.UtcNow,
                        $"{auditMode}-blocked",
                        descriptor.SideEffect,
                        RequiresConfirmation(descriptor, auditContext),
                        reusedConfirmationResult.Actions),
                    auditContext));
            telemetry.CompleteToolInvocation(activity, repeatedResponse);
            await WriteToolInvocationAuditAsync(
                    repeatedResponse,
                    invocationKind,
                    auditContext,
                    sideEffectExecutionStarted: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return repeatedResponse;
        }

        if (auditContext?.ModelSelected == true &&
            HasSideEffect(descriptor) &&
            auditContext.ConfirmationEffective != true)
        {
            var code = auditContext.ConfirmationRequested
                ? "tool_arguments_not_approved"
                : "tool_requires_confirmation";
            var confirmationResult = new BlockedToolResult(
                code,
                auditContext.ConfirmationRequested
                    ? $"Tool '{descriptor.Name}' arguments do not exactly match the one-use approved JSON object."
                    : $"Tool '{descriptor.Name}' requires explicit confirmation for its exact JSON arguments.",
                ["Ask the user to approve the exact side-effect arguments in a new Agent request."]);
            var confirmationResponse = new AgentToolInvokeResponse(
                "blocked",
                descriptor.Name,
                "Microsoft.Extensions.AI.AITool",
                "Tomur.Agents.ModelSelectedToolFunction",
                descriptor.InputSchema,
                0,
                AgentToolResultJson.ToJsonElement(confirmationResult),
                [
                    $"invocation: {invocationKind}",
                    "scope: r16-model-selected-confirmation",
                    $"reason: {code}"
                ],
                CreateAudit(
                    new AgentToolInvokeAudit(
                        DateTimeOffset.UtcNow,
                        $"{auditMode}-blocked",
                        descriptor.SideEffect,
                        true,
                        confirmationResult.Actions),
                    auditContext));
            telemetry.CompleteToolInvocation(activity, confirmationResponse);
            await WriteToolInvocationAuditAsync(
                    confirmationResponse,
                    invocationKind,
                    auditContext,
                    sideEffectExecutionStarted: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return confirmationResponse;
        }

        var safeTool = toolFactory.CreateSafeReadOnlyTools().FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (safeTool is ILocalAgentTool invokable)
        {
            var started = DateTimeOffset.UtcNow;
            AgentToolInvokeResponse safeResponse;
            try
            {
                var result = await invokable.InvokeLocalAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
                safeResponse = new AgentToolInvokeResponse(
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
                    CreateAudit(
                        new AgentToolInvokeAudit(
                            started,
                            auditMode,
                            descriptor.SideEffect,
                            false,
                            descriptor.Actions),
                        auditContext));
            }
            catch (Exception exception) when (IsRecoverableToolException(exception))
            {
                logger.ToolInvocationFailed(descriptor.Name, exception);
                safeResponse = CreateFailureResponse(
                    descriptor,
                    started,
                    auditMode,
                    invocationKind,
                    "Microsoft.Extensions.AI.AITool",
                    safeTool.GetType().FullName ?? "AITool",
                    exception,
                    auditContext);
            }

            telemetry.CompleteToolInvocation(activity, safeResponse);
            await WriteToolInvocationAuditAsync(
                    safeResponse,
                    invocationKind,
                    auditContext,
                    sideEffectExecutionStarted: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return safeResponse;
        }

        var modelSelectedReadOnly = CanExecuteModelSelectedReadOnly(
            descriptor,
            auditMode,
            invocationKind);
        if (modelSelectedReadOnly || CanExecuteControlled(
                descriptor,
                request,
                auditMode,
                invocationKind,
                auditContext))
        {
            var started = DateTimeOffset.UtcNow;
            var sideEffectExecutionStarted = HasSideEffect(descriptor);
            ExceptionDispatchInfo? cancellationFailure = null;
            AgentToolExecutionResult result;
            try
            {
                result = await toolExecution.ExecuteAsync(
                        descriptor,
                        request.Arguments,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                // 副作用可能已经开始，先构造可审计结果，写入后再恢复原始取消异常。
                cancellationFailure = ExceptionDispatchInfo.Capture(exception);
                result = CreateFailureResult(
                    descriptor,
                    new InferenceException(
                        "tool_execution_canceled",
                        $"Tool '{descriptor.Name}' execution was canceled.",
                        ["Retry only after confirming whether the previous side effect completed."]));
            }
            catch (Exception exception) when (IsRecoverableToolException(exception))
            {
                logger.ToolInvocationFailed(descriptor.Name, exception);
                result = CreateFailureResult(descriptor, exception);
            }

            var response = new AgentToolInvokeResponse(
                string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase) ? "ok" : "error",
                descriptor.Name,
                "Tomur.R8.LocalTool",
                "Tomur.Agents.ToolExecutionService",
                descriptor.InputSchema,
                result.ElapsedMs > 0
                    ? result.ElapsedMs
                    : (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
                AgentToolResultJson.ToJsonElement(result),
                BuildConnectedToolDiagnostics(invocationKind, result, modelSelectedReadOnly),
                CreateAudit(
                    new AgentToolInvokeAudit(
                        started,
                        auditMode,
                        descriptor.SideEffect,
                        RequiresConfirmation(descriptor, auditContext),
                        ResolveControlledAuditActions(descriptor, result)),
                    auditContext));
            telemetry.CompleteToolInvocation(activity, response);
            await WriteToolInvocationAuditAsync(
                    response,
                    invocationKind,
                    auditContext,
                    sideEffectExecutionStarted,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationFailure?.Throw();
            return response;
        }

        var blocked = new BlockedToolResult(
            ResolveBlockedCode(descriptor, request, auditMode, invocationKind, auditContext),
            descriptor.Name == "chat.respond"
                ? "chat.respond is exposed as POST /api/agents/chat, not as a nested tool call in R9."
                : ResolveBlockedMessage(descriptor, request, auditMode, invocationKind, auditContext),
            ResolveBlockedActions(descriptor, request, auditMode, invocationKind, auditContext));

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
                "reason: tool requires readiness, an eligible invocation mode, or explicit allowlist and confirmation"
            ],
            CreateAudit(
                new AgentToolInvokeAudit(
                    DateTimeOffset.UtcNow,
                    $"{auditMode}-blocked",
                    descriptor.SideEffect,
                    RequiresConfirmation(descriptor, auditContext),
                    blocked.Actions),
                auditContext));
        telemetry.CompleteToolInvocation(activity, blockedResponse);
        await WriteToolInvocationAuditAsync(
                blockedResponse,
                invocationKind,
                auditContext,
                sideEffectExecutionStarted: false,
                cancellationToken)
            .ConfigureAwait(false);
        return blockedResponse;
    }

    /// <summary>
    /// 记录模型调用在进入实际工具前发生的可恢复错误，并把结构化结果交回推理循环。
    /// </summary>
    internal async Task<AgentToolInvokeResponse> RecordModelSelectedFailureAsync(
        AgentToolDescriptor descriptor,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext auditContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(auditContext);
        ArgumentNullException.ThrowIfNull(exception);

        using var activity = telemetry.StartToolInvocation(descriptor.Name, invocationKind, auditMode);
        var started = DateTimeOffset.UtcNow;
        logger.ToolInvocationFailed(descriptor.Name, exception);
        var response = CreateFailureResponse(
            descriptor,
            started,
            auditMode,
            invocationKind,
            "Microsoft.Extensions.AI.AITool",
            "Tomur.Agents.ModelSelectedToolFunction",
            exception,
            auditContext);
        telemetry.CompleteToolInvocation(activity, response);
        await WriteToolInvocationAuditAsync(
                response,
                invocationKind,
                auditContext,
                sideEffectExecutionStarted: false,
                cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    private static string ResolveBlockedAction(AgentToolDescriptor descriptor)
        => descriptor.Name switch
        {
            "chat.respond" => "Use POST /api/agents/chat for the current plain text Agent Framework endpoint.",
            "image.generate" => "Set mode to controlled and confirm to true before invoking this local artifact-generating tool, or use POST /v1/images/generations manually.",
            "vision.analyze" => "Set mode to controlled and send data URI images, or use POST /api/vision/analyze manually.",
            "ocr.recognize" => "Set mode to controlled and send a data URI image, or use POST /api/ocr/analyze manually.",
            "audio.transcribe" => "Set mode to controlled and send audio_data_uri or audio_base64, or use POST /v1/audio/transcriptions manually.",
            "audio.speak" => "Set mode to controlled and confirm to true before invoking this local artifact-generating tool, or use POST /v1/audio/speech manually.",
            "files.search" => "Invoke files.search as a read-only tool with a query and files under the Tomur managed files directory.",
            "runtime.repair" => "Set mode to controlled and confirm to true before invoking a supported runtime repair action.",
            _ => "Inspect GET /api/agents/tools for the current route, readiness and diagnostic actions."
        };

    private static bool CanExecuteControlled(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext)
    {
        if (!IsControlledMode(request, auditMode, invocationKind))
        {
            return false;
        }

        if (!descriptor.Callable)
        {
            return false;
        }

        if (RequiresConfirmation(descriptor, auditContext) && request.Confirm != true)
        {
            return false;
        }

        return descriptor.Name is
            "image.generate" or
            "vision.analyze" or
            "ocr.recognize" or
            "audio.transcribe" or
            "audio.speak" or
            "runtime.repair";
    }

    /// <summary>
    /// 只允许 Agent 内部模型自主链路执行已标记为 read 的本地适配器，公开手工入口不能伪造该授权。
    /// </summary>
    private static bool CanExecuteModelSelectedReadOnly(
        AgentToolDescriptor descriptor,
        string auditMode,
        string invocationKind)
    {
        var normalizedAuditMode = auditMode.Trim().ToLowerInvariant().Replace('-', '_');
        var normalizedInvocationKind = invocationKind.Trim().ToLowerInvariant().Replace('-', '_');
        return descriptor.Callable &&
            string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase) &&
            (normalizedAuditMode == "model_auto_read_only" || normalizedInvocationKind == "model_auto");
    }

    private static bool IsControlledMode(
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind)
    {
        if (IsControlledToken(request.Mode) ||
            IsControlledToken(auditMode) ||
            IsControlledToken(invocationKind))
        {
            return true;
        }

        return false;
    }

    private static bool IsControlledToken(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is "controlled" or "manual_controlled" or "chat_controlled" or "auto_controlled" or "controlled_auto" or "workflow_controlled";
    }

    /// <summary>
    /// 根据实际授权路径生成工具诊断，区分模型自主只读调用和显式 controlled 调用。
    /// </summary>
    private static IReadOnlyList<string> BuildConnectedToolDiagnostics(
        string invocationKind,
        AgentToolExecutionResult result,
        bool modelSelectedReadOnly)
    {
        var diagnostics = new List<string>
        {
            $"invocation: {invocationKind}",
            modelSelectedReadOnly
                ? "scope: r16-model-selected-read-only-tool"
                : "scope: r9-controlled-r8-tool",
            modelSelectedReadOnly
                ? "source: Tomur bounded Agent tool loop"
                : "source: Tomur R8 local adapter"
        };
        if (result.Tool == "runtime.repair")
        {
            diagnostics[1] = "scope: r9-controlled-runtime-tool";
            diagnostics[2] = "source: Tomur local runtime repair boundary";
        }

        diagnostics.Add($"tool-status: {result.Status}");
        if (!string.IsNullOrWhiteSpace(result.Backend))
        {
            diagnostics.Add($"backend: {result.Backend}");
        }

        if (result.Artifact is { } artifact)
        {
            diagnostics.Add($"artifact-type: {artifact.Type}");
            diagnostics.Add($"artifact-media-type: {artifact.MediaType ?? "unknown"}");
            diagnostics.Add($"artifact-format: {artifact.Format ?? "unknown"}");
            diagnostics.Add($"artifact-bytes: {artifact.Bytes}");
        }

        if (result.Diagnostic is { } diagnostic)
        {
            diagnostics.Add($"diagnostic: {diagnostic.Code}");
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> ResolveControlledAuditActions(
        AgentToolDescriptor descriptor,
        AgentToolExecutionResult result)
    {
        var actions = result.Diagnostic?.Actions ?? descriptor.Actions;
        var sanitized = actions
            .Where(static action => !ContainsPotentiallyVerboseWorkerOutput(action))
            .Select(static action => action.Length > 300 ? action[..300] + "..." : action)
            .ToArray();

        if (sanitized.Length > 0)
        {
            return sanitized;
        }

        return result.Diagnostic is null
            ? descriptor.Actions
            : ["Inspect the tool result diagnostic returned to the caller for details."];
    }

    private static bool ContainsPotentiallyVerboseWorkerOutput(string value)
        => value.Contains("worker-stderr:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("worker-stdout:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 将工具异常收敛为稳定诊断，只有已定义的推理异常会保留原始公开消息。
    /// </summary>
    private static AgentToolExecutionResult CreateFailureResult(
        AgentToolDescriptor descriptor,
        Exception exception)
    {
        string code;
        string message;
        IReadOnlyList<string> actions;
        switch (exception)
        {
            case InferenceException inference:
                code = inference.Code;
                message = inference.Message;
                actions = inference.Actions;
                break;
            case JsonException:
                code = "invalid_tool_arguments";
                message = $"Tool '{descriptor.Name}' received invalid JSON arguments.";
                actions = ["Return an object that matches the declared tool input schema."];
                break;
            case OperationCanceledException:
                code = "tool_invocation_cancelled";
                message = $"Tool '{descriptor.Name}' was cancelled before a usable result was returned.";
                actions = ["Retry only if the caller still wants this operation."];
                break;
            default:
                code = "tool_invocation_failed";
                message = $"Tool '{descriptor.Name}' failed before returning a usable result.";
                actions = ["Inspect the local Agent event log and runtime diagnostics before retrying."];
                break;
        }

        var diagnostic = new RuntimeDiagnostic(
            "error",
            code,
            message,
            descriptor.Model,
            actions);
        return new AgentToolExecutionResult(
            "error",
            descriptor.Name,
            descriptor.Backend,
            descriptor.Model,
            descriptor.Route,
            null,
            null,
            0,
            [$"{diagnostic.Code}: {diagnostic.Message}"],
            diagnostic);
    }

    /// <summary>
    /// 构造可回灌给模型的失败响应，响应中不暴露未分类异常文本。
    /// </summary>
    private static AgentToolInvokeResponse CreateFailureResponse(
        AgentToolDescriptor descriptor,
        DateTimeOffset started,
        string auditMode,
        string invocationKind,
        string toolType,
        string implementation,
        Exception exception,
        AgentToolInvocationAuditContext? auditContext)
    {
        var result = CreateFailureResult(descriptor, exception);
        return new AgentToolInvokeResponse(
            "error",
            descriptor.Name,
            toolType,
            implementation,
            descriptor.InputSchema,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            AgentToolResultJson.ToJsonElement(result),
            [
                $"invocation: {invocationKind}",
                "scope: r16-tool-error-recovery",
                $"diagnostic: {result.Diagnostic!.Code}"
            ],
            CreateAudit(
                new AgentToolInvokeAudit(
                    started,
                    auditMode,
                    descriptor.SideEffect,
                    RequiresConfirmation(descriptor, auditContext),
                    result.Diagnostic.Actions),
                auditContext));
    }

    /// <summary>
    /// 把模型调用上下文附加到公开审计对象，便于响应与持久化事件使用同一组元数据。
    /// </summary>
    private static AgentToolInvokeAudit CreateAudit(
        AgentToolInvokeAudit audit,
        AgentToolInvocationAuditContext? auditContext)
        => auditContext is null
            ? audit
            : audit with
            {
                CallId = auditContext.CallId,
                ModelSelected = auditContext.ModelSelected,
                ArgumentsSha256 = auditContext.ArgumentsSha256,
                ConfirmationRequested = auditContext.ConfirmationRequested,
                ConfirmationEffective = auditContext.ConfirmationEffective,
                ConfirmationConsumed = auditContext.ConfirmationConsumed,
                ConfirmationReused = auditContext.ConfirmationReused
            };

    /// <summary>
    /// 判断工具是否可能修改本地状态或生成产物，模型自主路径不依赖描述器确认标志兜底。
    /// </summary>
    private static bool HasSideEffect(AgentToolDescriptor descriptor)
        => !string.Equals(descriptor.SideEffect, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(descriptor.SideEffect, "read", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 模型自主副作用调用始终需要有效确认，即使描述器将确认标志误配为 false。
    /// </summary>
    private static bool RequiresConfirmation(
        AgentToolDescriptor descriptor,
        AgentToolInvocationAuditContext? auditContext)
        => descriptor.RequiresConfirmation ||
            auditContext?.ModelSelected == true && HasSideEffect(descriptor);

    /// <summary>
    /// 仅捕获能够转换为工具结果的异常，进程级致命异常继续向上传播。
    /// </summary>
    private static bool IsRecoverableToolException(Exception exception)
        => exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException;

    /// <summary>
    /// 副作用开始或工具失败后使用独立短超时写审计，避免请求取消抹掉动作与错误记录。
    /// </summary>
    private async ValueTask WriteToolInvocationAuditAsync(
        AgentToolInvokeResponse response,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext,
        bool sideEffectExecutionStarted,
        CancellationToken requestCancellationToken)
    {
        var requiresIndependentAudit = sideEffectExecutionStarted ||
            string.Equals(response.Status, "error", StringComparison.OrdinalIgnoreCase);
        if (!requiresIndependentAudit)
        {
            await eventLog.WriteToolInvocationAsync(
                    response,
                    invocationKind,
                    auditContext,
                    requestCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var auditCancellation = new CancellationTokenSource(SideEffectAuditTimeout);
        await eventLog.WriteToolInvocationAsync(
                response,
                invocationKind,
                auditContext,
                auditCancellation.Token)
            .ConfigureAwait(false);
    }

    private static string ResolveBlockedCode(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext)
    {
        if (!IsControlledMode(request, auditMode, invocationKind) &&
            descriptor.Name is "image.generate" or "vision.analyze" or "ocr.recognize" or "audio.transcribe" or "audio.speak" or "runtime.repair")
        {
            return "tool_requires_controlled_mode";
        }

        if (!descriptor.Callable)
        {
            return "tool_not_callable";
        }

        if (RequiresConfirmation(descriptor, auditContext) && request.Confirm != true)
        {
            return "tool_requires_confirmation";
        }

        return "tool_not_callable";
    }

    private static string ResolveBlockedMessage(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext)
    {
        if (!IsControlledMode(request, auditMode, invocationKind) &&
            descriptor.Name is "image.generate" or "vision.analyze" or "ocr.recognize" or "audio.transcribe" or "audio.speak" or "runtime.repair")
        {
                return $"Tool '{descriptor.Name}' is visible and callable only through the controlled R9 path, not the default read-only invocation.";
        }

        if (!descriptor.Callable)
        {
            return $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not ready or enabled for this invocation.";
        }

        if (RequiresConfirmation(descriptor, auditContext) && request.Confirm != true)
        {
            return $"Tool '{descriptor.Name}' generates a local artifact and requires confirm=true.";
        }

        return $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not enabled for this invocation.";
    }

    private static IReadOnlyList<string> ResolveBlockedActions(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind,
        AgentToolInvocationAuditContext? auditContext)
    {
        var actions = descriptor.Actions.Count == 0
            ? [ResolveBlockedAction(descriptor)]
            : descriptor.Actions.ToArray();

        if (!IsControlledMode(request, auditMode, invocationKind) &&
            descriptor.Name is "image.generate" or "vision.analyze" or "ocr.recognize" or "audio.transcribe" or "audio.speak" or "runtime.repair")
        {
            return actions
                .Prepend("Set mode to controlled on POST /api/agents/tools/invoke, or use tool_mode controlled with explicit tools[] in /api/agents/chat.")
                .ToArray();
        }

        if (RequiresConfirmation(descriptor, auditContext) && request.Confirm != true)
        {
            return actions
                .Prepend("Set confirm to true after the user explicitly approves local artifact generation.")
                .ToArray();
        }

        return actions;
    }
}
