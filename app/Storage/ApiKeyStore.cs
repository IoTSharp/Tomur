using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Tomur.Storage;

public sealed class ApiKeyStore
{
    private const string Prefix = "tmr";

    private readonly LocalDatabaseInitializer database;

    public ApiKeyStore(LocalDatabaseInitializer database)
    {
        this.database = database;
    }

    public ApiKeyStoreState GetState()
    {
        try
        {
            using var connection = database.OpenConnection();
            var keys = ListActiveKeys(connection);

            return new ApiKeyStoreState(
                keys.Count > 0 ? "ok" : "warning",
                keys.Count,
                keys.Count > 0
                    ? "At least one local API key is available."
                    : "No local API key has been created yet.",
                keys);
        }
        catch (SqliteException exception)
        {
            return new ApiKeyStoreState(
                "error",
                0,
                $"API key store could not be read: {exception.SqliteErrorCode} {exception.Message}",
                Array.Empty<ApiKeyRecord>());
        }
    }

    public (ApiKeyRecord Record, string PlainTextKey) CreateKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = DateTimeOffset.UtcNow;
        var plainTextKey = CreatePlainTextKey();
        var record = new ApiKeyRecord(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            CreateDisplayPrefix(plainTextKey),
            now,
            null);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO api_keys(id, name, key_hash, prefix, created_at, last_used_at, revoked_at)
            VALUES ($id, $name, $key_hash, $prefix, $created_at, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$key_hash", HashKey(plainTextKey));
        command.Parameters.AddWithValue("$prefix", record.Prefix);
        command.Parameters.AddWithValue("$created_at", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return (record, plainTextKey);
    }

    public bool ValidateKey(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
        {
            return false;
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id
            FROM api_keys
            WHERE key_hash = $key_hash
              AND revoked_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key_hash", HashKey(plainTextKey));

        return command.ExecuteScalar() is not null;
    }

    private static List<ApiKeyRecord> ListActiveKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, prefix, created_at, last_used_at
            FROM api_keys
            WHERE revoked_at IS NULL
            ORDER BY created_at DESC;
            """;

        using var reader = command.ExecuteReader();
        var keys = new List<ApiKeyRecord>();

        while (reader.Read())
        {
            keys.Add(new ApiKeyRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseDateTimeOffset(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseDateTimeOffset(reader.GetString(4))));
        }

        return keys;
    }

    private static string CreatePlainTextKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return $"{Prefix}_{Base64UrlEncode(bytes)}";
    }

    private static string CreateDisplayPrefix(string plainTextKey)
    {
        return plainTextKey.Length <= 12
            ? plainTextKey
            : plainTextKey[..12];
    }

    private static string HashKey(string plainTextKey)
    {
        var bytes = Encoding.UTF8.GetBytes(plainTextKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}
