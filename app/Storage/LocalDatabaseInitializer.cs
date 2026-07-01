using System.Globalization;
using Microsoft.Data.Sqlite;
using Tomur.Config;

namespace Tomur.Storage;

public sealed class LocalDatabaseInitializer
{
    private readonly DataPaths paths;

    public LocalDatabaseInitializer(DataPaths paths)
    {
        this.paths = paths;
    }

    public LocalDatabaseState EnsureDatabase()
    {
        try
        {
            var databaseDirectory = Path.GetDirectoryName(paths.DatabasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            var existed = File.Exists(paths.DatabasePath);
            using var connection = OpenConnection();
            CreateSchema(connection);
            var schemaVersion = ReadSchemaVersion(connection);

            return new LocalDatabaseState(
                existed ? "ok" : "created",
                paths.DatabasePath,
                schemaVersion,
                existed
                    ? "SQLite database is readable."
                    : "SQLite database was initialized.");
        }
        catch (SqliteException exception)
        {
            return new LocalDatabaseState(
                "error",
                paths.DatabasePath,
                0,
                $"SQLite database could not be opened: {exception.SqliteErrorCode} {exception.Message}");
        }
        catch (IOException exception)
        {
            return new LocalDatabaseState(
                "error",
                paths.DatabasePath,
                0,
                $"SQLite database path could not be accessed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new LocalDatabaseState(
                "error",
                paths.DatabasePath,
                0,
                $"SQLite database path could not be accessed: {exception.Message}");
        }
    }

    public SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );

            INSERT INTO metadata(key, value)
            VALUES ('schema_version', $schema_version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            CREATE TABLE IF NOT EXISTS api_keys (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                key_hash TEXT NOT NULL UNIQUE,
                prefix TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_used_at TEXT NULL,
                revoked_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_api_keys_active
            ON api_keys(revoked_at, created_at);

            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                model TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_message_at TEXT NULL,
                metadata_json TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_conversations_updated_at
            ON conversations(status, updated_at DESC);

            CREATE TABLE IF NOT EXISTS conversation_messages (
                id TEXT PRIMARY KEY NOT NULL,
                conversation_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                modality TEXT NOT NULL,
                status TEXT NOT NULL,
                model TEXT NULL,
                created_at TEXT NOT NULL,
                attachments_json TEXT NOT NULL,
                tool_calls_json TEXT NOT NULL,
                artifact_ids_json TEXT NOT NULL,
                metadata_json TEXT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_conversation_messages_conversation
            ON conversation_messages(conversation_id, created_at);

            CREATE TABLE IF NOT EXISTS conversation_artifacts (
                id TEXT PRIMARY KEY NOT NULL,
                conversation_id TEXT NOT NULL,
                type TEXT NOT NULL,
                path TEXT NULL,
                media_type TEXT NULL,
                source TEXT NULL,
                status TEXT NOT NULL,
                bytes INTEGER NULL,
                created_at TEXT NOT NULL,
                metadata_json TEXT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_conversation_artifacts_conversation
            ON conversation_artifacts(conversation_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS conversation_diagnostics (
                id TEXT PRIMARY KEY NOT NULL,
                conversation_id TEXT NOT NULL,
                status TEXT NOT NULL,
                code TEXT NOT NULL,
                message TEXT NOT NULL,
                model TEXT NULL,
                backend TEXT NULL,
                created_at TEXT NOT NULL,
                actions_json TEXT NOT NULL,
                metadata_json TEXT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_conversation_diagnostics_conversation
            ON conversation_diagnostics(conversation_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS file_documents (
                id TEXT PRIMARY KEY NOT NULL,
                root TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                absolute_path TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                extension TEXT NOT NULL,
                media_type TEXT NULL,
                size_bytes INTEGER NOT NULL,
                last_modified_utc TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                indexed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_file_documents_root
            ON file_documents(root, relative_path);

            CREATE TABLE IF NOT EXISTS file_chunks (
                id TEXT PRIMARY KEY NOT NULL,
                document_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                char_count INTEGER NOT NULL,
                FOREIGN KEY(document_id) REFERENCES file_documents(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_file_chunks_document
            ON file_chunks(document_id, ordinal);
            """;
        command.Parameters.AddWithValue("$schema_version", Defaults.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version' LIMIT 1;";

        var value = command.ExecuteScalar() as string;
        return int.TryParse(value, out var schemaVersion)
            ? schemaVersion
            : 0;
    }
}
