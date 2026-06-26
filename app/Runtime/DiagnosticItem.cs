using System.Text.Json.Serialization;

namespace Tomur.Runtime;

public sealed record DiagnosticItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
