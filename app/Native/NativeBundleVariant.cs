using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleVariant(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("runtime_path")] string RuntimePath,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("required_backends")] IReadOnlyList<string> RequiredBackends,
    [property: JsonPropertyName("libraries")] IReadOnlyList<NativeBundleLibrary>? Libraries = null,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic = null,
    [property: JsonPropertyName("actions")] IReadOnlyList<string>? Actions = null);
