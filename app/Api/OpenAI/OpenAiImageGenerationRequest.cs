using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiImageGenerationRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("n")] int? Count,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("response_format")] string? ResponseFormat);
