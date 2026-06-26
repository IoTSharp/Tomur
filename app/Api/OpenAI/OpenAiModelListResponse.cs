using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiModelListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiModelResponse> Data)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";
}

public sealed record OpenAiModelResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "model";
}
