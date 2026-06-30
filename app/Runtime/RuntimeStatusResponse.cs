using System.Text.Json.Serialization;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Native;
using Tomur.Storage;

namespace Tomur.Runtime;

public sealed record RuntimeStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("system")] SystemSnapshot System,
    [property: JsonPropertyName("paths")] PathConfiguration Paths,
    [property: JsonPropertyName("configuration")] ConfigurationState Configuration,
    [property: JsonPropertyName("directories")] IReadOnlyList<DirectoryState> Directories,
    [property: JsonPropertyName("database")] LocalDatabaseState Database,
    [property: JsonPropertyName("api_keys")] ApiKeyStoreState ApiKeys,
    [property: JsonPropertyName("disk")] DiskState Disk,
    [property: JsonPropertyName("proxy")] ProxyState Proxy,
    [property: JsonPropertyName("port")] PortState Port,
    [property: JsonPropertyName("acceleration")] AccelerationPlan Acceleration,
    [property: JsonPropertyName("native_bundle")] NativeBundleProbeResult NativeBundle,
    [property: JsonPropertyName("runtime")] RuntimeDiagnostic Runtime,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<DiagnosticItem> Diagnostics);
