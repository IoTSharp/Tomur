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
        string quantizationLayout,
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
        QuantizationLayout = quantizationLayout;
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

    public string QuantizationLayout { get; }

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

            if (!TryGetOptionalString(root, "display_name", architecture, out var displayName, out error) ||
                !TryGetOptionalString(root, "config", "config.json", out var configFile, out error) ||
                !TryGetOptionalString(root, "tokenizer", "tokenizer.json", out var tokenizerFile, out error) ||
                !TryGetOptionalString(root, "tensor_pattern", "*.safetensors", out var tensorPattern, out error) ||
                !TryGetOptionalString(root, "quantization", "unknown", out var quantization, out error) ||
                !TryGetOptionalString(root, "quantization_layout", "separate-scales", out var quantizationLayout, out error) ||
                !TryGetStringArray(root, "capabilities", out var capabilities, out error))
            {
                return false;
            }

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
                quantizationLayout,
                capabilities);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
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

    private static bool TryGetOptionalString(
        JsonElement root,
        string propertyName,
        string fallback,
        out string value,
        out string? error)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            value = fallback;
            error = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = string.Empty;
            error = $"Model provider manifest {propertyName} must be a non-empty string when present.";
            return false;
        }

        value = property.GetString()!.Trim();
        error = null;
        return true;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryGetStringArray(
        JsonElement root,
        string propertyName,
        out IReadOnlyList<string> values,
        out string? error)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            values = [];
            error = $"Model provider manifest {propertyName} must be an array of non-empty strings.";
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                values = [];
                error = $"Model provider manifest {propertyName} must contain only non-empty strings.";
                return false;
            }

            var value = item.GetString()!.Trim();
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        values = result;
        error = null;
        return true;
    }

    private static bool IsSafeRelativeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.EndsWith('/') ||
            path.EndsWith((char)92) ||
            path.Contains('*') ||
            path.Contains('?'))
        {
            return false;
        }

        return !path
            .Replace((char)92, '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }

    private static bool IsSafeSearchPattern(string pattern)
        => !string.IsNullOrWhiteSpace(pattern) &&
            string.Equals(Path.GetFileName(pattern), pattern, StringComparison.Ordinal) &&
            !pattern.Contains("..", StringComparison.Ordinal);
}
