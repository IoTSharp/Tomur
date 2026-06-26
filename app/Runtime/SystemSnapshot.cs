using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record SystemSnapshot(
    [property: JsonPropertyName("os_description")] string OSDescription,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("framework_description")] string FrameworkDescription,
    [property: JsonPropertyName("processor_count")] int ProcessorCount,
    [property: JsonPropertyName("cpu_name")] string? CpuName,
    [property: JsonPropertyName("total_memory_bytes")] ulong? TotalMemoryBytes);
