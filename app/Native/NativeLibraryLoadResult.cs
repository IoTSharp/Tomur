using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeLibraryLoadResult(
    [property: JsonPropertyName("resolution")] NativeLibraryResolution Resolution,
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("handle")] string? Handle,
    [property: JsonPropertyName("message")] string Message);
