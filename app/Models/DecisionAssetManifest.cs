using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tomur.Models;

/// <summary>Provisional, versioned declaration of the files required by a managed decision model.</summary>
public sealed record DecisionAssetManifest(
    int SchemaVersion,
    string Provider,
    string Architecture,
    string ModelRevision,
    DecisionAssetLicense License,
    IReadOnlyList<DecisionAssetFile> Files);

public sealed record DecisionAssetLicense(string Status, string? ReviewStatus, string? Identifier);

public sealed record DecisionAssetFile(
    string Role,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record DecisionAssetDiagnostic(string Code, string Message, string? RelativePath = null);

public sealed record DecisionAssetVerificationResult(
    bool IsValid,
    IReadOnlyList<DecisionAssetDiagnostic> Diagnostics);

public sealed record DecisionAssetVerificationOptions(
    int MaximumFileCount = 256,
    long MaximumFileBytes = 2L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 8L * 1024 * 1024 * 1024,
    TimeSpan? MaximumDuration = null)
{
    public TimeSpan EffectiveMaximumDuration => MaximumDuration ?? TimeSpan.FromSeconds(30);
}

public static class DecisionAssetManifestReader
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumFiles = 256;
    private const int MaximumPathLength = 512;
    private static readonly string[] RequiredRoles = ["tensor", "tokenizer", "schema", "head", "calibration"];

    public static bool TryRead(
        string path,
        out DecisionAssetManifest? manifest,
        out IReadOnlyList<DecisionAssetDiagnostic> diagnostics)
    {
        manifest = null;
        var errors = new List<DecisionAssetDiagnostic>();
        diagnostics = errors;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                errors.Add(new("manifest.missing", "Decision asset manifest was not found."));
                return false;
            }

            if (info.Length is <= 0 or > MaximumManifestBytes)
            {
                errors.Add(new("manifest.size", $"Manifest must be between 1 and {MaximumManifestBytes} bytes."));
                return false;
            }

            using var stream = File.OpenRead(info.FullName);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new("manifest.root", "Manifest root must be a JSON object."));
                return false;
            }

            if (!TryInt(root, "schema_version", out var schemaVersion) || schemaVersion != 1)
                errors.Add(new("manifest.schema", "schema_version must be 1."));
            if (!TryString(root, "provider", out var provider) || !string.Equals(provider, "managed-decision", StringComparison.Ordinal))
                errors.Add(new("manifest.provider", "provider must be managed-decision."));
            if (!TryString(root, "architecture", out var architecture))
                errors.Add(new("manifest.architecture", "architecture must be a non-empty string."));
            if (!TryString(root, "model_revision", out var revision))
                errors.Add(new("manifest.revision", "model_revision must be a non-empty string."));

            var license = ParseLicense(root, errors);
            var files = ParseFiles(root, errors);
            if (errors.Count > 0)
                return false;

            manifest = new DecisionAssetManifest(schemaVersion, provider, architecture, revision, license!, files);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            errors.Add(new("manifest.read", $"Manifest could not be read: {exception.Message}"));
            return false;
        }
    }

    private static DecisionAssetLicense? ParseLicense(JsonElement root, List<DecisionAssetDiagnostic> errors)
    {
        if (!root.TryGetProperty("license", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("manifest.license", "license must be an object with status and review_status."));
            return null;
        }

        var status = TryString(value, "status", out var licenseStatus) ? licenseStatus : string.Empty;
        var review = TryOptionalString(value, "review_status", out var reviewStatus) ? reviewStatus : null;
        var identifier = TryOptionalString(value, "identifier", out var id) ? id : null;
        if (status is not ("approved" or "pending" or "rejected"))
            errors.Add(new("manifest.license_status", "license.status must be approved, pending, or rejected."));
        if (string.IsNullOrWhiteSpace(review))
            errors.Add(new("manifest.review_status", "license.review_status must be a non-empty string."));
        return new DecisionAssetLicense(status, review, identifier);
    }

    private static IReadOnlyList<DecisionAssetFile> ParseFiles(JsonElement root, List<DecisionAssetDiagnostic> errors)
    {
        var files = new List<DecisionAssetFile>();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("files", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("manifest.files", "files must be an array."));
            return files;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (files.Count >= MaximumFiles)
            {
                errors.Add(new("manifest.file_count", $"A manifest may declare at most {MaximumFiles} files."));
                break;
            }

            if (item.ValueKind != JsonValueKind.Object || !TryString(item, "role", out var role) ||
                !TryString(item, "path", out var relativePath) || !TryInt64(item, "length", out var length) ||
                !TryString(item, "sha256", out var sha256))
            {
                errors.Add(new("manifest.file_entry", "Each file requires role, path, length, and sha256."));
                continue;
            }

            role = role.ToLowerInvariant();
            if (!RequiredRoles.Contains(role, StringComparer.Ordinal))
                errors.Add(new("manifest.file_role", $"Unsupported asset role '{role}'.", relativePath));
            if (role != "tensor" && !roles.Add(role))
                errors.Add(new("manifest.duplicate_role", $"Asset role '{role}' may only occur once.", relativePath));
            if (length < 0)
                errors.Add(new("manifest.file_length", "Asset length cannot be negative.", relativePath));
            if (!IsSafeRelativePath(relativePath))
                errors.Add(new("manifest.file_path", "Asset path must be a safe relative file path.", relativePath));
            if (!IsSha256(sha256))
                errors.Add(new("manifest.file_hash", "sha256 must be a 64-character hexadecimal digest.", relativePath));
            if (!paths.Add(relativePath))
                errors.Add(new("manifest.duplicate_path", "Asset paths must be unique case-insensitively.", relativePath));
            files.Add(new DecisionAssetFile(role, relativePath, length, sha256.ToLowerInvariant()));
        }

        foreach (var required in RequiredRoles)
        {
            if (!files.Any(file => string.Equals(file.Role, required, StringComparison.Ordinal)))
                errors.Add(new("manifest.missing_role", $"Required asset role '{required}' is missing."));
        }

        if (files.Count == 0)
            errors.Add(new("manifest.files_empty", "At least one asset file is required."));
        return files;
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        return parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()) && (value = property.GetString()!.Trim()).Length > 0;
    }

    private static bool TryOptionalString(JsonElement parent, string name, out string? value)
    {
        value = parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() : null;
        return value is not null;
    }

    private static bool TryInt(JsonElement parent, string name, out int value) =>
        parent.TryGetProperty(name, out var property) && property.TryGetInt32(out value);

    private static bool TryInt64(JsonElement parent, string name, out long value) =>
        parent.TryGetProperty(name, out var property) && property.TryGetInt64(out value);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || Path.IsPathRooted(path) ||
            path.Contains(':') || path.EndsWith('/') || path.EndsWith('\\'))
            return false;
        return path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(static segment => segment is not ("." or ".."));
    }
}

