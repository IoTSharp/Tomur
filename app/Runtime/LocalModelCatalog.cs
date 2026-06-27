using Tomur.Config;

namespace Tomur.Runtime;

public sealed class LocalModelCatalog
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gguf",
        ".ggml",
        ".bin",
        ".safetensors",
        ".onnx"
    };

    private readonly DataPaths paths;

    public LocalModelCatalog(DataPaths paths)
    {
        this.paths = paths;
    }

    public IReadOnlyList<LocalModelDescriptor> ListModels()
    {
        if (!Directory.Exists(paths.ModelsDirectory))
        {
            return [];
        }

        var descriptors = new List<LocalModelDescriptor>();

        foreach (var file in Directory.EnumerateFiles(paths.ModelsDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!SupportedExtensions.Contains(extension))
            {
                continue;
            }

            descriptors.Add(CreateDescriptor(file));
        }

        return descriptors
            .OrderBy(static descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public LocalModelDescriptor? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var requested = NormalizeModelId(model);
        return ListModels().FirstOrDefault(descriptor =>
            string.Equals(descriptor.Id, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptor.Name, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptor.FileName, requested, StringComparison.OrdinalIgnoreCase));
    }

    private LocalModelDescriptor CreateDescriptor(string file)
    {
        var info = new FileInfo(file);
        var relativePath = Path.GetRelativePath(paths.ModelsDirectory, file).Replace('\\', '/');
        var id = NormalizeModelId(Path.ChangeExtension(relativePath, null) ?? relativePath);
        var name = Path.GetFileNameWithoutExtension(file);
        var format = ResolveFormat(info.Extension);
        var family = ResolveFamily(relativePath, format);

        return new LocalModelDescriptor(
            id,
            name,
            Path.GetFileName(file),
            relativePath,
            info.FullName,
            info.Length,
            info.LastWriteTimeUtc,
            format,
            family,
            ResolveQuantizationLevel(name),
            ResolveCapabilities(family));
    }

    private static string NormalizeModelId(string value)
    {
        return value.Trim().Replace('\\', '/');
    }

    private static string ResolveFormat(string extension)
    {
        return extension.ToLowerInvariant().TrimStart('.') switch
        {
            "gguf" => "gguf",
            "ggml" => "ggml",
            "bin" => "ggml",
            "onnx" => "onnx",
            "safetensors" => "safetensors",
            _ => "unknown"
        };
    }

    private static string ResolveFamily(string relativePath, string format)
    {
        var path = relativePath.Replace('\\', '/');
        if (path.Contains("embedding", StringComparison.OrdinalIgnoreCase))
        {
            return "embedding";
        }

        if (path.Contains("rerank", StringComparison.OrdinalIgnoreCase))
        {
            return "rerank";
        }

        if (path.Contains("speech/asr", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            return "whisper";
        }

        if (path.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("flux", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("stable-diffusion", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        return format is "gguf" or "ggml" ? "llama" : "unknown";
    }

    private static string ResolveQuantizationLevel(string name)
    {
        string[] quantizationLevels =
        [
            "Q8_0",
            "Q6_K",
            "Q5_K_M",
            "Q5_0",
            "Q4_K_M",
            "Q4_K_S",
            "Q4_0",
            "Q3_K_M",
            "Q2_K",
            "F16",
            "BF16"
        ];

        foreach (var level in quantizationLevels)
        {
            if (name.Contains(level, StringComparison.OrdinalIgnoreCase))
            {
                return level;
            }
        }

        return "unknown";
    }

    private static IReadOnlyList<string> ResolveCapabilities(string family)
    {
        return family switch
        {
            "embedding" => ["embedding"],
            "rerank" => ["rerank"],
            "image" => ["image"],
            "whisper" => ["audio"],
            "llama" => ["completion", "chat"],
            _ => []
        };
    }
}

public sealed record LocalModelDescriptor(
    string Id,
    string Name,
    string FileName,
    string RelativePath,
    string AbsolutePath,
    long SizeBytes,
    DateTime LastModifiedUtc,
    string Format,
    string Family,
    string QuantizationLevel,
    IReadOnlyList<string> Capabilities);
