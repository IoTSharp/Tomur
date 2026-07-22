using System.Text.Json;
using Tomur.Agents;
using Tomur.Api;
using Tomur.Api.Ollama;
using Tomur.Inference;
using Tomur.Serialization;

namespace Tomur.Providers.M10.Tests;

public sealed class OllamaToolCallingWireContractTests
{
    /// <summary>
    /// 验证 Ollama 工具请求通过 source-generated JSON 往返调用 ID、对象参数和结果关联字段。
    /// </summary>
    [Fact]
    public void ToolCallingRequestRoundTripsSourceGeneratedContract()
    {
        var request = new OllamaChatRequest(
            "ready",
            [
                new OllamaChatMessage("assistant", string.Empty)
                {
                    ToolCalls =
                    [
                        new OllamaChatToolCall(
                            new OllamaChatToolCallFunction(
                                "get_weather",
                                ParseJson("{\"city\":\"Urumqi\"}")))
                        {
                            Id = "call_1"
                        }
                    ]
                },
                new OllamaChatMessage("tool", "sunny")
                {
                    ToolName = "get_weather",
                    ToolCallId = "call_1"
                }
            ],
            true,
            null,
            null)
        {
            Tools =
            [
                new OllamaChatTool(
                    "function",
                    new OllamaChatToolFunction(
                        "get_weather",
                        "Read weather",
                        ParseJson("{\"type\":\"object\"}")))
            ]
        };

        var json = JsonSerializer.Serialize(
            request,
            AppJsonSerializerContext.Default.OllamaChatRequest);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OllamaChatRequest);

        Assert.NotNull(roundTrip);
        Assert.Equal("get_weather", Assert.Single(roundTrip.Tools!).Function?.Name);
        var call = Assert.Single(roundTrip.Messages![0].ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("Urumqi", call.Function?.Arguments.GetProperty("city").GetString());
        Assert.Equal("call_1", roundTrip.Messages[1].ToolCallId);
        Assert.Equal("get_weather", roundTrip.Messages[1].ToolName);
    }

    /// <summary>
    /// 验证 messages、tools 和 tool_calls 数组中的 null 元素都收敛为稳定 invalid_request。
    /// </summary>
    [Fact]
    public void NullToolProtocolArrayEntriesReturnInvalidRequest()
    {
        var nullMessage = DeserializeRequest(
            """{"model":"ready","messages":[null]}""");
        Assert.False(ApiRouteExtensions.UsesOllamaToolProtocol(nullMessage));
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOllamaInputCharacters(nullMessage));

        var nullTool = DeserializeRequest(
            """{"model":"ready","messages":[{"role":"user","content":"hello"}],"tools":[null]}""");
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOllamaInputCharacters(nullTool));

        var nullCall = DeserializeRequest(
            """{"model":"ready","messages":[{"role":"assistant","content":"","tool_calls":[null]}]}""");
        AssertInvalidRequest(() => ToolCallingChatAdapter.EstimateOllamaInputCharacters(nullCall));
    }

    /// <summary>
    /// 验证启用 tools 后仍完整保留 Ollama 文本路径合并出的采样、上下文、重复惩罚和停止参数。
    /// </summary>
    [Fact]
    public void ToolChatOptionsPreserveMergedOllamaGenerationOptions()
    {
        var request = CreateRequest([new OllamaChatMessage("user", "hello")]) with
        {
            Options = ParseJson(
                """{"temperature":0.25,"top_p":0.75,"top_k":17,"num_predict":123,"num_ctx":8192,"repeat_penalty":1.23,"repeat_last_n":77,"seed":42,"stop":["END","STOP"]}"""),
            Tools =
            [
                new OllamaChatTool(
                    "function",
                    new OllamaChatToolFunction(
                        "get_weather",
                        "Read weather",
                        ParseJson("""{"type":"object"}""")))
            ]
        };
        var merged = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            null,
            null,
            null,
            request.Options);

        var chatOptions = ToolCallingChatAdapter.CreateOllamaChatOptions(request, merged);
        var rawOptions = Assert.IsType<CompletionOptions>(chatOptions.RawRepresentationFactory!(null!));
        var resolved = LocalChatClient.ResolveCompletionOptions(chatOptions, rawOptions);

        Assert.Equal(0.25f, resolved.Temperature);
        Assert.Equal(0.75f, resolved.TopP);
        Assert.Equal(17, resolved.TopK);
        Assert.Equal(123, resolved.MaxOutputTokens);
        Assert.Equal(8192, resolved.ContextSize);
        Assert.Equal(1.23f, resolved.RepeatPenalty);
        Assert.Equal(77, resolved.PenaltyLastTokens);
        Assert.Equal(42, resolved.Seed);
        Assert.Equal(["END", "STOP"], resolved.StopSequences);
        Assert.NotEmpty(chatOptions.Tools!);
    }

    /// <summary>
    /// 创建使用固定模型和非流式默认值的 Ollama 测试请求。
    /// </summary>
    private static OllamaChatRequest CreateRequest(IReadOnlyList<OllamaChatMessage> messages)
        => new("ready", messages, false, null, null);

    /// <summary>
    /// 使用 Ollama source-generated 契约反序列化真实线协议输入。
    /// </summary>
    private static OllamaChatRequest DeserializeRequest(string json)
        => JsonSerializer.Deserialize(
            json,
            AppJsonSerializerContext.Default.OllamaChatRequest)!;

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
