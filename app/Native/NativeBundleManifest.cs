using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("bundle_id")] string BundleId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("components")] IReadOnlyList<NativeBundleComponent> Components);
