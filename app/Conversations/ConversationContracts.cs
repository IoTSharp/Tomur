using System.Text.Json;
using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Conversations;

public sealed record ConversationListResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("conversations")] IReadOnlyList<ConversationRecord> Conversations);

public sealed record ConversationDetailResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("messages")] IReadOnlyList<ConversationMessageRecord> Messages,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<ConversationArtifactRecord> Artifacts,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ConversationDiagnosticRecord> Diagnostics);

public sealed record ConversationCreateRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationCreateResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation);

public sealed record ConversationAppendMessageRequest(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("modality")] string? Modality,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ConversationAttachment>? Attachments,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ConversationToolCall>? ToolCalls,
    [property: JsonPropertyName("artifact_ids")] IReadOnlyList<string>? ArtifactIds,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationAppendMessageResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("message")] ConversationMessageRecord Message);

public sealed record ConversationRegisterArtifactRequest(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationRegisterArtifactResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("artifact")] ConversationArtifactRecord Artifact);

public sealed record ConversationAppendDiagnosticRequest(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("backend")] string? Backend,
    [property: JsonPropertyName("actions")] IReadOnlyList<string>? Actions,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationAppendDiagnosticResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("diagnostic")] ConversationDiagnosticRecord Diagnostic);

public sealed record ConversationRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_message_at")] DateTimeOffset? LastMessageAt,
    [property: JsonPropertyName("message_count")] int MessageCount,
    [property: JsonPropertyName("artifact_count")] int ArtifactCount,
    [property: JsonPropertyName("diagnostic_count")] int DiagnosticCount,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationMessageRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("modality")] string Modality,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ConversationAttachment> Attachments,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ConversationToolCall> ToolCalls,
    [property: JsonPropertyName("artifact_ids")] IReadOnlyList<string> ArtifactIds,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationArtifactRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationDiagnosticRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("backend")] string? Backend,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationAttachment(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationToolCall(
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("result_json")] JsonElement? ResultJson,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic);

public sealed class ConversationStoreException : Exception
{
    public ConversationStoreException(
        string status,
        string code,
        string message,
        IReadOnlyList<string>? actions = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Actions = actions ?? [];
    }

    public string Status { get; }
    public string Code { get; }
    public IReadOnlyList<string> Actions { get; }
}