public static class DecisionAssetVerifier
{
    public static DecisionAssetVerificationResult Verify(
        DecisionAssetManifest manifest,
        string modelDirectory,
        DecisionAssetVerificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DecisionAssetVerificationOptions();
        var diagnostics = new List<DecisionAssetDiagnostic>();
        var deadline = DateTimeOffset.UtcNow + options.EffectiveMaximumDuration;

        if (options.MaximumFileCount <= 0 || options.MaximumFileBytes <= 0 || options.MaximumTotalBytes <= 0 ||
            options.EffectiveMaximumDuration <= TimeSpan.Zero)
        {
            diagnostics.Add(new("verify.options", "Verifier limits must be positive."));
            return new(false, diagnostics);
        }

        if (!string.Equals(manifest.License.Status, "approved", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("license.unapproved", "Asset license is not approved."));
        if (!string.Equals(manifest.License.ReviewStatus, "approved", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("license.review_pending", "Asset license review_status is not approved."));
        if (manifest.Files.Count > options.MaximumFileCount)
            diagnostics.Add(new("verify.file_count", "Manifest exceeds verifier file-count limit."));
        if (diagnostics.Count > 0)
            return new(false, diagnostics);

        string root;
        try
        {
            root = Path.GetFullPath(modelDirectory);
            if (!Directory.Exists(root) || HasReparsePoint(root))
            {
                diagnostics.Add(new("root.invalid", "Model directory is missing or a reparse point."));
                return new(false, diagnostics);
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("root.invalid", $"Model directory is invalid: {exception.Message}"));
            return new(false, diagnostics);
        }

        long totalBytes = 0;
        foreach (var asset in manifest.Files)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                diagnostics.Add(new("verify.timeout", "Asset verification exceeded its deadline."));
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath;
            try { fullPath = ResolvePath(root, asset.RelativePath); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("path.unsafe", exception.Message, asset.RelativePath));
                continue;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || HasReparsePoint(fullPath))
                {
                    diagnostics.Add(new("file.missing_or_link", "Asset file is missing or a reparse point.", asset.RelativePath));
                    continue;
                }
                if (info.Length != asset.Length)
                    diagnostics.Add(new("file.length_mismatch", $"Expected {asset.Length} bytes, found {info.Length}.", asset.RelativePath));
                if (asset.Length < 0 || asset.Length > options.MaximumFileBytes || info.Length > options.MaximumFileBytes ||
                    totalBytes > options.MaximumTotalBytes - Math.Min(info.Length, options.MaximumTotalBytes))
                {
                    diagnostics.Add(new("file.size_limit", "Asset exceeds verifier size limits.", asset.RelativePath));
                    continue;
                }

                var hash = HashFile(fullPath, deadline, options, cancellationToken);
                totalBytes += info.Length;
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(asset.Sha256), Convert.FromHexString(hash)))
                    diagnostics.Add(new("file.hash_mismatch", "SHA-256 does not match the manifest.", asset.RelativePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or TimeoutException)
            {
                diagnostics.Add(new("file.read", $"Asset could not be verified: {exception.Message}", asset.RelativePath));
            }
        }

        return new(diagnostics.Count == 0, diagnostics);
    }

    private static string ResolvePath(string root, string relativePath)
    {
        if (!DecisionAssetManifestReader.IsSafeRelativePath(relativePath))
            throw new InvalidDataException("Asset path is not safe.");
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Asset path escapes the model directory.");
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && HasReparsePoint(current))
                throw new IOException("Asset path traverses a reparse point.");
        }
        return candidate;
    }

    private static string HashFile(string path, DateTimeOffset deadline, DecisionAssetVerificationOptions options, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException("Asset verification exceeded its deadline.");
                hasher.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool HasReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
