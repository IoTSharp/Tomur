using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record ProxyState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("http_proxy")] string? HttpProxy,
    [property: JsonPropertyName("https_proxy")] string? HttpsProxy,
    [property: JsonPropertyName("no_proxy")] string? NoProxy,
    [property: JsonPropertyName("message")] string Message);
