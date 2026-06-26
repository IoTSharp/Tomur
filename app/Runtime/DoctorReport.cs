using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record DoctorReport(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("os_description")] string OSDescription,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("framework_description")] string FrameworkDescription,
    [property: JsonPropertyName("runtime")] RuntimeDiagnostic Runtime);
