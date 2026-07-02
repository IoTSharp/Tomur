using System.Text.Json;
using System.Text.Json.Serialization;
using Tomur.Agents;
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

public sealed record ConversationDeleteResponse(
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

public sealed record ConversationTurnRequest(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("modality")] string? Modality,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ConversationAttachment>? Attachments,
    [property: JsonPropertyName("tool_mode")] string? ToolMode,
    [property: JsonPropertyName("tools")] IReadOnlyList<AgentChatToolRequest>? Tools,
    [property: JsonPropertyName("max_tool_rounds")] int? MaxToolRounds,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("history_limit")] int? HistoryLimit,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata,
    [property: JsonPropertyName("confirm")] bool? Confirm = null,
    [property: JsonPropertyName("speak")] bool? Speak = null,
    [property: JsonPropertyName("voice")] string? Voice = null,
    [property: JsonPropertyName("tts_model")] string? TtsModel = null,
    [property: JsonPropertyName("response_format")] string? ResponseFormat = null,
    [property: JsonPropertyName("speed")] double? Speed = null,
    [property: JsonPropertyName("language")] string? Language = null);

public sealed record ConversationTurnResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("messages")] IReadOnlyList<ConversationMessageRecord> Messages,
    [property: JsonPropertyName("user_message")] ConversationMessageRecord UserMessage,
    [property: JsonPropertyName("tool_message")] ConversationMessageRecord? ToolMessage,
    [property: JsonPropertyName("assistant_message")] ConversationMessageRecord? AssistantMessage,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ConversationDiagnosticRecord> Diagnostics,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<ConversationArtifactRecord> Artifacts,
    [property: JsonPropertyName("speech_artifact")] ConversationArtifactRecord? SpeechArtifact,
    [property: JsonPropertyName("speech_media_type")] string? SpeechMediaType,
    [property: JsonPropertyName("speech_bytes")] long? SpeechBytes,
    [property: JsonPropertyName("agent")] AgentChatResponse? Agent);

public sealed record ConversationVoiceTurnRequest(
    [property: JsonPropertyName("audio_base64")] string? AudioBase64,
    [property: JsonPropertyName("audio_data_uri")] string? AudioDataUri,
    [property: JsonPropertyName("audio_media_type")] string? AudioMediaType,
    [property: JsonPropertyName("audio_name")] string? AudioName,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("asr_model")] string? AsrModel,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("tts_model")] string? TtsModel,
    [property: JsonPropertyName("speak")] bool? Speak,
    [property: JsonPropertyName("voice")] string? Voice,
    [property: JsonPropertyName("response_format")] string? ResponseFormat,
    [property: JsonPropertyName("speed")] double? Speed,
    [property: JsonPropertyName("tool_mode")] string? ToolMode,
    [property: JsonPropertyName("tools")] IReadOnlyList<AgentChatToolRequest>? Tools,
    [property: JsonPropertyName("max_tool_rounds")] int? MaxToolRounds,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("history_limit")] int? HistoryLimit,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata);

public sealed record ConversationVoiceTurnResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conversation")] ConversationRecord Conversation,
    [property: JsonPropertyName("transcript")] string? Transcript,
    [property: JsonPropertyName("input_artifact")] ConversationArtifactRecord? InputArtifact,
    [property: JsonPropertyName("user_message")] ConversationMessageRecord? UserMessage,
    [property: JsonPropertyName("tool_message")] ConversationMessageRecord? ToolMessage,
    [property: JsonPropertyName("assistant_message")] ConversationMessageRecord? AssistantMessage,
    [property: JsonPropertyName("speech_artifact")] ConversationArtifactRecord? SpeechArtifact,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ConversationDiagnosticRecord> Diagnostics,
    [property: JsonPropertyName("turn")] ConversationTurnResponse? Turn,
    [property: JsonPropertyName("speech_media_type")] string? SpeechMediaType,
    [property: JsonPropertyName("speech_bytes")] long? SpeechBytes);

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
    [property: JsonPropertyName("metadata")] JsonElement? Metadata,
    [property: JsonPropertyName("data_uri")] string? DataUri = null,
    [property: JsonPropertyName("base64")] string? Base64 = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("content")] string? Content = null);

public sealed record ConversationToolCall(
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("result_json")] JsonElement? ResultJson,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic);

public sealed record ConversationVisionToolArguments(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("images")] IReadOnlyList<ConversationImageToolInput> Images);

public sealed record ConversationOcrToolArguments(
    [property: JsonPropertyName("image")] ConversationImageToolInput Image,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("prompt")] string? Prompt);

public sealed record ConversationImageToolInput(
    [property: JsonPropertyName("data_uri")] string DataUri,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record ConversationAudioTranscriptionToolArguments(
    [property: JsonPropertyName("audio_data_uri")] string AudioDataUri,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("language")] string? Language);

public sealed record ConversationImageGenerationToolArguments(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("model")] string? Model);

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
