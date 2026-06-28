using Microsoft.Extensions.AI;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class LocalChatClient : IChatClient
{
    private readonly LocalInferenceService inferenceService;
    private readonly LocalModelCatalog modelCatalog;
    private readonly ChatClientMetadata metadata = new(providerName: "tomur.local");

    public LocalChatClient(LocalInferenceService inferenceService, LocalModelCatalog modelCatalog)
    {
        this.inferenceService = inferenceService;
        this.modelCatalog = modelCatalog;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var messageList = messages.ToArray();
        var model = ResolveModel(options?.ModelId);
        var completionOptions = ResolveCompletionOptions(options);
        var result = inferenceService.Chat(
            model,
            BuildChatTurns(messageList, options),
            completionOptions,
            cancellationToken);

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Text))
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = model.Id,
            ResponseId = $"tomur-{Guid.NewGuid():N}",
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

    public void Dispose()
    {
    }

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

    private static IReadOnlyList<ChatTurn> BuildChatTurns(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var turns = new List<ChatTurn>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            turns.Add(new ChatTurn("system", options.Instructions.Trim()));
        }

        foreach (var message in messages)
        {
            var text = SerializeTextContent(message);
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

    private static CompletionOptions ResolveCompletionOptions(ChatOptions? options)
    {
        return CompletionOptions.Default with
        {
            Temperature = NormalizeFloat(options?.Temperature, CompletionOptions.Default.Temperature),
            TopP = NormalizeFloat(options?.TopP, CompletionOptions.Default.TopP),
            MaxOutputTokens = Math.Clamp(options?.MaxOutputTokens ?? CompletionOptions.Default.MaxOutputTokens, 1, 4096)
        };
    }

    private static string SerializeTextContent(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return message.Text;
        }

        if (message.Contents is null || message.Contents.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(text.Text);
                    break;
                case FunctionCallContent functionCall:
                    parts.Add($"tool_call:{functionCall.Name}");
                    break;
                case FunctionResultContent functionResult:
                    parts.Add($"tool_result:{functionResult.Result}");
                    break;
            }
        }

        return string.Join("\n", parts);
    }

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

    private static bool IsChatModel(LocalModelDescriptor model)
    {
        return model.Capabilities.Any(static capability =>
            string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));
    }

    private static float NormalizeFloat(double? value, float fallback)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return fallback;
        }

        return (float)value.Value;
    }
}
