using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaModelListResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<OllamaModelResponse> Models);

public sealed record OllamaModelResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("modified_at")] DateTimeOffset ModifiedAt,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("details")] OllamaModelDetails Details);

public sealed record OllamaModelDetails(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("parameter_size")] string ParameterSize,
    [property: JsonPropertyName("quantization_level")] string QuantizationLevel);
