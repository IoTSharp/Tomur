using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiEmbeddingRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input")] JsonElement? Input,
    [property: JsonPropertyName("encoding_format")] string? EncodingFormat,
    [property: JsonPropertyName("dimensions")] int? Dimensions);
