using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundlePrepareResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("prepared_at")] DateTimeOffset PreparedAt,
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("bundle_id")] string BundleId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("manifest_path")] string ManifestPath,
    [property: JsonPropertyName("source_runtime_root")] string SourceRuntimeRoot,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("files")] IReadOnlyList<NativeBundleFilePrepareResult> Files,
    [property: JsonPropertyName("message")] string Message);
