using System.Text.Json.Serialization;

namespace Tomur.Hardware;

public sealed record AcceleratorDevice(
    [property: JsonPropertyName("device_index")] int DeviceIndex,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("memory_bytes")] ulong? MemoryBytes,
    [property: JsonPropertyName("selection_key")] string SelectionKey,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("integrated")] bool IsIntegrated,
    [property: JsonPropertyName("device_id")] string? DeviceId);
