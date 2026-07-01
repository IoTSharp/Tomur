using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleComponent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("source")] NativeBundleSource Source,
    [property: JsonPropertyName("wrapper_path")] string WrapperPath,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("runtime_path")] string RuntimePath,
    [property: JsonPropertyName("variants")] IReadOnlyList<NativeBundleVariant>? Variants,
    [property: JsonPropertyName("publisher")] bool Publisher,
    [property: JsonPropertyName("libraries")] IReadOnlyList<NativeBundleLibrary> Libraries,
    [property: JsonPropertyName("shared_dependencies")] IReadOnlyList<string> SharedDependencies);
