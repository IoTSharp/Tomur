using System.Text.Json.Serialization;

namespace Tomur.Multimodal;

public sealed record ImageGenerationWorkerRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("options")] ImageGenerationOptions Options);

public sealed record ImageGenerationWorkerResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("image_base64")] string? ImageBase64,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    [property: JsonPropertyName("error")] RuntimeWorkerError? Error);

public sealed record RuntimeWorkerError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
