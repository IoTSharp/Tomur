using System.Text.Json.Serialization;

namespace Tomur.Api;

public sealed record RuntimeSessionLoadRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("context_size")] int? ContextSize);

public sealed record RuntimeSessionControlError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);
