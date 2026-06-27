using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleProbeResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("bundle_id")] string BundleId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("manifest_path")] string ManifestPath,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("components")] IReadOnlyList<NativeComponentProbeResult> Components,
    [property: JsonPropertyName("message")] string Message);
