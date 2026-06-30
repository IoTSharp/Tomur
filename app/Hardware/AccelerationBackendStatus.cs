using System.Text.Json.Serialization;

namespace Tomur.Hardware;

public sealed record AccelerationBackendStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("library_name")] string LibraryName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("message")] string Message);
