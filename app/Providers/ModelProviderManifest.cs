using System.Text.Json;

namespace Tomur.Providers;

public sealed class ModelProviderManifest
{
    public const string FileName = "model.tomur.json";

    internal ModelProviderManifest(
        int schemaVersion,
        string provider,
        string architecture,
        string displayName,
        string configFile,
        string tokenizerFile,
        string tensorPattern,
        string quantization,
        IReadOnlyList<string> capabilities)
    {
        SchemaVersion = schemaVersion;
        Provider = provider;
        Architecture = architecture;
        DisplayName = displayName;
        ConfigFile = configFile;
        TokenizerFile = tokenizerFile;
        TensorPattern = tensorPattern;
        Quantization = quantization;
        Capabilities = capabilities;
    }

    public int SchemaVersion { get; }

    public string Provider { get; }

    public string Architecture { get; }

    public string DisplayName { get; }

    public string ConfigFile { get; }

    public string TokenizerFile { get; }

    public string TensorPattern { get; }

    public string Quantization { get; }

    public IReadOnlyList<string> Capabilities { get; }
}

public static class ModelProviderManifestReader
{
    private const int MaximumManifestBytes = 1024 * 1024;

    public static bool TryRead(
        string path,
        out ModelProviderManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = $"Model provider manifest was not found: {path}";
                return false;
            }

            if (info.Length <= 0 || info.Length > MaximumManifestBytes)
            {
                error = $"Model provider manifest must be between 1 and {MaximumManifestBytes} bytes.";
                return false;
            }

            using var stream = File.OpenRead(info.FullName);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Model provider manifest root must be a JSON object.";
                return false;
            }

            if (!TryGetInt32(root, "schema_version", out var schemaVersion) || schemaVersion != 1)
            {
                error = "Model provider manifest schema_version must be 1.";
                return false;
            }

            if (!TryGetRequiredString(root, "provider", out var provider, out error) ||
                !TryGetRequiredString(root, "architecture", out var architecture, out error))
            {
                return false;
            }

            var displayName = GetOptionalString(root, "display_name") ?? architecture;
            var configFile = GetOptionalString(root, "config") ?? "config.json";
            var tokenizerFile = GetOptionalString(root, "tokenizer") ?? "tokenizer.json";
            var tensorPattern = GetOptionalString(root, "tensor_pattern") ?? "*.safetensors";
            var quantization = GetOptionalString(root, "quantization") ?? "unknown";
            var capabilities = GetStringArray(root, "capabilities");

            if (!IsSafeRelativeFile(configFile) || !IsSafeRelativeFile(tokenizerFile))
            {
                error = "Model provider manifest config and tokenizer values must be relative files inside the model directory.";
                return false;
            }

            if (!IsSafeSearchPattern(tensorPattern))
            {
                error = "Model provider manifest tensor_pattern must be a file-name search pattern without directory traversal.";
                return false;
            }

            if (capabilities.Count == 0)
            {
                error = "Model provider manifest capabilities must contain at least one capability.";
                return false;
            }

            manifest = new ModelProviderManifest(
                schemaVersion,
                provider,
                architecture,
                displayName,
                configFile,
                tokenizerFile,
                tensorPattern,
                quantization,
                capabilities);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Model provider manifest could not be read: {exception.Message}";
            return false;
        }
    }

    public static string ResolveAssetPath(string modelDirectory, string relativeFile)
    {
        if (!IsSafeRelativeFile(relativeFile))
        {
            throw new InvalidDataException($"Model asset path must stay inside the model directory: {relativeFile}");
        }

        var root = Path.GetFullPath(modelDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativeFile));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Model asset path escapes the model directory: {relativeFile}");
        }

        return candidate;
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string value,
        out string? error)
    {
        value = GetOptionalString(root, propertyName) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return true;
        }

        error = $"Model provider manifest {propertyName} must be a non-empty string.";
        return false;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSafeRelativeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('*') ||
            path.Contains('?'))
        {
            return false;
        }

        return !path
            .Replace((char)92, '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == "..");
    }

    private static bool IsSafeSearchPattern(string pattern)
        => !string.IsNullOrWhiteSpace(pattern) &&
            string.Equals(Path.GetFileName(pattern), pattern, StringComparison.Ordinal) &&
            !pattern.Contains("..", StringComparison.Ordinal);
}
