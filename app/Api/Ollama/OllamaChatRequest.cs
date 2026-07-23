using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("options")] JsonElement? Options)
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<OllamaChatTool>? Tools { get; init; }
}

public sealed record OllamaChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content)
{
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OllamaChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }
}

public sealed record OllamaChatTool(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("function")] OllamaChatToolFunction? Function);

public sealed record OllamaChatToolFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters);

public sealed record OllamaChatToolCall(
    [property: JsonPropertyName("function")] OllamaChatToolCallFunction? Function)
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public sealed record OllamaChatToolCallFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments)
{
    [JsonPropertyName("index")]
    public int? Index { get; init; }
}
