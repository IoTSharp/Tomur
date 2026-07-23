using System.Text.Json;
using Tomur.Api;
using Tomur.Api.OpenAI;
using Tomur.Inference;
using Tomur.Serialization;

namespace Tomur.Providers.M10.Tests;

public sealed class OpenAiToolCallingWireContractTests
{
    /// <summary>
    /// 验证 OpenAI 工具请求通过 source-generated JSON 往返声明、选择、调用和结果关联字段。
    /// </summary>
    [Fact]
    public void ToolCallingRequestRoundTripsSourceGeneratedContract()
    {
        var request = new OpenAiChatCompletionRequest(
            "ready",
            [
                new OpenAiChatMessage("assistant", null)
                {
                    ToolCalls =
                    [
                        new OpenAiChatToolCall(
                            "call_1",
                            "function",
                            new OpenAiChatToolCallFunction("get_weather", "{\"city\":\"Urumqi\"}"))
                    ]
                },
                new OpenAiChatMessage("tool", ParseJson("\"sunny\""))
                {
                    ToolCallId = "call_1"
                }
            ],
            true,
            null,
            null,
            null)
        {
            Tools =
            [
                new OpenAiChatTool(
                    "function",
                    new OpenAiChatToolFunction(
                        "get_weather",
                        "Read weather",
                        ParseJson("{\"type\":\"object\"}")))
            ],
            ToolChoice = ParseJson("{\"type\":\"function\",\"function\":{\"name\":\"get_weather\"}}"),
            ParallelToolCalls = false
        };

        var json = JsonSerializer.Serialize(
            request,
            AppJsonSerializerContext.Default.OpenAiChatCompletionRequest);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(roundTrip);
        Assert.False(roundTrip.ParallelToolCalls);
        Assert.Equal("get_weather", Assert.Single(roundTrip.Tools!).Function?.Name);
        Assert.Equal(
            "get_weather",
            roundTrip.ToolChoice?.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call_1", roundTrip.Messages![0].ToolCalls![0].Id);
        Assert.Equal("call_1", roundTrip.Messages[1].ToolCallId);
    }

    /// <summary>
    /// 验证工具调用响应通过 source-generated JSON 显式保留 content:null，并完整往返调用信息。
    /// </summary>
    [Fact]
    public void ToolCallingAssistantMessageWritesExplicitNullContent()
    {
        var message = new OpenAiChatCompletionMessage("assistant", null)
        {
            ToolCalls =
            [
                new OpenAiChatToolCall(
                    "call_1",
                    "function",
                    new OpenAiChatToolCallFunction("get_weather", "{\"city\":\"Urumqi\"}"))
            ]
        };

        var json = JsonSerializer.Serialize(
            message,
            AppJsonSerializerContext.Default.OpenAiChatCompletionMessage);

        using var document = JsonDocument.Parse(json);
        var content = document.RootElement.GetProperty("content");
        Assert.Equal(JsonValueKind.Null, content.ValueKind);

        var roundTrip = JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OpenAiChatCompletionMessage);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip.Content);
        var toolCall = Assert.Single(roundTrip.ToolCalls!);
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("function", toolCall.Type);
        Assert.Equal("get_weather", toolCall.Function?.Name);
        Assert.Equal("{\"city\":\"Urumqi\"}", toolCall.Function?.Arguments);
    }

    /// <summary>
    /// 验证普通文本响应继续输出原 content，且未设置的 tool_calls 不改变既有 JSON 形状。
    /// </summary>
    [Fact]
    public void TextAssistantMessagePreservesExistingWireShape()
    {
        var message = new OpenAiChatCompletionMessage("assistant", "Forecast ready.");

        var json = JsonSerializer.Serialize(
            message,
            AppJsonSerializerContext.Default.OpenAiChatCompletionMessage);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Forecast ready.", document.RootElement.GetProperty("content").GetString());
        Assert.False(document.RootElement.TryGetProperty("tool_calls", out _));

        var roundTrip = JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OpenAiChatCompletionMessage);
        Assert.NotNull(roundTrip);
        Assert.Equal("Forecast ready.", roundTrip.Content);
        Assert.Null(roundTrip.ToolCalls);
    }

    /// <summary>
    /// 验证 messages、tools 和 tool_calls 数组中的 null 元素都收敛为稳定 invalid_request。
    /// </summary>
    [Fact]
    public void NullToolProtocolArrayEntriesReturnInvalidRequest()
    {
        var nullMessage = DeserializeRequest(
            """{"model":"ready","messages":[null]}""");
        Assert.False(ApiRouteExtensions.UsesOpenAiToolProtocol(nullMessage));
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOpenAiInputCharacters(nullMessage));

        var nullTool = DeserializeRequest(
            """{"model":"ready","messages":[{"role":"user","content":"hello"}],"tools":[null]}""");
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOpenAiInputCharacters(nullTool));

        var nullCall = DeserializeRequest(
            """{"model":"ready","messages":[{"role":"assistant","content":null,"tool_calls":[null]}]}""");
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOpenAiInputCharacters(nullCall));
    }

    /// <summary>
    /// 使用 OpenAI source-generated 契约反序列化真实线协议输入。
    /// </summary>
    private static OpenAiChatCompletionRequest DeserializeRequest(string json)
        => JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OpenAiChatCompletionRequest)!;

    /// <summary>
    /// 断言适配器把畸形线协议输入转换为 invalid_request，而不是空引用异常。
    /// </summary>
    private static void AssertInvalidRequest(Action action)
    {
        var exception = Assert.Throws<InferenceException>(action);
        Assert.Equal("invalid_request", exception.Code);
    }

    /// <summary>
    /// 克隆测试 JSON 根元素，确保临时文档释放后仍可参与 source-generated 序列化。
    /// </summary>
    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
