using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleSource(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("license")] string License);
