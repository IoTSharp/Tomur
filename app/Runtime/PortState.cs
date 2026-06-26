using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record PortState(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);
