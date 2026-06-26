using System.Text.Json.Serialization;

namespace Tomur.Api;

public sealed record RootResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("endpoints")] IReadOnlyList<string> Endpoints);
