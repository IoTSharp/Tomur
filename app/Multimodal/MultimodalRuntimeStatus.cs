using System.Text.Json.Serialization;

namespace Tomur.Multimodal;

public sealed record MultimodalRuntimeStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("backends")] IReadOnlyList<MultimodalBackendStatus> Backends,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);

public sealed record MultimodalBackendStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("native_component_id")] string NativeComponentId,
    [property: JsonPropertyName("native_status")] string? NativeStatus,
    [property: JsonPropertyName("native_message")] string? NativeMessage,
    [property: JsonPropertyName("model_requirement")] string ModelRequirement,
    [property: JsonPropertyName("visible_model_ids")] IReadOnlyList<string> VisibleModelIds,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
