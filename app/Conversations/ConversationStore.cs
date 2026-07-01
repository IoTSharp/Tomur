using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tomur.Serialization;
using Tomur.Storage;

namespace Tomur.Conversations;

public sealed class ConversationStore
{
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;
    private const int DefaultDetailLimit = 200;
    private const int MaxDetailLimit = 1000;

    private readonly LocalDatabaseInitializer database;

    public ConversationStore(LocalDatabaseInitializer database)
    {
        this.database = database;
    }

    public ConversationListResponse List(int? limit)
    {
        using var connection = database.OpenConnection();
        var normalizedLimit = NormalizeLimit(limit, DefaultListLimit, MaxListLimit);
        var conversations = new List<ConversationRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                c.id,
                c.title,
                c.status,
                c.model,
                c.created_at,
                c.updated_at,
                c.last_message_at,
                c.metadata_json,
                COUNT(DISTINCT m.id) AS message_count,
                COUNT(DISTINCT a.id) AS artifact_count,
                COUNT(DISTINCT d.id) AS diagnostic_count
            FROM conversations c
            LEFT JOIN conversation_messages m ON m.conversation_id = c.id
            LEFT JOIN conversation_artifacts a ON a.conversation_id = c.id
            LEFT JOIN conversation_diagnostics d ON d.conversation_id = c.id
            WHERE c.status <> 'deleted'
            GROUP BY c.id
            ORDER BY c.updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", normalizedLimit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            conversations.Add(ReadConversation(reader));
        }

