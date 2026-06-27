using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Multimodal;

public sealed record VisionAnalysisRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("images")] IReadOnlyList<MultimodalImageInput>? Images,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature);

public sealed record OcrAnalysisRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("image")] MultimodalImageInput? Image,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("language")] string? Language);

public sealed record MultimodalImageInput(
    [property: JsonPropertyName("image_url")] string? ImageUrl,
    [property: JsonPropertyName("data_uri")] string? DataUri,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record MultimodalInputSummary(
    [property: JsonPropertyName("text_characters")] int TextCharacters,
    [property: JsonPropertyName("image_count")] int ImageCount,
    [property: JsonPropertyName("audio_bytes")] long? AudioBytes,
    [property: JsonPropertyName("response_format")] string? ResponseFormat,
    [property: JsonPropertyName("size")] string? Size);

public sealed record MultimodalOperationResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic Diagnostic,
    [property: JsonPropertyName("backend_status")] MultimodalBackendStatus BackendStatus,
    [property: JsonPropertyName("input")] MultimodalInputSummary Input);

public sealed record MultimodalTextResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    [property: JsonPropertyName("backend_status")] MultimodalBackendStatus BackendStatus,
    [property: JsonPropertyName("input")] MultimodalInputSummary Input);
