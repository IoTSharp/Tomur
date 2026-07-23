using System.Text.Json;
using Microsoft.Extensions.AI;
using Tomur.Agents;
using Tomur.Api.OpenAI;
using Tomur.Api.Ollama;
using Tomur.Inference;

namespace Tomur.Api;

internal static class ToolCallingChatAdapter
{
    private const int MaxDeclaredTools = 64;

    /// <summary>
    /// 执行 OpenAI 工具调用请求；兼容端点只返回模型调用，不在服务端执行调用方函数。
    /// </summary>
    public static async Task<ToolAwareCompletion> CompleteOpenAiAsync(
        LocalChatClient chatClient,
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);

        var tools = CreateOpenAiTools(request.Tools);
        var toolMode = ResolveOpenAiToolMode(request.ToolChoice, tools);
        var messages = CreateOpenAiMessages(request.Messages ?? []);
        var response = await chatClient.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    ModelId = request.Model,
                    Temperature = NormalizeFloat(request.Temperature),
                    TopP = NormalizeFloat(request.TopP),
                    MaxOutputTokens = request.MaxTokens is > 0
                        ? Math.Clamp(request.MaxTokens.Value, 1, 4096)
                        : null,
                    Tools = tools.Cast<AITool>().ToArray(),
                    ToolMode = toolMode,
                    AllowMultipleToolCalls = request.ParallelToolCalls != false
                },
                cancellationToken)
            .ConfigureAwait(false);
        return ToCompletion(response);
    }

    /// <summary>
    /// 执行 Ollama 工具调用请求，并保留 assistant 调用与后续 tool result 的顺序关联。
    /// </summary>
    public static async Task<ToolAwareCompletion> CompleteOllamaAsync(
        LocalChatClient chatClient,
        OllamaChatRequest request,
        CompletionOptions completionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completionOptions);

        var chatOptions = CreateOllamaChatOptions(request, completionOptions);
        var response = await chatClient.GetResponseAsync(
                CreateOllamaMessages(request.Messages ?? []),
                chatOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return ToCompletion(response);
    }

    /// <summary>
    /// 将 Ollama 合并后的全部生成参数传入本地聊天客户端，工具路径与普通文本路径保持一致。
    /// </summary>
    internal static ChatOptions CreateOllamaChatOptions(
        OllamaChatRequest request,
        CompletionOptions completionOptions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completionOptions);

        var tools = CreateOllamaTools(request.Tools);
        return new ChatOptions
        {
            ModelId = request.Model,
            Temperature = completionOptions.Temperature,
            TopP = completionOptions.TopP,
            TopK = completionOptions.TopK,
            MaxOutputTokens = completionOptions.MaxOutputTokens,
            FrequencyPenalty = completionOptions.FrequencyPenalty,
            PresencePenalty = completionOptions.PresencePenalty,
            Seed = completionOptions.Seed,
            StopSequences = completionOptions.StopSequences.ToArray(),
            Tools = tools.Cast<AITool>().ToArray(),
            ToolMode = tools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            // MEAI 没有 num_ctx、repeat_penalty 和 repeat_last_n 强类型字段，原始选项保留完整本地语义。
            RawRepresentationFactory = _ => completionOptions
        };
    }

    /// <summary>
    /// 按最终注入本地模型的系统工具指令和消息文本估算 OpenAI 输入字符数。
    /// </summary>
    public static int EstimateOpenAiInputCharacters(OpenAiChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tools = CreateOpenAiTools(request.Tools);
        var toolMode = ResolveOpenAiToolMode(request.ToolChoice, tools);
        return EstimateInjectedCharacters(
            CreateOpenAiMessages(request.Messages ?? []),
            tools,
            toolMode,
            request.ParallelToolCalls != false);
    }

    /// <summary>
    /// 按最终注入本地模型的系统工具指令和消息文本估算 Ollama 输入字符数。
    /// </summary>
    public static int EstimateOllamaInputCharacters(OllamaChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tools = CreateOllamaTools(request.Tools);
        ChatToolMode toolMode = tools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto;
        return EstimateInjectedCharacters(
            CreateOllamaMessages(request.Messages ?? []),
            tools,
            toolMode,
            allowMultipleToolCalls: true);
    }

    /// <summary>
    /// 校验并转换 OpenAI function tool 声明。
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> CreateOpenAiTools(
        IReadOnlyList<OpenAiChatTool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        EnsureToolCount(tools.Count);
        var declarations = new List<AIFunctionDeclaration>(tools.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null ||
                !string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tool.Function?.Name))
            {
                throw InvalidRequest("Every OpenAI tools[] entry must be a named function tool.");
            }

            if (tool.Function.Strict == true)
            {
                throw InvalidRequest(
                    "OpenAI function strict=true is not supported by the local tool protocol.");
            }

            var name = tool.Function.Name.Trim();
            EnsureUniqueName(names, name);
            declarations.Add(new ProtocolToolDeclaration(
                name,
                tool.Function.Description,
                NormalizeSchema(tool.Function.Parameters)));
        }

        return declarations;
    }

    /// <summary>
    /// 校验并转换 Ollama function tool 声明。
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> CreateOllamaTools(
        IReadOnlyList<OllamaChatTool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        EnsureToolCount(tools.Count);
        var declarations = new List<AIFunctionDeclaration>(tools.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null ||
                !string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tool.Function?.Name))
            {
                throw InvalidRequest("Every Ollama tools[] entry must be a named function tool.");
            }

            var name = tool.Function.Name.Trim();
            EnsureUniqueName(names, name);
            declarations.Add(new ProtocolToolDeclaration(
                name,
                tool.Function.Description,
                NormalizeSchema(tool.Function.Parameters)));
        }

        return declarations;
    }

    /// <summary>
    /// 将 OpenAI 消息中的文本、assistant tool calls 和 tool result 映射为 MEAI 内容。
    /// </summary>
    internal static IReadOnlyList<ChatMessage> CreateOpenAiMessages(
        IReadOnlyList<OpenAiChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<ChatMessage>(messages.Count);
        var pendingCallIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is null)
            {
                throw InvalidRequest("OpenAI messages[] entries cannot be null.");
            }

            var role = NormalizeRole(message.Role);
            if (pendingCallIds.Count > 0 && role != ChatRole.Tool)
            {
                throw InvalidRequest("Every OpenAI assistant tool_call must be followed by its tool result before the next message.");
            }

            var contents = new List<AIContent>();
            var text = ExtractText(message.Content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                contents.Add(new TextContent(text));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                if (role != ChatRole.Assistant)
                {
                    throw InvalidRequest("OpenAI tool_calls are allowed only on assistant messages.");
                }

                foreach (var call in message.ToolCalls)
                {
                    if (call is null ||
                        string.IsNullOrWhiteSpace(call.Id) ||
                        !string.Equals(call.Type, "function", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(call.Function?.Name))
                    {
                        throw InvalidRequest("Assistant tool_calls require id, type=function and function.name.");
                    }

                    var callId = call.Id.Trim();
                    if (!seenCallIds.Add(callId))
                    {
                        throw InvalidRequest($"OpenAI tool_call id '{callId}' appears more than once.");
                    }

                    pendingCallIds.Add(callId);
                    contents.Add(new FunctionCallContent(
                        callId,
                        call.Function.Name.Trim(),
                        ModelToolProtocol.ToArguments(
                            ModelToolProtocol.ParseArguments(call.Function.Arguments))));
                }
            }

            if (role == ChatRole.Tool)
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    throw InvalidRequest("OpenAI tool messages require tool_call_id.");
                }

                var callId = message.ToolCallId.Trim();
                if (!pendingCallIds.Remove(callId))
                {
                    throw InvalidRequest($"OpenAI tool result references unknown or already completed call '{callId}'.");
                }

                contents.Clear();
                contents.Add(new FunctionResultContent(callId, text));
            }
            else if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw InvalidRequest("OpenAI tool_call_id is allowed only on tool messages.");
            }

            if (contents.Count > 0)
            {
                result.Add(new ChatMessage(role, contents) { AuthorName = message.Name });
            }
        }

        if (pendingCallIds.Count > 0)
        {
            throw InvalidRequest("OpenAI assistant tool_calls are missing one or more tool result messages.");
        }

        return result;
    }

    /// <summary>
    /// 将 Ollama 消息映射为 MEAI 内容，并按历史顺序为无 ID 调用生成稳定关联 ID。
    /// </summary>
    internal static IReadOnlyList<ChatMessage> CreateOllamaMessages(
        IReadOnlyList<OllamaChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<ChatMessage>(messages.Count);
        var pendingCalls = new List<(string Name, string Id)>();
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (message is null)
            {
                throw InvalidRequest("Ollama messages[] entries cannot be null.");
            }

            var role = NormalizeRole(message.Role);
            if (pendingCalls.Count > 0 && role != ChatRole.Tool)
            {
                throw InvalidRequest("Every Ollama assistant tool_call must be followed by its tool result before the next message.");
            }

            var contents = new List<AIContent>();
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                contents.Add(new TextContent(message.Content));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                if (role != ChatRole.Assistant)
                {
                    throw InvalidRequest("Ollama tool_calls are allowed only on assistant messages.");
                }

                for (var callIndex = 0; callIndex < message.ToolCalls.Count; callIndex++)
                {
                    var call = message.ToolCalls[callIndex];
                    if (call is null || string.IsNullOrWhiteSpace(call.Function?.Name))
                    {
                        throw InvalidRequest("Ollama assistant tool_calls require function.name.");
                    }

                    var name = call.Function.Name.Trim();
                    var id = string.IsNullOrWhiteSpace(call.Id)
                        ? $"call_ollama_{messageIndex}_{callIndex}"
                        : call.Id.Trim();
                    if (!seenCallIds.Add(id))
                    {
                        throw InvalidRequest($"Ollama tool call id '{id}' appears more than once.");
                    }

                    var arguments = call.Function.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? ModelToolProtocol.EmptyArguments()
                        : call.Function.Arguments;
                    contents.Add(new FunctionCallContent(
                        id,
                        name,
                        ModelToolProtocol.ToArguments(arguments)));
                    pendingCalls.Add((name, id));
                }
            }

            if (role == ChatRole.Tool)
            {
                var pendingIndex = ResolvePendingCallIndex(
                    pendingCalls,
                    message.ToolCallId,
                    message.ToolName);
                if (pendingIndex < 0)
                {
                    throw InvalidRequest("Ollama tool messages must follow a matching assistant tool_call.");
                }

                var pending = pendingCalls[pendingIndex];
                pendingCalls.RemoveAt(pendingIndex);
                contents.Clear();
                contents.Add(new FunctionResultContent(pending.Id, message.Content ?? string.Empty));
            }
            else if (!string.IsNullOrWhiteSpace(message.ToolName) ||
                     !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw InvalidRequest("Ollama tool_name and tool_call_id are allowed only on tool messages.");
            }

            if (contents.Count > 0)
            {
                result.Add(new ChatMessage(role, contents));
            }
        }

        if (pendingCalls.Count > 0)
        {
            throw InvalidRequest("Ollama assistant tool_calls are missing one or more tool result messages.");
        }

        return result;
    }

    /// <summary>
    /// 解析 OpenAI tool_choice 的字符串或命名函数对象。
    /// </summary>
    private static ChatToolMode ResolveOpenAiToolMode(
        JsonElement? toolChoice,
        IReadOnlyList<AIFunctionDeclaration> tools)
    {
        if (toolChoice is null || toolChoice.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return tools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto;
        }

        var value = toolChoice.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "none" => ChatToolMode.None,
                "auto" => tools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto,
                "required" when tools.Count > 0 => ChatToolMode.RequireAny,
                "required" => throw InvalidRequest("tool_choice=required requires at least one declared tool."),
                _ => throw InvalidRequest("tool_choice must be none, auto, required, or a named function object.")
            };
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "function", StringComparison.OrdinalIgnoreCase) ||
            !value.TryGetProperty("function", out var function) ||
            function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("name", out var nameValue) ||
            nameValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameValue.GetString()))
        {
            throw InvalidRequest("Named tool_choice must contain type=function and function.name.");
        }

        var name = nameValue.GetString()!.Trim();
        if (!tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
        {
            throw InvalidRequest($"tool_choice references undeclared function '{name}'.");
        }

        return ChatToolMode.RequireSpecific(name);
    }

    /// <summary>
    /// 从 MEAI ChatResponse 提取原始 usage、普通文本和结构化工具调用。
    /// </summary>
    private static ToolAwareCompletion ToCompletion(ChatResponse response)
    {
        var completion = response.RawRepresentation as CompletionResult
            ?? throw new InferenceException(
                "tool_calling_response_invalid",
                "The local chat client did not return CompletionResult metadata.",
                ["Inspect the local chat client response pipeline."]);
        var calls = response.Messages
            .SelectMany(static message => message.Contents ?? [])
            .OfType<FunctionCallContent>()
            .Select(static call => new ModelToolCall(
                call.CallId,
                call.Name,
                ModelToolProtocol.ToJsonElement(call.Arguments)))
            .ToArray();
        return new ToolAwareCompletion(completion, response.Text ?? string.Empty, calls);
    }

    /// <summary>
    /// 读取 OpenAI string 或文本 content-part 数组，其他对象保留原始 JSON。
    /// </summary>
    private static string ExtractText(JsonElement? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var value = content.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return value.GetRawText();
        }

        return string.Join(
            "\n",
            value.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object &&
                      item.TryGetProperty("text", out var text) &&
                      text.ValueKind == JsonValueKind.String
                        ? text.GetString()
                        : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>
    /// 将协议角色映射到 Microsoft.Extensions.AI 角色。
    /// </summary>
    private static ChatRole NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "developer" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };

    /// <summary>
    /// 优先按 Ollama tool_call_id 查找结果关联，缺少 ID 时兼容 tool_name 和声明顺序。
    /// </summary>
    private static int ResolvePendingCallIndex(
        IReadOnlyList<(string Name, string Id)> pendingCalls,
        string? toolCallId,
        string? toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            for (var index = 0; index < pendingCalls.Count; index++)
            {
                if (!string.Equals(pendingCalls[index].Id, toolCallId.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                return string.IsNullOrWhiteSpace(toolName) ||
                    string.Equals(pendingCalls[index].Name, toolName.Trim(), StringComparison.Ordinal)
                        ? index
                        : -1;
            }

            return -1;
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return pendingCalls.Count == 0 ? -1 : 0;
        }

        for (var index = 0; index < pendingCalls.Count; index++)
        {
            if (string.Equals(pendingCalls[index].Name, toolName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// 复用本地聊天协议的最终序列化形式统计字符，覆盖固定指令、JSON 转义和调用结果包装。
    /// </summary>
    private static int EstimateInjectedCharacters(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> tools,
        ChatToolMode? toolMode,
        bool allowMultipleToolCalls)
    {
        long count = ModelToolProtocol.BuildInstructions(tools, toolMode, allowMultipleToolCalls).Length;
        foreach (var message in messages)
        {
            count += LocalChatClient.SerializeMessageContent(message).Length;
            if (count >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)count;
    }

    /// <summary>
    /// 归一化 JSON Schema；省略 parameters 时使用空对象 schema。
    /// </summary>
    private static JsonElement NormalizeSchema(JsonElement? schema)
    {
        if (schema is { ValueKind: JsonValueKind.Object })
        {
            return schema.Value.Clone();
        }

        if (schema is not null && schema.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            throw InvalidRequest("function.parameters must be a JSON Schema object.");
        }

        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 限制单次请求的工具声明数量，防止 schema 无界占用上下文。
    /// </summary>
    private static void EnsureToolCount(int count)
    {
        if (count > MaxDeclaredTools)
        {
            throw InvalidRequest($"A chat request can declare at most {MaxDeclaredTools} tools.");
        }
    }

    /// <summary>
    /// 拒绝重复工具名，避免模型调用与客户端回灌产生歧义。
    /// </summary>
    private static void EnsureUniqueName(HashSet<string> names, string name)
    {
        if (!names.Add(name))
        {
            throw InvalidRequest($"Tool '{name}' is declared more than once.");
        }
    }

    /// <summary>
    /// 过滤无效采样浮点值。
    /// </summary>
    private static float? NormalizeFloat(double? value)
        => value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? null
            : (float)value.Value;

    /// <summary>
    /// 创建供兼容路由转换为协议 400 响应的请求错误。
    /// </summary>
    private static InferenceException InvalidRequest(string message)
        => new("invalid_request", message, ["Correct the tool declaration or tool message history and retry."]);
}
