using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChatCompletionChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage Usage);

public sealed record OpenAiChatCompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OpenAiChatCompletionMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record OpenAiChatCompletionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Content)
{
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiChatToolCall>? ToolCalls { get; init; }
}

public sealed record OpenAiCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiCompletionChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage Usage);

public sealed record OpenAiCompletionChoice(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record OpenAiChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChatCompletionChunkChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage? Usage);

public sealed record OpenAiChatCompletionChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] OpenAiChatCompletionDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record OpenAiChatCompletionDelta(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content)
{
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiChatToolCall>? ToolCalls { get; init; }
}

public sealed record OpenAiCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiCompletionChunkChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage? Usage);

public sealed record OpenAiCompletionChunkChoice(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);
