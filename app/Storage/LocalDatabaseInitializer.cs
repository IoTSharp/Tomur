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
        return connection;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

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
