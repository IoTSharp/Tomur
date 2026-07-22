using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Tomur.Agents;
using Tomur.Api;
using Tomur.Api.Ollama;
using Tomur.Api.OpenAI;
using Tomur.Inference;

namespace Tomur.Providers.M10.Tests;

public sealed class ToolCallingProtocolTests
{
    /// <summary>
    /// 验证模型协议能够解析单调用和同轮多个调用，并保留 ID、名称和对象参数。
    /// </summary>
    [Fact]
    public void ModelProtocolParsesSingleAndMultipleCalls()
    {
        var tools = CreateToolDeclarations();
        var single = ModelToolProtocol.ParseResponse(
            """
            <tool_call>{"id":"call_one","name":"weather.get","arguments":{"city":"Urumqi"}}</tool_call>
            """,
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);
        Assert.Single(single.ToolCalls);
        Assert.Equal("call_one", single.ToolCalls[0].Id);
        Assert.Equal("Urumqi", single.ToolCalls[0].Arguments.GetProperty("city").GetString());

        var multiple = ModelToolProtocol.ParseResponse(
            """
            <tool_calls>{"calls":[{"id":"call_a","name":"weather.get","arguments":{"city":"Mulei"}},{"id":"call_b","name":"clock.get","arguments":{"zone":"Asia/Shanghai"}}]}</tool_calls>
            """,
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);
        Assert.Equal(["call_a", "call_b"], multiple.ToolCalls.Select(static call => call.Id).ToArray());
        Assert.Equal(["weather.get", "clock.get"], multiple.ToolCalls.Select(static call => call.Name).ToArray());
    }

