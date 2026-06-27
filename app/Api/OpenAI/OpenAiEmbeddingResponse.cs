using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiEmbeddingResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiEmbeddingData> Data,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] OpenAiUsage Usage);

public sealed record OpenAiEmbeddingData(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding,
    [property: JsonPropertyName("index")] int Index);
