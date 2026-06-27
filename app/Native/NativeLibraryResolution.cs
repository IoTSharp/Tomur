using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeLibraryResolution(
    [property: JsonPropertyName("component_id")] string ComponentId,
    [property: JsonPropertyName("library_name")] string LibraryName,
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("component_runtime_path")] string ComponentRuntimePath,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("component_status")] string ComponentStatus,
    [property: JsonPropertyName("message")] string Message);