        return new ConversationListResponse("ok", DateTimeOffset.UtcNow, conversations);
    }

    public ConversationDetailResponse Get(string conversationId, int? limit)
    {
        var id = NormalizeId(conversationId);
        using var connection = database.OpenConnection();
        var conversation = GetConversation(connection, id);
        var normalizedLimit = NormalizeLimit(limit, DefaultDetailLimit, MaxDetailLimit);

        return new ConversationDetailResponse(
            "ok",
            conversation,
            ListMessages(connection, id, normalizedLimit),
            ListArtifacts(connection, id, normalizedLimit),
            ListDiagnostics(connection, id, normalizedLimit));
    }

    public ConversationArtifactRecord GetArtifact(string conversationId, string artifactId)
    {
        var id = NormalizeId(conversationId);
        var normalizedArtifactId = NormalizeId(artifactId);
        using var connection = database.OpenConnection();
        _ = GetConversation(connection, id);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_id,
                type,
                path,
                media_type,
                source,
                status,
                bytes,
                created_at,
                metadata_json
            FROM conversation_artifacts
            WHERE conversation_id = $conversation_id
              AND id = $artifact_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversation_id", id);
        command.Parameters.AddWithValue("$artifact_id", normalizedArtifactId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return ReadArtifact(reader);
        }

        throw new ConversationStoreException(
            "not_found",
            "artifact_not_found",
            "The requested conversation artifact does not exist.",
            ["List conversation artifacts with GET /api/conversations/{conversationId}."]);
    }

    public IReadOnlyList<ConversationMessageRecord> ListRecentMessages(
        string conversationId,
        int? limit)
    {
        var id = NormalizeId(conversationId);
        using var connection = database.OpenConnection();
        _ = GetConversation(connection, id);
        var normalizedLimit = NormalizeLimit(limit, DefaultDetailLimit, MaxDetailLimit);
        return ListRecentMessages(connection, id, normalizedLimit);
    }

    public ConversationRecord Create(ConversationCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var title = NormalizeTitle(request.Title, "新会话");
        var model = NormalizeOptional(request.Model);
        var metadataJson = SerializeNullable(request.Metadata);
        var conversation = new ConversationRecord(
            Guid.NewGuid().ToString("N"),
            title,
            "active",
            model,
            now,
            now,
            null,
            0,
            0,
            0,
            CloneNullable(request.Metadata));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO conversations(
                id,
                title,
                status,
                model,
                created_at,
                updated_at,
                last_message_at,
                metadata_json)
            VALUES (
                $id,
                $title,
                'active',
                $model,
                $created_at,
                $updated_at,
                NULL,
                $metadata_json);
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$title", conversation.Title);
        AddNullable(command, "$model", conversation.Model);
        command.Parameters.AddWithValue("$created_at", FormatDate(now));
        command.Parameters.AddWithValue("$updated_at", FormatDate(now));
        AddNullable(command, "$metadata_json", metadataJson);
        command.ExecuteNonQuery();

        return conversation;
    }

    public ConversationAppendMessageResponse AppendMessage(
        string conversationId,
        ConversationAppendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = NormalizeId(conversationId);
        var role = NormalizeRole(request.Role);
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content) &&
            (request.Attachments is null || request.Attachments.Count == 0) &&
            (request.ToolCalls is null || request.ToolCalls.Count == 0))
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Message content, attachments or tool_calls are required.",
                ["Provide text content or register at least one attachment/tool call for this conversation message."]);
        }

        var now = DateTimeOffset.UtcNow;
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var conversation = GetConversation(connection, id, transaction);

        var message = new ConversationMessageRecord(
            Guid.NewGuid().ToString("N"),
            conversation.Id,
            role,
            content ?? string.Empty,
            NormalizeModality(request.Modality),
            NormalizeStatus(request.Status, "ok"),
            NormalizeOptional(request.Model) ?? conversation.Model,
            now,
            NormalizeAttachments(request.Attachments),
            NormalizeToolCalls(request.ToolCalls),
            NormalizeStringList(request.ArtifactIds),
            CloneNullable(request.Metadata));

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO conversation_messages(
                id,
                conversation_id,
                role,
                content,
                modality,
                status,
                model,
                created_at,
                attachments_json,
                tool_calls_json,
                artifact_ids_json,
                metadata_json)
            VALUES (
                $id,
                $conversation_id,
                $role,
                $content,
                $modality,
                $status,
                $model,
                $created_at,
                $attachments_json,
                $tool_calls_json,
                $artifact_ids_json,
                $metadata_json);
            """;
        insert.Parameters.AddWithValue("$id", message.Id);
        insert.Parameters.AddWithValue("$conversation_id", message.ConversationId);
        insert.Parameters.AddWithValue("$role", message.Role);
        insert.Parameters.AddWithValue("$content", message.Content);
        insert.Parameters.AddWithValue("$modality", message.Modality);
        insert.Parameters.AddWithValue("$status", message.Status);
        AddNullable(insert, "$model", message.Model);
        insert.Parameters.AddWithValue("$created_at", FormatDate(now));
        insert.Parameters.AddWithValue("$attachments_json", Serialize(message.Attachments.ToArray(), AppJsonSerializerContext.Default.ConversationAttachmentArray));
        insert.Parameters.AddWithValue("$tool_calls_json", Serialize(message.ToolCalls.ToArray(), AppJsonSerializerContext.Default.ConversationToolCallArray));
        insert.Parameters.AddWithValue("$artifact_ids_json", Serialize(message.ArtifactIds.ToArray(), AppJsonSerializerContext.Default.StringArray));
        AddNullable(insert, "$metadata_json", SerializeNullable(message.Metadata));
        insert.ExecuteNonQuery();

        UpdateConversationAfterActivity(
            connection,
            transaction,
            conversation.Id,
            conversation.Title == "新会话" && message.Role == "user"
                ? CreateTitle(message.Content)
                : null,
            message.Model,
            now,
            setLastMessageAt: true);

        transaction.Commit();
        return new ConversationAppendMessageResponse(
            "ok",
            GetConversation(connection, conversation.Id),
            message);
    }

    public ConversationRegisterArtifactResponse RegisterArtifact(
        string conversationId,
        ConversationRegisterArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = NormalizeId(conversationId);
        var type = NormalizeRequired(request.Type, "type");
        var path = NormalizeOptional(request.Path);
        if (string.IsNullOrWhiteSpace(path) && request.Bytes is null)
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Artifact path or bytes must be provided.",
                ["Register a local artifact path, or include bytes when the artifact is not materialized yet."]);
        }

        var now = DateTimeOffset.UtcNow;
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var conversation = GetConversation(connection, id, transaction);
        var artifact = new ConversationArtifactRecord(
            Guid.NewGuid().ToString("N"),
            conversation.Id,
            type,
            path,
            NormalizeOptional(request.MediaType),
            NormalizeOptional(request.Source),
            NormalizeStatus(request.Status, "available"),
            request.Bytes is >= 0 ? request.Bytes : null,
            now,
            CloneNullable(request.Metadata));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO conversation_artifacts(
                id,
                conversation_id,
                type,
                path,
                media_type,
                source,
                status,
                bytes,
                created_at,
                metadata_json)
            VALUES (
                $id,
                $conversation_id,
                $type,
                $path,
                $media_type,
                $source,
                $status,
                $bytes,
                $created_at,
                $metadata_json);
            """;
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$conversation_id", artifact.ConversationId);
        command.Parameters.AddWithValue("$type", artifact.Type);
        AddNullable(command, "$path", artifact.Path);
        AddNullable(command, "$media_type", artifact.MediaType);
        AddNullable(command, "$source", artifact.Source);
        command.Parameters.AddWithValue("$status", artifact.Status);
        if (artifact.Bytes is null)
        {
            command.Parameters.AddWithValue("$bytes", DBNull.Value);
        }
        else
        {
            command.Parameters.AddWithValue("$bytes", artifact.Bytes.Value);
        }

        command.Parameters.AddWithValue("$created_at", FormatDate(now));
        AddNullable(command, "$metadata_json", SerializeNullable(artifact.Metadata));
        command.ExecuteNonQuery();

        UpdateConversationAfterActivity(connection, transaction, conversation.Id, null, null, now, setLastMessageAt: false);
        transaction.Commit();

        return new ConversationRegisterArtifactResponse(
            "ok",
            GetConversation(connection, conversation.Id),
            artifact);
    }

    public ConversationAppendDiagnosticResponse AppendDiagnostic(
        string conversationId,
        ConversationAppendDiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = NormalizeId(conversationId);
        var code = NormalizeRequired(request.Code, "code");
        var message = NormalizeRequired(request.Message, "message");
        var now = DateTimeOffset.UtcNow;

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var conversation = GetConversation(connection, id, transaction);
        var diagnostic = new ConversationDiagnosticRecord(
            Guid.NewGuid().ToString("N"),
            conversation.Id,
            NormalizeStatus(request.Status, "warning"),
            code,
            message,
            NormalizeOptional(request.Model),
            NormalizeOptional(request.Backend),
            now,
            NormalizeStringList(request.Actions),
            CloneNullable(request.Metadata));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO conversation_diagnostics(
                id,
                conversation_id,
                status,
                code,
                message,
                model,
                backend,
                created_at,
                actions_json,
                metadata_json)
            VALUES (
                $id,
                $conversation_id,
                $status,
                $code,
                $message,
                $model,
                $backend,
                $created_at,
                $actions_json,
                $metadata_json);
            """;
        command.Parameters.AddWithValue("$id", diagnostic.Id);
        command.Parameters.AddWithValue("$conversation_id", diagnostic.ConversationId);
        command.Parameters.AddWithValue("$status", diagnostic.Status);
        command.Parameters.AddWithValue("$code", diagnostic.Code);
        command.Parameters.AddWithValue("$message", diagnostic.Message);
        AddNullable(command, "$model", diagnostic.Model);
        AddNullable(command, "$backend", diagnostic.Backend);
        command.Parameters.AddWithValue("$created_at", FormatDate(now));
        command.Parameters.AddWithValue("$actions_json", Serialize(diagnostic.Actions.ToArray(), AppJsonSerializerContext.Default.StringArray));
        AddNullable(command, "$metadata_json", SerializeNullable(diagnostic.Metadata));
        command.ExecuteNonQuery();

        UpdateConversationAfterActivity(connection, transaction, conversation.Id, null, diagnostic.Model, now, setLastMessageAt: false);
        transaction.Commit();

        return new ConversationAppendDiagnosticResponse(
            "ok",
            GetConversation(connection, conversation.Id),
            diagnostic);
    }

    private static ConversationRecord GetConversation(
        SqliteConnection connection,
        string conversationId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                c.id,
                c.title,
                c.status,
                c.model,
                c.created_at,
                c.updated_at,
                c.last_message_at,
                c.metadata_json,
                COUNT(DISTINCT m.id) AS message_count,
                COUNT(DISTINCT a.id) AS artifact_count,
                COUNT(DISTINCT d.id) AS diagnostic_count
            FROM conversations c
            LEFT JOIN conversation_messages m ON m.conversation_id = c.id
            LEFT JOIN conversation_artifacts a ON a.conversation_id = c.id
            LEFT JOIN conversation_diagnostics d ON d.conversation_id = c.id
            WHERE c.id = $id
              AND c.status <> 'deleted'
            GROUP BY c.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", conversationId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return ReadConversation(reader);
        }

        throw new ConversationStoreException(
            "not_found",
            "conversation_not_found",
            "The requested conversation does not exist.",
            ["Create a conversation first with POST /api/conversations."]);
    }

    private static IReadOnlyList<ConversationMessageRecord> ListMessages(
        SqliteConnection connection,
        string conversationId,
        int limit)
    {
        var messages = new List<ConversationMessageRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_id,
                role,
                content,
                modality,
                status,
                model,
                created_at,
                attachments_json,
                tool_calls_json,
                artifact_ids_json,
                metadata_json
            FROM conversation_messages
            WHERE conversation_id = $conversation_id
            ORDER BY created_at ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    private static IReadOnlyList<ConversationMessageRecord> ListRecentMessages(
        SqliteConnection connection,
        string conversationId,
        int limit)
    {
        var messages = new List<ConversationMessageRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_id,
                role,
                content,
                modality,
                status,
                model,
                created_at,
                attachments_json,
                tool_calls_json,
                artifact_ids_json,
                metadata_json
            FROM (
                SELECT
                    id,
                    conversation_id,
                    role,
                    content,
                    modality,
                    status,
                    model,
                    created_at,
                    attachments_json,
                    tool_calls_json,
                    artifact_ids_json,
                    metadata_json
                FROM conversation_messages
                WHERE conversation_id = $conversation_id
                ORDER BY created_at DESC
                LIMIT $limit
            ) AS recent
            ORDER BY created_at ASC;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    private static ConversationMessageRecord ReadMessage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ParseDate(reader.GetString(7)),
            DeserializeArray(reader.IsDBNull(8) ? null : reader.GetString(8), AppJsonSerializerContext.Default.ConversationAttachmentArray),
            DeserializeArray(reader.IsDBNull(9) ? null : reader.GetString(9), AppJsonSerializerContext.Default.ConversationToolCallArray),
            DeserializeArray(reader.IsDBNull(10) ? null : reader.GetString(10), AppJsonSerializerContext.Default.StringArray),
            ParseJsonElement(reader.IsDBNull(11) ? null : reader.GetString(11)));

    private static IReadOnlyList<ConversationArtifactRecord> ListArtifacts(
        SqliteConnection connection,
        string conversationId,
        int limit)
    {
        var artifacts = new List<ConversationArtifactRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_id,
                type,
                path,
                media_type,
                source,
                status,
                bytes,
                created_at,
                metadata_json
            FROM conversation_artifacts
            WHERE conversation_id = $conversation_id
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            artifacts.Add(ReadArtifact(reader));
        }

        return artifacts;
    }

    private static ConversationArtifactRecord ReadArtifact(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            ParseDate(reader.GetString(8)),
            ParseJsonElement(reader.IsDBNull(9) ? null : reader.GetString(9)));

    private static IReadOnlyList<ConversationDiagnosticRecord> ListDiagnostics(
        SqliteConnection connection,
        string conversationId,
        int limit)
    {
        var diagnostics = new List<ConversationDiagnosticRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                conversation_id,
                status,
                code,
                message,
                model,
                backend,
                created_at,
                actions_json,
                metadata_json
            FROM conversation_diagnostics
            WHERE conversation_id = $conversation_id
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            diagnostics.Add(new ConversationDiagnosticRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ParseDate(reader.GetString(7)),
                DeserializeArray(reader.IsDBNull(8) ? null : reader.GetString(8), AppJsonSerializerContext.Default.StringArray),
                ParseJsonElement(reader.IsDBNull(9) ? null : reader.GetString(9))));
        }

        return diagnostics;
    }

    private static void UpdateConversationAfterActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string? title,
        string? model,
        DateTimeOffset updatedAt,
        bool setLastMessageAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE conversations
            SET title = COALESCE($title, title),
                model = COALESCE($model, model),
                updated_at = $updated_at,
                last_message_at = CASE WHEN $set_last_message_at = 1 THEN $updated_at ELSE last_message_at END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        AddNullable(command, "$title", title);
        AddNullable(command, "$model", model);
        command.Parameters.AddWithValue("$updated_at", FormatDate(updatedAt));
        command.Parameters.AddWithValue("$set_last_message_at", setLastMessageAt ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader)
    {
        return new ConversationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
            checked((int)reader.GetInt64(8)),
            checked((int)reader.GetInt64(9)),
            checked((int)reader.GetInt64(10)),
            ParseJsonElement(reader.IsDBNull(7) ? null : reader.GetString(7)));
    }

    private static string NormalizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Conversation id is required.",
                ["Use a non-empty conversation id."]);
        }

        return value.Trim();
    }

    private static string NormalizeRole(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "user";
        }

        return normalized is "user" or "assistant" or "system" or "tool"
            ? normalized
            : throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Message role must be user, assistant, system or tool.",
                ["Set role to one of: user, assistant, system, tool."]);
    }

    private static string NormalizeModality(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "text" : normalized;
    }

    private static string NormalizeStatus(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeTitle(string? value, string fallback)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null ? fallback : normalized.Length > 120 ? normalized[..120] : normalized;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is not null)
        {
            return normalized;
        }

        throw new ConversationStoreException(
            "error",
            "invalid_request",
            $"The {fieldName} field is required.",
            [$"Provide {fieldName} in the request body."]);
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<ConversationAttachment> NormalizeAttachments(
        IReadOnlyList<ConversationAttachment>? values)
        => values?
            .Select(static value => value with
            {
                Id = NormalizeOptional(value.Id),
                Type = NormalizeOptional(value.Type),
                Name = NormalizeOptional(value.Name),
                MediaType = NormalizeOptional(value.MediaType),
                Path = NormalizeOptional(value.Path),
                Bytes = value.Bytes is >= 0 ? value.Bytes : null,
                Metadata = CloneNullable(value.Metadata),
                DataUri = NormalizeOptional(value.DataUri),
                Base64 = NormalizeOptional(value.Base64),
                Text = NormalizeOptional(value.Text),
                Content = NormalizeOptional(value.Content)
            })
            .ToArray()
            ?? [];

    private static IReadOnlyList<ConversationToolCall> NormalizeToolCalls(
        IReadOnlyList<ConversationToolCall>? values)
        => values?
            .Select(static value => value with
            {
                Tool = NormalizeOptional(value.Tool),
                Status = NormalizeOptional(value.Status),
                ArtifactId = NormalizeOptional(value.ArtifactId),
                Result = NormalizeOptional(value.Result),
                ResultJson = CloneNullable(value.ResultJson)
            })
            .ToArray()
            ?? [];

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string>? values)
        => values?
            .Select(static value => NormalizeOptional(value))
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];

    private static int NormalizeLimit(int? value, int fallback, int max)
        => value is > 0 ? Math.Clamp(value.Value, 1, max) : fallback;

    private static string CreateTitle(string content)
    {
        var normalized = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "新会话";
        }

        return normalized.Length > 40 ? normalized[..40] : normalized;
    }

    private static void AddNullable(SqliteCommand command, string name, string? value)
        => command.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static string Serialize<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    private static string? SerializeNullable(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.GetRawText();
    }

    private static JsonElement? CloneNullable(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.Clone();
    }

    private static JsonElement? ParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<T> DeserializeArray<T>(
        string? json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T[]> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
