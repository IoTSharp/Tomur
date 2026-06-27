using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaGenerateRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("template")] string? Template,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("options")] JsonElement? Options);
