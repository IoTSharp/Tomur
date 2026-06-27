using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeComponentProbeResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("runtime_path")] string RuntimePath,
    [property: JsonPropertyName("publisher")] bool Publisher,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("source")] NativeBundleSource Source,
    [property: JsonPropertyName("wrapper_path")] string WrapperPath,
    [property: JsonPropertyName("shared_dependencies")] IReadOnlyList<string> SharedDependencies,
    [property: JsonPropertyName("libraries")] IReadOnlyList<NativeLibraryProbeResult> Libraries,
    [property: JsonPropertyName("message")] string Message);
