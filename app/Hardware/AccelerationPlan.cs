using System.Text.Json.Serialization;

namespace Tomur.Hardware;

public sealed record AccelerationPlan(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("preferred_backend")] string PreferredBackend,
    [property: JsonPropertyName("effective_backend")] string EffectiveBackend,
    [property: JsonPropertyName("configured_gpu_layers")] int ConfiguredGpuLayers,
    [property: JsonPropertyName("effective_gpu_layers")] int EffectiveGpuLayers,
    [property: JsonPropertyName("recommended_gpu_layers")] int RecommendedGpuLayers,
    [property: JsonPropertyName("selected_accelerator_key")] string? SelectedAcceleratorKey,
    [property: JsonPropertyName("selected_accelerator")] AcceleratorDevice? SelectedAccelerator,
    [property: JsonPropertyName("devices")] IReadOnlyList<AcceleratorDevice> Devices,
    [property: JsonPropertyName("backends")] IReadOnlyList<AccelerationBackendStatus> Backends,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
