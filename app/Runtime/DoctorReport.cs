using System.Text.Json.Serialization;
using Tomur.Native;

namespace Tomur.Runtime;

public sealed record DoctorReport(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("os_description")] string OSDescription,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("framework_description")] string FrameworkDescription,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("native_bundle")] NativeBundleProbeResult NativeBundle,
    [property: JsonPropertyName("runtime")] RuntimeDiagnostic Runtime,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<DiagnosticItem> Diagnostics,
    [property: JsonPropertyName("details")] RuntimeStatusResponse Details);
