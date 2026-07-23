using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiChatCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens)
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenAiChatTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; init; }
}

public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] JsonElement? Content)
{
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record OpenAiChatTool(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("function")] OpenAiChatToolFunction? Function);

public sealed record OpenAiChatToolFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters)
{
    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }
}

public sealed record OpenAiChatToolCall(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("function")] OpenAiChatToolCallFunction? Function)
{
    [JsonPropertyName("index")]
    public int? Index { get; init; }
}

public sealed record OpenAiChatToolCallFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments);
