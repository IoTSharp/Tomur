using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("options")] JsonElement? Options);

public sealed record OllamaChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);
