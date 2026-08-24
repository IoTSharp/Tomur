using System.Text.Json;
using Tomur.Agents;
using Tomur.Inference;
using Tomur.Serialization;

namespace Tomur.Providers.M10.Tests;

public sealed class AgentAutonomousToolSafetyTests
{
    /// <summary>
    /// 验证模型工具会克隆批准参数，并明确告知副作用调用只能使用一次完全一致的 JSON。
    /// </summary>
    [Fact]
    public void ModelSelectedToolClonesApprovedArgumentsAndDescribesExactMatch()
    {
        ModelSelectedToolFunction function;
        using (var document = JsonDocument.Parse("""{"prompt":"approved-value"}"""))
        {
            function = new ModelSelectedToolFunction(
                CreateDescriptor(requiresConfirmation: false),
                document.RootElement);
        }

        Assert.Contains("exactly this approved JSON object once", function.Description, StringComparison.Ordinal);
        Assert.Contains("approved-value", function.Description, StringComparison.Ordinal);
        Assert.Equal(
            "approved-value",
            function.ApprovedArguments!.Value.GetProperty("prompt").GetString());
    }

    /// <summary>
    /// 验证副作用确认按完整参数匹配且只能消费一次，描述器误配确认标志也不能绕过边界。
    /// </summary>
    [Fact]
    public void SideEffectApprovalRequiresExactArgumentsAndIsConsumedOnce()
    {
        var approved = ParseJson("""{"prompt":"approved-value","steps":4}""");
        var requested = new AgentChatToolRequest("image.generate", approved)
        {
            Confirm = true
        };
        var tracker = new ModelToolApprovalTracker();
        var descriptor = CreateDescriptor(requiresConfirmation: false);

        Assert.True(AgentRuntimeService.HasModelToolSideEffect(descriptor));
        var first = tracker.Evaluate(descriptor.Name, true, requested, approved);
        var repeated = tracker.Evaluate(descriptor.Name, true, requested, approved);

        Assert.True(first.Effective);
        Assert.True(first.Consumed);
        Assert.False(first.Reused);
        Assert.False(repeated.Effective);
        Assert.False(repeated.Consumed);
        Assert.True(repeated.Reused);

        var mismatchTracker = new ModelToolApprovalTracker();
        var mismatch = mismatchTracker.Evaluate(
            descriptor.Name,
            true,
            requested,
            ParseJson("""{"prompt":"changed-value","steps":4}"""));
        Assert.True(mismatch.Requested);
        Assert.False(mismatch.Effective);
        Assert.False(mismatch.Consumed);
    }

