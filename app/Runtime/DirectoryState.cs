using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record DirectoryState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);
