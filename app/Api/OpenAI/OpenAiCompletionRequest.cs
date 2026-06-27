using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiCompletionRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] JsonElement? Prompt,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens);