    /// <summary>
    /// 验证审计使用属性顺序无关的参数指纹，并且持久化事件不包含原始参数内容。
    /// </summary>
    [Fact]
    public void ToolAuditPersistsFingerprintWithoutRawArguments()
    {
        var arguments = ParseJson("""{"prompt":"secret-value","options":{"steps":4,"seed":7}}""");
        var reordered = ParseJson("""{"options":{"seed":7,"steps":4},"prompt":"secret-value"}""");
        var context = AgentToolInvocationAuditContext.CreateModelSelected(
            "call-audit",
            arguments,
            confirmationRequested: true,
            confirmationEffective: true,
            confirmationConsumed: true,
            confirmationReused: false);
        var response = new AgentToolInvokeResponse(
            "ok",
            "image.generate",
            "Tomur.R8.LocalTool",
            "Tomur.Agents.ToolExecutionService",
            """{"type":"object"}""",
            12,
            null,
            [],
            new AgentToolInvokeAudit(
                DateTimeOffset.UtcNow,
                "model-auto-controlled",
                "generates-local-artifact",
                true,
                []));

        var entry = AgentEventLogEntry.FromToolInvocation(response, "auto-controlled", context);
        var json = JsonSerializer.Serialize(entry, AppJsonSerializerContext.Default.AgentEventLogEntry);

        Assert.Equal(
            AgentToolArgumentFingerprint.ComputeSha256(reordered),
            context.ArgumentsSha256);
        Assert.Equal("call-audit", entry.CallId);
        Assert.True(entry.ModelSelected);
        Assert.Equal(true, entry.ConfirmationRequested);
        Assert.Equal(true, entry.ConfirmationEffective);
        Assert.Equal(true, entry.ConfirmationConsumed);
        Assert.Equal(false, entry.ConfirmationReused);
        Assert.Equal(64, entry.ArgumentsSha256!.Length);
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"arguments\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证 Agent Chat 在进入消息或工具规划前拒绝集合中的空元素。
    /// </summary>
    [Fact]
    public void AgentChatRejectsNullCollectionEntries()
    {
        var requests = new AgentChatRequest[]
        {
            new(null, "hello", [null!], null, null, null, null, null, null, null, null),
            new(null, "hello", null, [null!], null, null, null, null, null, null, null),
            new(null, "hello", null, null, "model_auto_read_only", [null!], null, null, null, null, null)
        };

        foreach (var request in requests)
        {
            var exception = Assert.Throws<InferenceException>(
                () => AgentRuntimeService.ValidateChatRequestCollections(request));
            Assert.Equal("invalid_request", exception.Code);
        }
    }

    /// <summary>
    /// 验证只读工作流同样拒绝空消息和空工具条目。
    /// </summary>
    [Fact]
    public void AgentWorkflowRejectsNullCollectionEntries()
    {
        var requests = new AgentReadOnlyWorkflowRequest[]
        {
            new(null, "hello", [null!], null, null, null, null, null, null, null),
            new(null, "hello", null, [null!], null, null, null, null, null, null)
        };

        foreach (var request in requests)
        {
            var exception = Assert.Throws<InferenceException>(
                () => AgentRuntimeService.ValidateWorkflowRequestCollections(request));
            Assert.Equal("invalid_request", exception.Code);
        }
    }

    /// <summary>
    /// 验证 Agent 自主请求可以显式关闭同轮多个工具调用，并通过 source-generated JSON 保留字段。
    /// </summary>
    [Fact]
    public void AgentChatPreservesParallelToolCallPreference()
    {
        var request = new AgentChatRequest(
            null,
            "inspect runtime",
            null,
            null,
            "model_auto_read_only",
            null,
            2,
            null,
            null,
            null,
            null)
        {
            ParallelToolCalls = false
        };

        var json = JsonSerializer.Serialize(
            request,
            AppJsonSerializerContext.Default.AgentChatRequest);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
    }

    /// <summary>验证车牌大图不会进入默认模型自主只读工具集合，只允许显式受控路径。</summary>
    [Fact]
    public void PlateRecognitionRequiresControlledModelInvocation()
    {
        var descriptor = new AgentToolDescriptor(
            "plate.recognize",
            "License Plate Recognition",
            "ready",
            "test-backend",
            null,
            "/api/agents/tools/invoke",
            """{"type":"object"}""",
            "read",
            true,
            false,
            AgentRuntimeService.ResolvePlateRecognitionInvocationModes(),
            "Recognize one approved image.",
            []);

        Assert.False(AgentRuntimeService.SupportsInvocationMode(descriptor, "model_auto_read_only"));
        Assert.False(AgentRuntimeService.IsDefaultModelReadOnlyTool(descriptor));
        Assert.True(AgentRuntimeService.SupportsInvocationMode(descriptor, "model_auto_controlled"));
        Assert.True(AgentRuntimeService.SupportsInvocationMode(descriptor, "manual-controlled"));
    }

    /// <summary>验证工具失败后返回确定性诊断，不再让聊天模型生成未经工具支持的结论。</summary>
    [Fact]
    public void FailedToolCallUsesDeterministicDiagnosticResponse()
    {
        var result = ParseJson(
            """{"status":"error","diagnostic":{"message":"failed to load llama model"}}""");
        var toolCall = new AgentChatToolCall(
            "vision.analyze",
            "error",
            7856,
            result,
            [],
            new AgentToolInvokeAudit(DateTimeOffset.UtcNow, "test", "read", false, []));

        Assert.True(AgentRuntimeService.HasFailedToolCalls([toolCall]));
        Assert.Equal(
            "Tool 'vision.analyze' returned status 'error': failed to load llama model. No model-generated summary was produced.",
            AgentRuntimeService.BuildFailedToolResponse([toolCall]));
    }

    /// <summary>
    /// 创建确认边界测试使用的副作用工具描述器。
    /// </summary>
    private static AgentToolDescriptor CreateDescriptor(bool requiresConfirmation)
        => new(
            "image.generate",
            "Image Generation",
            "ready",
            "test-backend",
            null,
            "/v1/images/generations",
            """{"type":"object","required":["prompt"],"properties":{"prompt":{"type":"string"}}}""",
            "generates-local-artifact",
            true,
            requiresConfirmation,
            ["model-auto-controlled"],
            "Generate an approved local image.",
            []);

    /// <summary>
    /// 克隆 JSON 根元素，避免测试文档释放影响断言。
    /// </summary>
    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
