using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class ToolInvoker
{
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
            telemetry.CompleteToolInvocation(activity, response);
            await eventLog.WriteToolInvocationAsync(response, invocationKind, cancellationToken).ConfigureAwait(false);
            return response;
        }

        if (CanExecuteControlled(descriptor, request, auditMode, invocationKind))
        {
            var started = DateTimeOffset.UtcNow;
            AgentToolExecutionResult result;
            try
            {
                result = await toolExecution.ExecuteAsync(
                        descriptor,
                        request.Arguments,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InferenceException exception)
            {
                logger.ToolInvocationFailed(descriptor.Name, exception);
                result = CreateControlledFailureResult(descriptor, exception);
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
                BuildControlledDiagnostics(invocationKind, result),
                new AgentToolInvokeAudit(
                    started,
                    auditMode,
                    descriptor.SideEffect,
                    descriptor.RequiresConfirmation,
                    ResolveControlledAuditActions(descriptor, result)));
            telemetry.CompleteToolInvocation(activity, response);
            await eventLog.WriteToolInvocationAsync(response, invocationKind, cancellationToken).ConfigureAwait(false);
            return response;
        }

        var blocked = new BlockedToolResult(
            ResolveBlockedCode(descriptor, request, auditMode, invocationKind),
            descriptor.Name == "chat.respond"
                ? "chat.respond is exposed as POST /api/agents/chat, not as a nested tool call in R9."
                : ResolveBlockedMessage(descriptor, request, auditMode, invocationKind),
            ResolveBlockedActions(descriptor, request, auditMode, invocationKind));

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
        telemetry.CompleteToolInvocation(activity, blockedResponse);
        await eventLog.WriteToolInvocationAsync(blockedResponse, invocationKind, cancellationToken).ConfigureAwait(false);
        return blockedResponse;
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
        string invocationKind)
    {
        if (!IsControlledMode(request, auditMode, invocationKind))
        {
            return false;
        }

        if (!descriptor.Callable)
        {
            return false;
        }

        if (descriptor.RequiresConfirmation && request.Confirm != true)
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

    private static IReadOnlyList<string> BuildControlledDiagnostics(
        string invocationKind,
        AgentToolExecutionResult result)
    {
        var diagnostics = new List<string>
        {
            $"invocation: {invocationKind}",
            "scope: r9-controlled-r8-tool",
            "source: Tomur R8 local adapter"
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

    private static AgentToolExecutionResult CreateControlledFailureResult(
        AgentToolDescriptor descriptor,
        InferenceException exception)
    {
        var diagnostic = new RuntimeDiagnostic(
            "error",
            exception.Code,
            exception.Message,
            descriptor.Model,
            exception.Actions);
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

    private static string ResolveBlockedCode(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind)
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

        if (descriptor.RequiresConfirmation && request.Confirm != true)
        {
            return "tool_requires_confirmation";
        }

        return "tool_not_callable";
    }

    private static string ResolveBlockedMessage(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind)
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

        if (descriptor.RequiresConfirmation && request.Confirm != true)
        {
            return $"Tool '{descriptor.Name}' generates a local artifact and requires confirm=true.";
        }

        return $"Tool '{descriptor.Name}' is visible in the R9 tool map but is not enabled for this invocation.";
    }

    private static IReadOnlyList<string> ResolveBlockedActions(
        AgentToolDescriptor descriptor,
        AgentToolInvokeRequest request,
        string auditMode,
        string invocationKind)
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

        if (descriptor.RequiresConfirmation && request.Confirm != true)
        {
            return actions
                .Prepend("Set confirm to true after the user explicitly approves local artifact generation.")
                .ToArray();
        }

        return actions;
    }
}
