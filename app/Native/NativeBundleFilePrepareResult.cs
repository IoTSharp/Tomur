using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleFilePrepareResult(
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("destination_path")] string DestinationPath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("message")] string Message);
