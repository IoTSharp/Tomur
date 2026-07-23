using Microsoft.Extensions.AI;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class LocalChatClient : IChatClient
{
    private readonly LocalInferenceService inferenceService;
    private readonly LocalModelCatalog modelCatalog;
    private readonly ChatClientMetadata metadata = new(providerName: "tomur.local");

    /// <summary>
    /// 创建映射到 Tomur 本地模型目录和推理会话的聊天客户端。
    /// </summary>
    public LocalChatClient(LocalInferenceService inferenceService, LocalModelCatalog modelCatalog)
    {
        this.inferenceService = inferenceService;
        this.modelCatalog = modelCatalog;
    }

    /// <summary>
    /// 执行一次本地聊天，并把模型工具调用转换为 Microsoft.Extensions.AI 结构化内容。
    /// </summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var messageList = messages.ToArray();
        var model = ResolveModel(options?.ModelId);
        var rawCompletionOptions = options?.RawRepresentationFactory?.Invoke(this) as CompletionOptions;
        var completionOptions = ResolveCompletionOptions(options, rawCompletionOptions);
        var toolDeclarations = ResolveToolDeclarations(options);
        var result = inferenceService.Chat(
            model,
            BuildChatTurns(messageList, options, toolDeclarations),
            completionOptions,
            cancellationToken);

        var allowMultipleToolCalls = options?.AllowMultipleToolCalls != false;
        var modelResponse = toolDeclarations.Count == 0 || options?.ToolMode == ChatToolMode.None
            ? new ModelToolResponse(result.Text, [])
            : ModelToolProtocol.ParseResponse(
                result.Text,
                toolDeclarations,
                options?.ToolMode,
                allowMultipleToolCalls);
        var responseMessage = CreateResponseMessage(modelResponse);
        var response = new ChatResponse(responseMessage)
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = model.Id,
            ResponseId = $"tomur-{Guid.NewGuid():N}",
            FinishReason = modelResponse.ToolCalls.Count > 0
                ? ChatFinishReason.ToolCalls
                : ChatFinishReason.Stop,
            RawRepresentation = result
        };
        response.Usage = new UsageDetails
        {
            InputTokenCount = result.Usage.PromptTokens,
            OutputTokenCount = result.Usage.CompletionTokens,
            TotalTokenCount = result.Usage.TotalTokens
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// 以统一更新对象返回本地响应；工具调用同样保留结构化 delta。
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <summary>
    /// 暴露 ChatClientMetadata 和当前客户端实例供框架发现。
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// 本客户端不持有独立 native 资源，生命周期由推理会话管理器负责。
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// 解析请求模型，缺失时选择首个本地聊天模型。
    /// </summary>
    private LocalModelDescriptor ResolveModel(string? requestedModel)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            var requested = modelCatalog.Find(requestedModel);
            if (requested is null)
            {
                throw new InferenceException(
                    "model_not_downloaded",
                    $"The requested model '{requestedModel}' is not available in the local models directory.",
                    [
                        "Run tomur pull recommended to install the default local model package set.",
                        "Use /v1/models or /api/tags to inspect models currently visible to Tomur."
                    ]);
            }

            return requested;
        }

        return modelCatalog.ListModels().FirstOrDefault(IsChatModel)
            ?? throw new InferenceException(
                "model_not_downloaded",
                "No local chat model is available for the Microsoft.Extensions.AI chat client.",
                [
                    "Run tomur pull recommended to install the default local assistant model.",
                    "Use /v1/models or /api/tags to inspect models currently visible to Tomur."
                ]);
    }

    /// <summary>
    /// 将聊天内容、工具历史和工具声明转换为 provider-neutral 的本地消息序列。
    /// </summary>
    private static IReadOnlyList<ChatTurn> BuildChatTurns(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<AIFunctionDeclaration> toolDeclarations)
    {
        var turns = new List<ChatTurn>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            turns.Add(new ChatTurn("system", options.Instructions.Trim()));
        }

        var toolInstructions = ModelToolProtocol.BuildInstructions(
            toolDeclarations,
            options?.ToolMode,
            options?.AllowMultipleToolCalls != false);
        if (!string.IsNullOrWhiteSpace(toolInstructions))
        {
            turns.Add(new ChatTurn("system", toolInstructions));
        }

        foreach (var message in messages)
        {
            var text = SerializeMessageContent(message);
            if (!string.IsNullOrWhiteSpace(text))
            {
                turns.Add(new ChatTurn(NormalizeRole(message.Role), text));
            }
        }

        if (turns.Count == 0)
        {
            throw new InferenceException(
                "invalid_request",
                "At least one text chat message is required.",
                ["Provide text input before invoking the local chat client."]);
        }

        return turns;
    }

    /// <summary>
    /// 将 ChatOptions 与实现专用原始选项合并，保留 Ollama 工具路径的完整本地生成参数。
    /// </summary>
    internal static CompletionOptions ResolveCompletionOptions(
        ChatOptions? options,
        CompletionOptions? rawCompletionOptions = null)
    {
        var defaults = rawCompletionOptions ?? CompletionOptions.Default;
        return defaults with
        {
            Temperature = NormalizeFloat(options?.Temperature, defaults.Temperature),
            TopP = NormalizeFloat(options?.TopP, defaults.TopP),
            TopK = options?.TopK ?? defaults.TopK,
            MaxOutputTokens = Math.Clamp(options?.MaxOutputTokens ?? defaults.MaxOutputTokens, 1, 4096),
            FrequencyPenalty = NormalizeFloat(options?.FrequencyPenalty, defaults.FrequencyPenalty),
            PresencePenalty = NormalizeFloat(options?.PresencePenalty, defaults.PresencePenalty),
            Seed = NormalizeSeed(options?.Seed, defaults.Seed),
            StopSequences = options?.StopSequences?.ToArray() ?? defaults.StopSequences
        };
    }

    /// <summary>
    /// 序列化文本、函数调用和函数结果，保留调用 ID 与结构化参数。
    /// </summary>
    internal static string SerializeMessageContent(ChatMessage message)
    {
        if (message.Contents is null || message.Contents.Count == 0)
        {
            return message.Text ?? string.Empty;
        }

        var parts = new List<string>();
        var functionCalls = new List<FunctionCallContent>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(text.Text);
                    break;
                case FunctionCallContent functionCall:
                    functionCalls.Add(functionCall);
                    break;
                case FunctionResultContent functionResult:
                    parts.Add(ModelToolProtocol.SerializeResult(functionResult));
                    break;
            }
        }

        if (functionCalls.Count > 0)
        {
            parts.Add(ModelToolProtocol.SerializeCalls(functionCalls));
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// 将框架角色归一化为本地 provider 使用的角色名称。
    /// </summary>
    private static string NormalizeRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return "system";
        }

        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        if (role == ChatRole.Tool)
        {
            return "tool";
        }

        return "user";
    }

    /// <summary>
    /// 判断本地模型是否具备文本聊天或补全能力。
    /// </summary>
    private static bool IsChatModel(LocalModelDescriptor model)
    {
        return model.Capabilities.Any(static capability =>
            string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 过滤无效浮点采样值并保留默认配置。
    /// </summary>
    private static float NormalizeFloat(double? value, float fallback)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return fallback;
        }

        return (float)value.Value;
    }

    /// <summary>
    /// 将 MEAI 的 long 随机种子安全收敛到本地 CompletionOptions 使用的 int 范围。
    /// </summary>
    private static int NormalizeSeed(long? value, int fallback)
        => value is null
            ? fallback
            : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);

    /// <summary>
    /// 仅保留能够向模型提供函数名称和 JSON Schema 的工具声明。
    /// </summary>
    private static IReadOnlyList<AIFunctionDeclaration> ResolveToolDeclarations(ChatOptions? options)
        => options?.Tools?
            .OfType<AIFunctionDeclaration>()
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray() ?? [];

    /// <summary>
    /// 构造同时支持普通文本与一个或多个函数调用的 assistant 消息。
    /// </summary>
    private static ChatMessage CreateResponseMessage(ModelToolResponse response)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            contents.Add(new TextContent(response.Text));
        }

        contents.AddRange(response.ToolCalls.Select(static call =>
            new FunctionCallContent(
                call.Id,
                call.Name,
                ModelToolProtocol.ToArguments(call.Arguments))));
        return new ChatMessage(ChatRole.Assistant, contents);
    }
}
