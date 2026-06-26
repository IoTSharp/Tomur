using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record DiskState(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("drive")] string Drive,
    [property: JsonPropertyName("available_bytes")] long? AvailableBytes,
    [property: JsonPropertyName("total_bytes")] long? TotalBytes,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);
