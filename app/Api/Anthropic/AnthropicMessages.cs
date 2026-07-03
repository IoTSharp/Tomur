using System.Text.Json;
using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Api.Anthropic;

public sealed record AnthropicModelListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<AnthropicModelResponse> Data,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("first_id")] string? FirstId,
    [property: JsonPropertyName("last_id")] string? LastId);

public sealed record AnthropicModelResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "model";
}

public sealed record AnthropicMessageRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicInputMessage>? Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("system")] JsonElement? System,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("stop_sequences")] IReadOnlyList<string>? StopSequences);

public sealed record AnthropicInputMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] JsonElement? Content);

public sealed record AnthropicMessageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("stop_sequence")] string? StopSequence,
    [property: JsonPropertyName("usage")] AnthropicUsage Usage);

public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record AnthropicTokenCountResponse(
    [property: JsonPropertyName("input_tokens")] int InputTokens);

public sealed record AnthropicErrorResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("error")] AnthropicError Error)
{
    public static AnthropicErrorResponse InvalidRequest(string message)
        => new("error", new AnthropicError("invalid_request_error", message, null));

    public static AnthropicErrorResponse ModelNotFound(RuntimeDiagnostic diagnostic)
        => new("error", new AnthropicError("not_found_error", diagnostic.Message, diagnostic));

    public static AnthropicErrorResponse RuntimeUnavailable(RuntimeDiagnostic diagnostic)
        => new("error", new AnthropicError("api_error", diagnostic.Message, diagnostic));

    public static AnthropicErrorResponse ContextLengthExceeded(RuntimeDiagnostic diagnostic)
        => new("error", new AnthropicError("invalid_request_error", diagnostic.Message, diagnostic));
}

public sealed record AnthropicError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic);

public sealed record AnthropicMessageStartEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] AnthropicMessageResponse Message);

public sealed record AnthropicContentBlockStartEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("content_block")] AnthropicContentBlock ContentBlock);

public sealed record AnthropicContentBlockDeltaEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] AnthropicTextDelta Delta);

public sealed record AnthropicTextDelta(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record AnthropicContentBlockStopEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("index")] int Index);

public sealed record AnthropicMessageDeltaEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("delta")] AnthropicMessageDelta Delta,
    [property: JsonPropertyName("usage")] AnthropicDeltaUsage Usage);

public sealed record AnthropicMessageDelta(
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("stop_sequence")] string? StopSequence);

public sealed record AnthropicDeltaUsage(
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record AnthropicMessageStopEvent(
    [property: JsonPropertyName("type")] string Type);