    /// <summary>
    /// 验证 required、未声明工具、非法参数和禁止多调用均返回稳定诊断代码。
    /// </summary>
    [Fact]
    public void ModelProtocolRejectsInvalidToolCalls()
    {
        var tools = CreateToolDeclarations();
        var required = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            "No tool is needed.",
            tools,
            ChatToolMode.RequireAny,
            allowMultipleToolCalls: true));
        Assert.Equal("tool_call_required", required.Code);

        var unknown = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_x","name":"unknown.run","arguments":{}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("tool_not_declared", unknown.Code);

        var invalidArguments = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_x","name":"weather.get","arguments":[]}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_arguments", invalidArguments.Code);

        var encodedInvalidArguments = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_encoded","name":"weather.get","arguments":"[]"}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal(invalidArguments.Code, encodedInvalidArguments.Code);

        var multiple = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_calls>{"calls":[{"id":"call_a","name":"weather.get","arguments":{}},{"id":"call_b","name":"clock.get","arguments":{}}]}</tool_calls>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: false));
        Assert.Equal("parallel_tool_calls_not_allowed", multiple.Code);

        var duplicateArguments = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_x","name":"weather.get","arguments":{"city":"Mulei","city":"Urumqi"}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_arguments", duplicateArguments.Code);
    }

    /// <summary>
    /// 验证命名工具选择只能调用指定函数，且同轮调用 ID 必须唯一。
    /// </summary>
    [Fact]
    public void ModelProtocolEnforcesNamedToolChoiceAndUniqueCallIds()
    {
        var tools = CreateToolDeclarations();
        var requiredWeather = ChatToolMode.RequireSpecific("weather.get");
        var matching = ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_weather","name":"weather.get","arguments":{"city":"Mulei"}}</tool_call>""",
            tools,
            requiredWeather,
            allowMultipleToolCalls: true);
        Assert.Equal("weather.get", Assert.Single(matching.ToolCalls).Name);

        var mismatch = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_clock","name":"clock.get","arguments":{"zone":"Asia/Shanghai"}}</tool_call>""",
            tools,
            requiredWeather,
            allowMultipleToolCalls: true));
        Assert.Equal("required_tool_mismatch", mismatch.Code);

        var duplicateId = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_calls>{"calls":[{"id":"call_same","name":"weather.get","arguments":{}},{"id":"call_same","name":"clock.get","arguments":{}}]}</tool_calls>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("duplicate_tool_call_id", duplicateId.Code);
    }

    /// <summary>
    /// 验证 envelope、call 和 function 的重复语义属性会被拒绝，不依赖 JSON last-wins 行为。
    /// </summary>
    [Fact]
    public void ModelProtocolRejectsDuplicateEnvelopeSemanticProperties()
    {
        var tools = CreateToolDeclarations();
        var duplicateRawRoot = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """{"calls":[],"calls":[]}""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", duplicateRawRoot.Code);

        var duplicateTaggedRoot = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_calls>{"calls":[],"tool_calls":[]}</tool_calls>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", duplicateTaggedRoot.Code);

        var duplicateCallName = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_name","name":"weather.get","name":"clock.get","arguments":{}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", duplicateCallName.Code);

        var duplicateFunctionArguments = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_function","function":{"name":"weather.get","arguments":{},"arguments":{}}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", duplicateFunctionArguments.Code);

        var mixedCallShapes = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_mixed","name":"weather.get","function":{"name":"clock.get","arguments":{}}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", mixedCallShapes.Code);
    }

    /// <summary>
    /// 验证显式提供的 function 必须是对象，不能用非法值回退到外层 name/arguments。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("\"weather.get\"")]
    [InlineData("[]")]
    [InlineData("true")]
    public void ModelProtocolRejectsNonObjectFunctionWrapper(string functionJson)
    {
        var tools = CreateToolDeclarations();
        var output = "<tool_call>{\"id\":\"call_invalid_function\",\"name\":\"weather.get\"," +
            "\"arguments\":{},\"function\":" + functionJson + "}</tool_call>";
        var exception = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            output,
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", exception.Code);
    }

    /// <summary>
    /// 验证显式 ID 只能是字符串，null 和其他 JSON 类型不能伪装成缺失 ID。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void ModelProtocolRejectsNonStringCallId(string idJson)
    {
        var tools = CreateToolDeclarations();
        var output = "<tool_call>{\"id\":" + idJson +
            ",\"name\":\"weather.get\",\"arguments\":{}}</tool_call>";
        var exception = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            output,
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", exception.Code);
    }

    /// <summary>
    /// 验证缺失或空白字符串 ID 仍生成格式稳定且互不冲突的本地调用 ID。
    /// </summary>
    [Fact]
    public void ModelProtocolSynthesizesMissingOrBlankCallIds()
    {
        var tools = CreateToolDeclarations();
        var missing = ModelToolProtocol.ParseResponse(
            """<tool_call>{"name":"weather.get","arguments":{}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);
        var blank = ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"   ","name":"weather.get","arguments":{}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);

        var missingId = Assert.Single(missing.ToolCalls).Id;
        var blankId = Assert.Single(blank.ToolCalls).Id;
        Assert.StartsWith("call_", missingId);
        Assert.StartsWith("call_", blankId);
        Assert.Equal(37, missingId.Length);
        Assert.Equal(37, blankId.Length);
        Assert.NotEqual(missingId, blankId);
    }

    /// <summary>
    /// 验证 required、type 和 additionalProperties 会约束普通工具调用参数。
    /// </summary>
    [Fact]
    public void ModelProtocolValidatesBasicToolSchemaConstraints()
    {
        var tools = CreateSchemaConstrainedTools();
        var valid = ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_valid","name":"weather.get","arguments":{"city":"Mulei","days":2}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);
        Assert.Equal(2, Assert.Single(valid.ToolCalls).Arguments.GetProperty("days").GetInt32());

        var missingRequired = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_missing","name":"weather.get","arguments":{"days":2}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_arguments", missingRequired.Code);

        var wrongType = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_type","name":"weather.get","arguments":{"city":42}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_arguments", wrongType.Code);

        var additionalProperty = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """<tool_call>{"id":"call_extra","name":"weather.get","arguments":{"city":"Mulei","unit":"celsius"}}</tool_call>""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_arguments", additionalProperty.Code);
    }

    /// <summary>
    /// 验证基础 schema 关键字形状错误会在声明阶段返回稳定诊断，而不是泄漏 JsonElement 异常。
    /// </summary>
    [Fact]
    public void ToolDeclarationRejectsMalformedBasicSchemaKeywords()
    {
        var invalidRequired = Assert.Throws<InferenceException>(() => new ProtocolToolDeclaration(
            "weather.required",
            "Invalid required",
            ParseJson("""{"type":"object","required":"city"}""")));
        Assert.Equal("invalid_tool_schema", invalidRequired.Code);

        var invalidType = Assert.Throws<InferenceException>(() => new ProtocolToolDeclaration(
            "weather.type",
            "Invalid type",
            ParseJson("""{"type":"record"}""")));
        Assert.Equal("invalid_tool_schema", invalidType.Code);

        var invalidAdditionalProperties = Assert.Throws<InferenceException>(() => new ProtocolToolDeclaration(
            "weather.additional",
            "Invalid additional properties",
            ParseJson("""{"type":"object","additionalProperties":"no"}""")));
        Assert.Equal("invalid_tool_schema", invalidAdditionalProperties.Code);
    }

    /// <summary>
    /// 验证明显损坏的原始工具 envelope 会报错，普通 JSON 回答仍保留为文本。
    /// </summary>
    [Fact]
    public void ModelProtocolDistinguishesMalformedRawEnvelopeFromJsonAnswer()
    {
        var tools = CreateToolDeclarations();
        var malformedEnvelope = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """{"calls":[{"id":"call_x","name":"weather.get","arguments":{}}],}""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", malformedEnvelope.Code);

        var missingClosingBrace = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """{"calls":[{"id":"call_open","name":"weather.get","arguments":{}}]""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", missingClosingBrace.Code);

        var missingToolCallsClosingBrace = Assert.Throws<InferenceException>(() => ModelToolProtocol.ParseResponse(
            """{"tool_calls":[{"id":"call_open_alias","name":"weather.get","arguments":{}}]""",
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true));
        Assert.Equal("invalid_tool_call", missingToolCallsClosingBrace.Code);

        const string ordinaryJson = """{"message":"the word calls",}""";
        var ordinaryResponse = ModelToolProtocol.ParseResponse(
            ordinaryJson,
            tools,
            ChatToolMode.Auto,
            allowMultipleToolCalls: true);
        Assert.Empty(ordinaryResponse.ToolCalls);
        Assert.Equal(ordinaryJson, ordinaryResponse.Text);
    }

    /// <summary>
    /// 验证空参数历史写成对象，并把带 ID 的结构化工具结果完整回灌到模型协议。
    /// </summary>
    [Fact]
    public void ModelProtocolSerializesNullArgumentsAndStructuredToolResult()
    {
        var callsPayload = ModelToolProtocol.SerializeCalls(
        [
            new FunctionCallContent("call_empty", "clock.get", null!)
        ]);
        using var callsDocument = ParseTaggedJson(callsPayload, "<tool_calls>", "</tool_calls>");
        var serializedCall = callsDocument.RootElement.GetProperty("calls")[0];
        Assert.Equal("call_empty", serializedCall.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, serializedCall.GetProperty("arguments").ValueKind);
        Assert.Empty(serializedCall.GetProperty("arguments").EnumerateObject());

        var resultPayload = ModelToolProtocol.SerializeResult(new FunctionResultContent(
            "call_empty",
            ParseJson("""{"forecast":"sunny","temperature":18}""")));
        using var resultDocument = ParseTaggedJson(resultPayload, "<tool_response>", "</tool_response>");
        Assert.Equal("call_empty", resultDocument.RootElement.GetProperty("id").GetString());
        var result = resultDocument.RootElement.GetProperty("result");
        Assert.Equal("sunny", result.GetProperty("forecast").GetString());
        Assert.Equal(18, result.GetProperty("temperature").GetInt32());
    }

    /// <summary>
    /// 验证两种兼容协议都把工具结果关联回原调用，而不是降级为无 ID 文本。
    /// </summary>
    [Fact]
    public void ToolAdaptersPreserveResultCallIds()
    {
        var openAiMessages = ToolCallingChatAdapter.CreateOpenAiMessages(
        [
            new OpenAiChatMessage("assistant", null)
            {
                ToolCalls =
                [
                    new OpenAiChatToolCall(
                        "call_openai",
                        "function",
                        new OpenAiChatToolCallFunction("weather.get", "{\"city\":\"Mulei\"}"))
                ]
            },
            new OpenAiChatMessage("tool", ParseJson("\"sunny\""))
            {
                ToolCallId = "call_openai"
            }
        ]);
        Assert.Equal(
            "call_openai",
            openAiMessages[0].Contents.OfType<FunctionCallContent>().Single().CallId);
        var openAiResult = openAiMessages[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_openai", openAiResult.CallId);
        Assert.Equal("sunny", Assert.IsType<string>(openAiResult.Result));

        var ollamaMessages = ToolCallingChatAdapter.CreateOllamaMessages(
        [
            new OllamaChatMessage("assistant", string.Empty)
            {
                ToolCalls =
                [
                    new OllamaChatToolCall(
                        new OllamaChatToolCallFunction("weather.get", ParseJson("{\"city\":\"Mulei\"}")))
                ]
            },
            new OllamaChatMessage("tool", "sunny")
            {
                ToolName = "weather.get"
            }
        ]);
        var ollamaCallId = ollamaMessages[0].Contents.OfType<FunctionCallContent>().Single().CallId;
        var ollamaResult = ollamaMessages[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal(ollamaCallId, ollamaResult.CallId);
        Assert.Equal("sunny", Assert.IsType<string>(ollamaResult.Result));
    }

    /// <summary>
    /// 验证 Ollama 并行同名调用优先按 tool_call_id 关联，结果顺序变化时不会串联。
    /// </summary>
    [Fact]
    public void OllamaAdapterCorrelatesParallelSameNameCallsById()
    {
        var messages = ToolCallingChatAdapter.CreateOllamaMessages(
        [
            new OllamaChatMessage("assistant", string.Empty)
            {
                ToolCalls =
                [
                    new OllamaChatToolCall(
                        new OllamaChatToolCallFunction("weather.get", ParseJson("{\"city\":\"Mulei\"}")))
                    {
                        Id = "call_mulei"
                    },
                    new OllamaChatToolCall(
                        new OllamaChatToolCallFunction("weather.get", ParseJson("{\"city\":\"Urumqi\"}")))
                    {
                        Id = "call_urumqi"
                    }
                ]
            },
            new OllamaChatMessage("tool", "18 C")
            {
                ToolName = "weather.get",
                ToolCallId = "call_urumqi"
            },
            new OllamaChatMessage("tool", "16 C")
            {
                ToolName = "weather.get",
                ToolCallId = "call_mulei"
            }
        ]);

        Assert.Equal(
            ["call_mulei", "call_urumqi"],
            messages[0].Contents.OfType<FunctionCallContent>().Select(static call => call.CallId).ToArray());
        Assert.Equal(
            "call_urumqi",
            messages[1].Contents.OfType<FunctionResultContent>().Single().CallId);
        Assert.Equal(
            "call_mulei",
            messages[2].Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    /// <summary>
    /// 验证空工具和无工具选项保留文本路径，required 与 strict 仍在推理前返回稳定请求错误。
    /// </summary>
    [Fact]
    public void ToolRouteSelectionKeepsEmptyToolsStreamingAndValidatesUnsupportedChoices()
    {
        var messages = new[] { new OpenAiChatMessage("user", ParseJson("\"hello\"")) };
        var emptyTools = new OpenAiChatCompletionRequest("ready", messages, true, null, null, null)
        {
            Tools = []
        };
        Assert.False(ApiRouteExtensions.UsesOpenAiToolProtocol(emptyTools));

        var parallelOnly = emptyTools with
        {
            Tools = null,
            ParallelToolCalls = true
        };
        Assert.False(ApiRouteExtensions.UsesOpenAiToolProtocol(parallelOnly));

        var autoWithoutTools = emptyTools with
        {
            Tools = null,
            ToolChoice = ParseJson("\"auto\"")
        };
        Assert.False(ApiRouteExtensions.UsesOpenAiToolProtocol(autoWithoutTools));
        _ = ToolCallingChatAdapter.EstimateOpenAiInputCharacters(autoWithoutTools);

        var requiredWithoutTools = autoWithoutTools with
        {
            ToolChoice = ParseJson("\"required\"")
        };
        var required = Assert.Throws<InferenceException>(() =>
            ToolCallingChatAdapter.EstimateOpenAiInputCharacters(requiredWithoutTools));
        Assert.Equal("invalid_request", required.Code);

        var strict = emptyTools with
        {
            Tools =
            [
                new OpenAiChatTool(
                    "function",
                    new OpenAiChatToolFunction("weather.get", "Read weather", ParseJson("{\"type\":\"object\"}"))
                    {
                        Strict = true
                    })
            ]
        };
        var unsupportedStrict = Assert.Throws<InferenceException>(() =>
            ToolCallingChatAdapter.EstimateOpenAiInputCharacters(strict));
        Assert.Equal("invalid_request", unsupportedStrict.Code);

        var disabled = strict with
        {
            Tools =
            [
                new OpenAiChatTool(
                    "function",
                    new OpenAiChatToolFunction("weather.get", "Read weather", ParseJson("{\"type\":\"object\"}")))
            ],
            ToolChoice = ParseJson("\"none\"")
        };
        Assert.False(ApiRouteExtensions.UsesOpenAiToolProtocol(disabled));

        var malformedNamedChoice = disabled with
        {
            ToolChoice = ParseJson("{\"type\":{},\"function\":{\"name\":\"weather.get\"}}")
        };
        var malformed = Assert.Throws<InferenceException>(() =>
            ToolCallingChatAdapter.EstimateOpenAiInputCharacters(malformedNamedChoice));
        Assert.Equal("invalid_request", malformed.Code);

        var ollamaEmptyTools = new OllamaChatRequest(
            "ready",
            [new OllamaChatMessage("user", "hello")],
            true,
            null,
            null)
        {
            Tools = []
        };
        Assert.False(ApiRouteExtensions.UsesOllamaToolProtocol(ollamaEmptyTools));
    }

    /// <summary>
    /// 验证 developer 指令保持 system 级，并且上下文估算包含最终工具指令和 JSON 包装开销。
    /// </summary>
    [Fact]
    public void OpenAiAdapterPreservesDeveloperPriorityAndCountsInjectedPrompt()
    {
        var mapped = ToolCallingChatAdapter.CreateOpenAiMessages(
        [
            new OpenAiChatMessage("developer", ParseJson("\"Follow policy.\""))
        ]);
        Assert.Equal(ChatRole.System, Assert.Single(mapped).Role);

        const string description = "Read \\\"quoted\\\" weather values.";
        var request = new OpenAiChatCompletionRequest(
            "ready",
            [new OpenAiChatMessage("user", ParseJson("\"hello\""))],
            false,
            null,
            null,
            null)
        {
            Tools =
            [
                new OpenAiChatTool(
                    "function",
                    new OpenAiChatToolFunction("weather.get", description, ParseJson("{\"type\":\"object\"}")))
            ]
        };

        var rawCharacters = "hello".Length + "weather.get".Length + description.Length;
        Assert.True(ToolCallingChatAdapter.EstimateOpenAiInputCharacters(request) > rawCharacters);
    }

    /// <summary>
    /// 验证 OpenAI 非流式显式 content:null，SSE 输出可聚合的 tool_calls 和终止原因。
    /// </summary>
    [Fact]
    public async Task OpenAiToolWritersEmitNonStreamingAndSseContracts()
    {
        var single = CreateToolCompletion(
            new ModelToolCall("call_one", "weather.get", ParseJson("{\"city\":\"Mulei\"}")));
        var nonStreaming = CreateContext(out var nonStreamingBody);
        await using (nonStreamingBody)
        {
            await ApiRouteExtensions.WriteOpenAiChatCompletionSuccessAsync(
                nonStreaming,
                "ready",
                single,
                stream: false);
            using var document = JsonDocument.Parse(nonStreamingBody.ToArray());
            var choice = document.RootElement.GetProperty("choices")[0];
            Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.Equal(JsonValueKind.Null, choice.GetProperty("message").GetProperty("content").ValueKind);
            var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
            Assert.False(call.TryGetProperty("index", out _));
            Assert.Equal("weather.get", call.GetProperty("function").GetProperty("name").GetString());
        }

        var multiple = CreateToolCompletion(
            new ModelToolCall("call_a", "weather.get", ParseJson("{\"city\":\"Mulei\"}")),
            new ModelToolCall("call_b", "clock.get", ParseJson("{\"zone\":\"Asia/Shanghai\"}")));
        var streaming = CreateContext(out var streamingBody);
        await using (streamingBody)
        {
            await ApiRouteExtensions.WriteOpenAiChatCompletionSuccessAsync(
                streaming,
                "ready",
                multiple,
                stream: true);
            var events = ReadSseData(streamingBody);
            Assert.Equal(3, events.Count);
            Assert.Equal("[DONE]", events[^1]);
            using var callChunk = JsonDocument.Parse(events[0]);
            using var terminal = JsonDocument.Parse(events[1]);
            var calls = callChunk.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls");
            Assert.Equal(2, calls.GetArrayLength());
            Assert.Equal(0, calls[0].GetProperty("index").GetInt32());
            Assert.Equal(1, calls[1].GetProperty("index").GetInt32());
            Assert.Equal("tool_calls", terminal.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        }
    }

    /// <summary>
    /// 验证 Ollama 流先输出对象参数调用帧，再输出带 done_reason 和 usage 的终帧。
    /// </summary>
    [Fact]
    public async Task OllamaToolWriterEmitsCallAndTerminalNdjsonFrames()
    {
        var completion = CreateToolCompletion(
            new ModelToolCall("call_one", "weather.get", ParseJson("{\"city\":\"Mulei\"}")));
        var context = CreateContext(out var body);
        await using (body)
        {
            await ApiRouteExtensions.WriteOllamaChatSuccessAsync(
                context,
                "ready",
                completion,
                stream: true);
            var lines = Encoding.UTF8.GetString(body.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using var callFrame = JsonDocument.Parse(lines[0]);
            using var terminal = JsonDocument.Parse(lines[1]);
            Assert.False(callFrame.RootElement.GetProperty("done").GetBoolean());
            var toolCall = callFrame.RootElement.GetProperty("message").GetProperty("tool_calls")[0];
            Assert.Equal("call_one", toolCall.GetProperty("id").GetString());
            Assert.False(toolCall.TryGetProperty("type", out _));
            var function = toolCall.GetProperty("function");
            Assert.Equal(0, function.GetProperty("index").GetInt32());
            Assert.Equal("weather.get", function.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Object, function.GetProperty("arguments").ValueKind);
            Assert.True(terminal.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal("stop", terminal.RootElement.GetProperty("done_reason").GetString());
            Assert.Equal(3, terminal.RootElement.GetProperty("prompt_eval_count").GetInt32());
            Assert.Equal(2, terminal.RootElement.GetProperty("eval_count").GetInt32());
        }
    }

    /// <summary>
    /// 验证普通 Ollama chat 非流式成功响应也包含标准 stop 终止原因。
    /// </summary>
    [Fact]
    public async Task OllamaTextWriterEmitsDoneReason()
    {
        var context = CreateContext(out var body);
        await using (body)
        {
            await ApiRouteExtensions.WriteOllamaChatSuccessAsync(
                context,
                "ready",
                new CompletionResult(
                    "hello",
                    new TokenUsage(2, 1, 3),
                    TimeSpan.FromMilliseconds(10),
                    []),
                stream: false);
            using var document = JsonDocument.Parse(body.ToArray());
            Assert.True(document.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal("stop", document.RootElement.GetProperty("done_reason").GetString());
        }
    }

    /// <summary>
    /// 创建测试用工具声明，schema 保持为对象且不依赖反射序列化。
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> CreateToolDeclarations()
        =>
        [
            new ProtocolToolDeclaration("weather.get", "Read weather", ParseJson("{\"type\":\"object\"}")),
            new ProtocolToolDeclaration("clock.get", "Read time", ParseJson("{\"type\":\"object\"}"))
        ];

    /// <summary>
    /// 创建同时约束必填字段、字段类型和额外属性的测试工具声明。
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> CreateSchemaConstrainedTools()
        =>
        [
            new ProtocolToolDeclaration(
                "weather.get",
                "Read weather",
                ParseJson(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "city": { "type": "string" },
                        "days": { "type": "integer" }
                      },
                      "required": ["city"],
                      "additionalProperties": false
                    }
                    """))
        ];

    /// <summary>
    /// 创建固定 usage 的工具响应，便于同时验证协议字段和终帧统计。
    /// </summary>
    private static ToolAwareCompletion CreateToolCompletion(params ModelToolCall[] calls)
        => new(
            new CompletionResult(
                string.Empty,
                new TokenUsage(3, 2, 5),
                TimeSpan.FromMilliseconds(25),
                []),
            string.Empty,
            calls);

    /// <summary>
    /// 创建使用内存响应体的 HTTP 上下文。
    /// </summary>
    private static DefaultHttpContext CreateContext(out MemoryStream body)
    {
        var context = new DefaultHttpContext();
        body = new MemoryStream();
        context.Response.Body = body;
        return context;
    }

    /// <summary>
    /// 克隆 JSON 根元素，确保文档释放后参数仍可使用。
    /// </summary>
    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 去除协议标签并解析其中的 JSON，便于直接断言回灌负载。
    /// </summary>
    private static JsonDocument ParseTaggedJson(string payload, string startTag, string endTag)
        => JsonDocument.Parse(payload[startTag.Length..^endTag.Length]);

    /// <summary>
    /// 从 SSE 响应提取 data 行，保留终止标记用于顺序断言。
    /// </summary>
    private static IReadOnlyList<string> ReadSseData(MemoryStream body)
        => Encoding.UTF8.GetString(body.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(static block => block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static line => line[6..])
            .ToArray();
}
