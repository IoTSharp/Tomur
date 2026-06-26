using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiChatCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens);

public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] JsonElement? Content);
