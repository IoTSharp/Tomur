using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeLibraryProbeResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("checksum_status")] string ChecksumStatus);
