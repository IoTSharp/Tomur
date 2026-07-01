using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Storage;

namespace Tomur.Agents;

public sealed class FileIndexStore
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 20;
    private const int DefaultMaxFiles = 512;
    private const int MaxFiles = 4096;
    private const long DefaultMaxFileBytes = 1L * 1024L * 1024L;
    private const long MaxFileBytes = 5L * 1024L * 1024L;
    private const int MaxChunkChars = 1200;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".markdown",
        ".json",
        ".jsonl",
        ".csv",
        ".tsv",
        ".log",
        ".yaml",
        ".yml",
        ".xml",
        ".html",
        ".htm",
        ".cs",
        ".ts",
        ".tsx",
        ".js",
        ".jsx"
    };

    private readonly LocalDatabaseInitializer database;
    private readonly DataPaths paths;

    public FileIndexStore(LocalDatabaseInitializer database, DataPaths paths)
    {
        this.database = database;
        this.paths = paths;
    }

    public FileSearchToolResult Search(FileSearchToolArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var query = NormalizeRequired(arguments.Query, "query");
        var root = ResolveRoot(arguments.Root);
        var topK = NormalizeInt(arguments.TopK, DefaultTopK, 1, MaxTopK);
        var refresh = arguments.Refresh ?? true;
        var maxFiles = NormalizeInt(arguments.MaxFiles, DefaultMaxFiles, 1, MaxFiles);
        var maxFileBytes = NormalizeLong(arguments.MaxFileBytes, DefaultMaxFileBytes, 1, MaxFileBytes);

        Directory.CreateDirectory(root);
        var databaseState = database.EnsureDatabase();
        if (databaseState.Status == "error")
        {
            throw new InferenceException(
                "file_index_unavailable",
                databaseState.Message,
                ["Run tomur doctor to inspect the local SQLite database state."]);
        }

        using var connection = database.OpenConnection();
        var ftsAvailable = EnsureSchema(connection);
        var index = refresh
            ? Refresh(connection, root, maxFiles, maxFileBytes, ftsAvailable)
            : ReadIndexSummary(connection, root, false, ftsAvailable);
        var matches = SearchIndexedChunks(connection, root, query, topK, ftsAvailable, out var searchMode);
        var diagnostics = new List<string>
        {
            $"root: {root}",
            $"index-refreshed: {refresh.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}",
            $"search-mode: {searchMode}",
            $"indexed-documents: {index.DocumentCount}",
            $"indexed-chunks: {index.ChunkCount}"
        };

        if (!ftsAvailable)
        {
            diagnostics.Add("sqlite-fts: unavailable; fallback LIKE search was used.");
        }

        return new FileSearchToolResult(
            matches.Count > 0 ? "ok" : "empty",
            root,
            query,
            topK,
            matches.Count,
            index,
            matches,
            BuildContext(matches),
            diagnostics);
    }

    private FileIndexSummary Refresh(
        SqliteConnection connection,
        string root,
        int maxFiles,
        long maxFileBytes,
        bool ftsAvailable)
    {
        var now = DateTimeOffset.UtcNow;
        var scanned = 0;
        var indexed = 0;
        var skipped = 0;
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateCandidateFiles(root, maxFiles))
        {
            scanned++;
            seen.Add(file);

            var info = new FileInfo(file);
            if (!info.Exists || info.Length <= 0 || info.Length > maxFileBytes)
            {
                skipped++;
                continue;
            }

            if (!SupportedExtensions.Contains(info.Extension))
            {
                skipped++;
                continue;
            }

            if (!TryReadTextFile(file, maxFileBytes, out var text, out var sha256))
            {
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                skipped++;
                continue;
            }

            var lastModified = info.LastWriteTimeUtc;
            if (IsDocumentCurrent(connection, file, info.Length, lastModified, sha256))
            {
                indexed++;
                continue;
            }

            UpsertDocument(connection, root, file, info, text, sha256, now, ftsAvailable);
            indexed++;
            updated++;
        }

        RemoveStaleDocuments(connection, root, seen, ftsAvailable);
        var summary = ReadIndexSummary(connection, root, true, ftsAvailable);
        return summary with
        {
            ScannedFiles = scanned,
            IndexedFiles = indexed,
            SkippedFiles = skipped,
            UpdatedFiles = updated,
            RefreshedAt = now
        };
    }

    private static bool EnsureSchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
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
            command.ExecuteNonQuery();
        }

        try
        {
            using var fts = connection.CreateCommand();
            fts.CommandText =
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS file_chunks_fts
                USING fts5(
                    text,
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    relative_path UNINDEXED,
                    tokenize = 'unicode61'
                );
                """;
            fts.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static IReadOnlyList<FileSearchMatch> SearchIndexedChunks(
        SqliteConnection connection,
        string root,
        string query,
        int topK,
        bool ftsAvailable,
        out string searchMode)
    {
        if (ftsAvailable && TrySearchFts(connection, root, query, topK, out var ftsMatches))
        {
            searchMode = "sqlite-fts5";
            return ftsMatches;
        }

        searchMode = "sqlite-like";
        return SearchLike(connection, root, query, topK);
    }

    private static bool TrySearchFts(
        SqliteConnection connection,
        string root,
        string query,
        int topK,
        out IReadOnlyList<FileSearchMatch> matches)
    {
        matches = [];
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return false;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    c.id,
                    d.relative_path,
                    d.absolute_path,
                    d.name,
                    d.media_type,
                    c.start_line,
                    c.end_line,
                    c.text,
                    snippet(file_chunks_fts, 0, '', '', '...', 18) AS snippet,
                    bm25(file_chunks_fts) AS rank
                FROM file_chunks_fts
                JOIN file_chunks c ON c.id = file_chunks_fts.chunk_id
                JOIN file_documents d ON d.id = c.document_id
                WHERE file_chunks_fts MATCH $query
                  AND d.root = $root
                ORDER BY rank
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$limit", topK);

            var collected = new List<FileSearchMatch>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                collected.Add(ReadSearchMatch(reader, scoreColumn: 9));
            }

            matches = collected;
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static IReadOnlyList<FileSearchMatch> SearchLike(
        SqliteConnection connection,
        string root,
        string query,
        int topK)
    {
        var terms = ExtractTerms(query);
        using var command = connection.CreateCommand();
        var predicates = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            var parameter = $"$term{i}";
            predicates.Add($"LOWER(c.text) LIKE {parameter} OR LOWER(d.name) LIKE {parameter}");
            command.Parameters.AddWithValue(parameter, $"%{terms[i].ToLowerInvariant()}%");
        }

        if (predicates.Count == 0)
        {
            predicates.Add("LOWER(c.text) LIKE $query");
            command.Parameters.AddWithValue("$query", $"%{query.ToLowerInvariant()}%");
        }

        command.CommandText =
            $"""
             SELECT
                 c.id,
                 d.relative_path,
                 d.absolute_path,
                 d.name,
                 d.media_type,
                 c.start_line,
                 c.end_line,
                 c.text,
                 c.text AS snippet,
                 0.0 AS rank
             FROM file_chunks c
             JOIN file_documents d ON d.id = c.document_id
             WHERE d.root = $root
               AND ({string.Join(" OR ", predicates)})
             ORDER BY d.relative_path, c.ordinal
             LIMIT $limit;
             """;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$limit", topK);

        var matches = new List<FileSearchMatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(ReadSearchMatch(reader, scoreColumn: 9));
        }

        return matches;
    }

    private static FileSearchMatch ReadSearchMatch(SqliteDataReader reader, int scoreColumn)
    {
        var text = reader.GetString(7);
        var snippet = reader.GetString(8);
        if (snippet.Length > 500)
        {
            snippet = snippet[..500] + "...";
        }

        if (text.Length > 900)
        {
            text = text[..900] + "...";
        }

        return new FileSearchMatch(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            snippet,
            text,
            reader.IsDBNull(scoreColumn) ? null : reader.GetDouble(scoreColumn));
    }

    private static void UpsertDocument(
        SqliteConnection connection,
        string root,
        string file,
        FileInfo info,
        string text,
        string sha256,
        DateTimeOffset indexedAt,
        bool ftsAvailable)
    {
        var documentId = FindDocumentId(connection, file) ?? Guid.NewGuid().ToString("N");
        DeleteChunks(connection, documentId, ftsAvailable);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO file_documents(
                    id,
                    root,
                    relative_path,
                    absolute_path,
                    name,
                    extension,
                    media_type,
                    size_bytes,
                    last_modified_utc,
                    sha256,
                    indexed_at)
                VALUES (
                    $id,
                    $root,
                    $relative_path,
                    $absolute_path,
                    $name,
                    $extension,
                    $media_type,
                    $size_bytes,
                    $last_modified_utc,
                    $sha256,
                    $indexed_at)
                ON CONFLICT(absolute_path) DO UPDATE SET
                    root = excluded.root,
                    relative_path = excluded.relative_path,
                    name = excluded.name,
                    extension = excluded.extension,
                    media_type = excluded.media_type,
                    size_bytes = excluded.size_bytes,
                    last_modified_utc = excluded.last_modified_utc,
                    sha256 = excluded.sha256,
                    indexed_at = excluded.indexed_at;
                """;
            command.Parameters.AddWithValue("$id", documentId);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$relative_path", Path.GetRelativePath(root, file).Replace('\\', '/'));
            command.Parameters.AddWithValue("$absolute_path", file);
            command.Parameters.AddWithValue("$name", info.Name);
            command.Parameters.AddWithValue("$extension", info.Extension);
            command.Parameters.AddWithValue("$media_type", ResolveMediaType(info.Extension));
            command.Parameters.AddWithValue("$size_bytes", info.Length);
            command.Parameters.AddWithValue("$last_modified_utc", FormatDate(info.LastWriteTimeUtc));
            command.Parameters.AddWithValue("$sha256", sha256);
            command.Parameters.AddWithValue("$indexed_at", FormatDate(indexedAt));
            command.ExecuteNonQuery();
        }

        var ordinal = 0;
        foreach (var chunk in CreateChunks(text))
        {
            var chunkId = Guid.NewGuid().ToString("N");
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO file_chunks(
                        id,
                        document_id,
                        ordinal,
                        text,
                        start_line,
                        end_line,
                        char_count)
                    VALUES (
                        $id,
                        $document_id,
                        $ordinal,
                        $text,
                        $start_line,
                        $end_line,
                        $char_count);
                    """;
                insert.Parameters.AddWithValue("$id", chunkId);
                insert.Parameters.AddWithValue("$document_id", documentId);
                insert.Parameters.AddWithValue("$ordinal", ordinal++);
                insert.Parameters.AddWithValue("$text", chunk.Text);
                insert.Parameters.AddWithValue("$start_line", chunk.StartLine);
                insert.Parameters.AddWithValue("$end_line", chunk.EndLine);
                insert.Parameters.AddWithValue("$char_count", chunk.Text.Length);
                insert.ExecuteNonQuery();
            }

            if (!ftsAvailable)
            {
                continue;
            }

            using var fts = connection.CreateCommand();
            fts.CommandText =
                """
                INSERT INTO file_chunks_fts(text, chunk_id, document_id, relative_path)
                VALUES ($text, $chunk_id, $document_id, $relative_path);
                """;
            fts.Parameters.AddWithValue("$text", chunk.Text);
            fts.Parameters.AddWithValue("$chunk_id", chunkId);
            fts.Parameters.AddWithValue("$document_id", documentId);
            fts.Parameters.AddWithValue("$relative_path", Path.GetRelativePath(root, file).Replace('\\', '/'));
            fts.ExecuteNonQuery();
        }
    }

    private static void RemoveStaleDocuments(
        SqliteConnection connection,
        string root,
        HashSet<string> seen,
        bool ftsAvailable)
    {
        var staleIds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, absolute_path FROM file_documents WHERE root = $root;";
            command.Parameters.AddWithValue("$root", root);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                if (!seen.Contains(path) || !File.Exists(path))
                {
                    staleIds.Add(reader.GetString(0));
                }
            }
        }

        foreach (var documentId in staleIds)
        {
            DeleteChunks(connection, documentId, ftsAvailable);
            using var deleteDocument = connection.CreateCommand();
            deleteDocument.CommandText = "DELETE FROM file_documents WHERE id = $id;";
            deleteDocument.Parameters.AddWithValue("$id", documentId);
            deleteDocument.ExecuteNonQuery();
        }
    }

    private static void DeleteChunks(
        SqliteConnection connection,
        string documentId,
        bool ftsAvailable)
    {
        if (ftsAvailable)
        {
            using var deleteFts = connection.CreateCommand();
            deleteFts.CommandText = "DELETE FROM file_chunks_fts WHERE document_id = $document_id;";
            deleteFts.Parameters.AddWithValue("$document_id", documentId);
            deleteFts.ExecuteNonQuery();
        }

        using var deleteChunks = connection.CreateCommand();
        deleteChunks.CommandText = "DELETE FROM file_chunks WHERE document_id = $document_id;";
        deleteChunks.Parameters.AddWithValue("$document_id", documentId);
        deleteChunks.ExecuteNonQuery();
    }

    private static string? FindDocumentId(SqliteConnection connection, string file)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM file_documents WHERE absolute_path = $path LIMIT 1;";
        command.Parameters.AddWithValue("$path", file);
        return command.ExecuteScalar() as string;
    }

    private static bool IsDocumentCurrent(
        SqliteConnection connection,
        string file,
        long size,
        DateTime lastModifiedUtc,
        string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT size_bytes, last_modified_utc, sha256
            FROM file_documents
            WHERE absolute_path = $path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", file);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        return reader.GetInt64(0) == size &&
            string.Equals(reader.GetString(1), FormatDate(lastModifiedUtc), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static FileIndexSummary ReadIndexSummary(
        SqliteConnection connection,
        string root,
        bool refreshed,
        bool ftsAvailable)
    {
        var documentCount = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM file_documents WHERE root = $root;", root);
        var chunkCount = ExecuteScalarInt(
            connection,
            """
            SELECT COUNT(*)
            FROM file_chunks c
            JOIN file_documents d ON d.id = c.document_id
            WHERE d.root = $root;
            """,
            root);

        return new FileIndexSummary(
            refreshed ? "refreshed" : "ready",
            root,
            ftsAvailable,
            documentCount,
            chunkCount,
            0,
            0,
            0,
            0,
            DateTimeOffset.UtcNow);
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql, string root)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$root", root);
        var value = command.ExecuteScalar();
        return value is long count ? checked((int)count) : 0;
    }

    private string ResolveRoot(string? requestedRoot)
    {
        var filesRoot = Path.GetFullPath(Path.Combine(paths.DataDirectory, "files"));
        var root = string.IsNullOrWhiteSpace(requestedRoot)
            ? filesRoot
            : requestedRoot.Trim();
        root = Path.IsPathRooted(root)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(filesRoot, root));

        if (!IsWithinRoot(root, filesRoot))
        {
            throw new InferenceException(
                "file_root_not_allowed",
                "files.search can index only the Tomur managed files directory.",
                [
                    $"Use a path under: {filesRoot}",
                    "Register external files into the Tomur files directory before asking the agent to search them."
                ]);
        }

        return root;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static IEnumerable<string> EnumerateCandidateFiles(string root, int maxFiles)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (count >= maxFiles)
            {
                yield break;
            }

            if (IsHiddenOrTemporary(file))
            {
                continue;
            }

            count++;
            yield return Path.GetFullPath(file);
        }
    }

    private static bool IsHiddenOrTemporary(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith(".", StringComparison.Ordinal) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(file);
            return attributes.HasFlag(FileAttributes.Hidden) ||
                attributes.HasFlag(FileAttributes.System);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryReadTextFile(
        string file,
        long maxFileBytes,
        out string text,
        out string sha256)
    {
        text = string.Empty;
        sha256 = string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.LongLength == 0 || bytes.LongLength > maxFileBytes || LooksBinary(bytes))
            {
                return false;
            }

            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            text = Encoding.UTF8.GetString(bytes).Replace("\0", string.Empty);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<FileChunkDraft> CreateChunks(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var chunks = new List<FileChunkDraft>();
        var current = new StringBuilder();
        var startLine = 1;
        var endLine = 1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > MaxChunkChars)
            {
                FlushCurrent();
                var offset = 0;
                while (offset < line.Length)
                {
                    var length = Math.Min(MaxChunkChars, line.Length - offset);
                    chunks.Add(new FileChunkDraft(line.Substring(offset, length), i + 1, i + 1));
                    offset += length;
                }

                continue;
            }

            if (current.Length > 0 && current.Length + line.Length + 1 > MaxChunkChars)
            {
                FlushCurrent();
            }

            if (current.Length == 0)
            {
                startLine = i + 1;
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
            endLine = i + 1;
        }

        FlushCurrent();
        return chunks;

        void FlushCurrent()
        {
            var value = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                chunks.Add(new FileChunkDraft(value, startLine, endLine));
            }

            current.Clear();
        }
    }

    private static string BuildFtsQuery(string query)
    {
        var terms = ExtractTerms(query);
        if (terms.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" OR ", terms.Select(static term => $"{term}*"));
    }

    private static IReadOnlyList<string> ExtractTerms(string value)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            Flush();
        }

        Flush();
        return terms
            .Where(static term => term.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            terms.Add(current.Length > 64 ? current.ToString(0, 64) : current.ToString());
            current.Clear();
        }
    }

    private static string BuildContext(IReadOnlyList<FileSearchMatch> matches)
        => string.Join(
            "\n\n",
            matches.Select((match, index) =>
                $"[{index + 1}] {match.RelativePath}:{match.StartLine}-{match.EndLine}\n{match.Text}"));

    private static string ResolveMediaType(string extension)
        => extension.Trim().ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "text/markdown",
            ".json" or ".jsonl" => "application/json",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            _ => "text/plain"
        };

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        throw new InferenceException(
            "invalid_request",
            $"The {fieldName} field is required.",
            [$"Provide {fieldName} before invoking files.search."]);
    }

    private static int NormalizeInt(int? value, int fallback, int min, int max)
        => Math.Clamp(value ?? fallback, min, max);

    private static long NormalizeLong(long? value, long fallback, long min, long max)
        => Math.Clamp(value ?? fallback, min, max);

    private static string FormatDate(DateTime value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record FileChunkDraft(string Text, int StartLine, int EndLine);
}

public sealed record FileSearchToolArguments(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("root")] string? Root,
    [property: JsonPropertyName("top_k")] int? TopK,
    [property: JsonPropertyName("refresh")] bool? Refresh,
    [property: JsonPropertyName("max_files")] int? MaxFiles,
    [property: JsonPropertyName("max_file_bytes")] long? MaxFileBytes);

public sealed record FileSearchToolResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("top_k")] int TopK,
    [property: JsonPropertyName("match_count")] int MatchCount,
    [property: JsonPropertyName("index")] FileIndexSummary Index,
    [property: JsonPropertyName("matches")] IReadOnlyList<FileSearchMatch> Matches,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

public sealed record FileIndexSummary(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("fts_available")] bool FtsAvailable,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("scanned_files")] int ScannedFiles,
    [property: JsonPropertyName("indexed_files")] int IndexedFiles,
    [property: JsonPropertyName("skipped_files")] int SkippedFiles,
    [property: JsonPropertyName("updated_files")] int UpdatedFiles,
    [property: JsonPropertyName("refreshed_at")] DateTimeOffset RefreshedAt);

public sealed record FileSearchMatch(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("score")] double? Score);
