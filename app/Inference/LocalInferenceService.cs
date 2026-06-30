using System.Text.Json;
using Tomur.Runtime;

namespace Tomur.Inference;

public sealed class LocalInferenceService
{
    private readonly SessionManager sessionManager;
    private readonly LlamaPromptBuilder promptBuilder = new();

    public LocalInferenceService(SessionManager sessionManager)
    {
        this.sessionManager = sessionManager;
    }

    public CompletionResult Complete(
        LocalModelDescriptor model,
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        EnsureTextModel(model);
        var preparedPrompt = promptBuilder.PrepareCompletionPrompt(prompt, options.ContextSize);
        return sessionManager.Generate(model, preparedPrompt, options, cancellationToken, onToken);
    }

    public CompletionResult Chat(
        LocalModelDescriptor model,
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        EnsureTextModel(model);
        var prompt = promptBuilder.BuildChatPrompt(messages, model.Name);
        var chatOptions = options with
        {
            StopSequences = promptBuilder.ResolveChatStops(model.Name, options.StopSequences)
        };

        var preparedPrompt = promptBuilder.PrepareCompletionPrompt(prompt, chatOptions.ContextSize);
        return sessionManager.Generate(model, preparedPrompt, chatOptions, cancellationToken, onToken);
    }

    public EmbeddingResult Embed(
        LocalModelDescriptor model,
        string input,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        EnsureEmbeddingModel(model);
        var preparedInput = promptBuilder.PrepareCompletionPrompt(input, options.ContextSize);
        return sessionManager.Embed(model, preparedInput, options, cancellationToken);
    }

    public SessionSnapshot GetSnapshot()
        => sessionManager.GetSnapshot();

    public void Unload()
        => sessionManager.Unload();

    public static CompletionOptions MergeOptions(
        CompletionOptions defaults,
        double? temperature,
        double? topP,
        int? maxTokens,
        JsonElement? options = null,
        IReadOnlyList<string>? stopSequences = null)
    {
        var merged = defaults with
        {
            Temperature = NormalizeFloat(temperature, defaults.Temperature),
            TopP = NormalizeFloat(topP, defaults.TopP),
            MaxOutputTokens = Math.Clamp(maxTokens ?? defaults.MaxOutputTokens, 1, 4096),
            StopSequences = stopSequences ?? defaults.StopSequences
        };

        if (options is null || options.Value.ValueKind != JsonValueKind.Object)
        {
            return merged;
        }

        var value = options.Value;
        merged = merged with
        {
            Temperature = TryGetFloat(value, "temperature", merged.Temperature),
            TopP = TryGetFloat(value, "top_p", merged.TopP),
            TopK = TryGetInt(value, "top_k", merged.TopK),
            MaxOutputTokens = Math.Clamp(TryGetInt(value, "num_predict", merged.MaxOutputTokens), 1, 4096),
            ContextSize = Math.Clamp(TryGetInt(value, "num_ctx", merged.ContextSize), 512, 131072),
            RepeatPenalty = TryGetFloat(value, "repeat_penalty", merged.RepeatPenalty),
            PenaltyLastTokens = TryGetInt(value, "repeat_last_n", merged.PenaltyLastTokens),
            Seed = TryGetInt(value, "seed", merged.Seed)
        };

        if (TryGetStopSequences(value, out var stops))
        {
            merged = merged with { StopSequences = stops };
        }

        return merged;
    }

    private static void EnsureTextModel(LocalModelDescriptor model)
    {
        if (model.Capabilities.Any(static capability =>
                string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InferenceException(
            "model_capability_mismatch",
            "The requested model is not a text generation model.",
            ["Use /v1/models or /api/tags to select a model with chat or completion capability."]);
    }

    private static void EnsureEmbeddingModel(LocalModelDescriptor model)
    {
        if (model.Capabilities.Any(static capability => string.Equals(capability, "embedding", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InferenceException(
            "model_capability_mismatch",
            "The requested model is not an embedding model.",
            ["Use the embeddinggemma package or another GGUF embedding model for /v1/embeddings."]);
    }

    private static float NormalizeFloat(double? value, float fallback)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return fallback;
        }

        return (float)value.Value;
    }

    private static int TryGetInt(JsonElement value, string propertyName, int fallback)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static float TryGetFloat(JsonElement value, string propertyName, float fallback)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var result)
            ? (float)result
            : fallback;
    }

    private static bool TryGetStopSequences(JsonElement value, out IReadOnlyList<string> stopSequences)
    {
        stopSequences = [];
        if (!value.TryGetProperty("stop", out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            stopSequences = [property.GetString() ?? string.Empty];
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        stopSequences = property
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return true;
    }
}
