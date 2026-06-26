using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record RuntimeDiagnostic(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
