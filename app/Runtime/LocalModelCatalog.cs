using Tomur.Config;
using Tomur.Models;

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
    private readonly ModelCatalog packageCatalog = new();

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

        var manifest = new InstallManifestStore(paths).Read();
        var manifestAssetPaths = manifest.Packages
            .SelectMany(static package => package.Assets.Select(static asset => asset.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var descriptors = new List<LocalModelDescriptor>();
        foreach (var package in manifest.Packages)
        {
            var descriptor = TryCreateManifestDescriptor(package);
            if (descriptor is not null)
            {
                descriptors.Add(descriptor);
            }
        }

        foreach (var file in Directory.EnumerateFiles(paths.ModelsDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!SupportedExtensions.Contains(extension))
            {
                continue;
            }

            var relativePath = BuildRelativePath(file);
            if (manifestAssetPaths.Contains(relativePath) ||
                relativePath.StartsWith($"{Defaults.DownloadCacheDirectoryName}/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            descriptors.Add(CreateDescriptor(file));
        }

        return descriptors
            .GroupBy(static descriptor => descriptor.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
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
            string.Equals(descriptor.PackageId, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptor.Name, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(descriptor.FileName, requested, StringComparison.OrdinalIgnoreCase));
    }

    private LocalModelDescriptor? TryCreateManifestDescriptor(InstalledModelPackage installedPackage)
    {
        if (!string.Equals(installedPackage.Status, "installed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var package = packageCatalog.Find(installedPackage.Id);
        var primaryPath = ResolveModelPath(installedPackage.PrimaryPath);
        var info = new FileInfo(primaryPath);
        if (!info.Exists)
        {
            return null;
        }

        var format = package?.Format ?? ResolveFormat(info.Extension);
        var family = package?.Family ?? ResolveFamily(installedPackage.PrimaryPath, format);
        var quantization = package?.Quantization ?? ResolveQuantizationLevel(info.Name);
        var primaryAsset = installedPackage.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Path, installedPackage.PrimaryPath, StringComparison.OrdinalIgnoreCase));

        return new LocalModelDescriptor(
            installedPackage.ModelKey,
            installedPackage.DisplayName,
            Path.GetFileName(primaryPath),
            BuildRelativePath(primaryPath),
            info.FullName,
            info.Length,
            info.LastWriteTimeUtc,
            format,
            family,
            string.IsNullOrWhiteSpace(quantization) ? "unknown" : quantization,
            ResolveCapabilities(package?.Task, family),
            installedPackage.Id,
            installedPackage.Status,
            installedPackage.License,
            installedPackage.LicenseNotice,
            primaryAsset?.Sha256Verified == true);
    }

    private LocalModelDescriptor CreateDescriptor(string file)
    {
        var info = new FileInfo(file);
        var relativePath = BuildRelativePath(file);
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
            ResolveCapabilities(null, family));
    }

    private string ResolveModelPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(paths.ModelsDirectory, normalizedPath));
    }

    private string BuildRelativePath(string file)
    {
        return Path.GetRelativePath(paths.ModelsDirectory, file).Replace('\\', '/');
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

    private static IReadOnlyList<string> ResolveCapabilities(string? task, string family)
    {
        if (!string.IsNullOrWhiteSpace(task))
        {
            return task switch
            {
                "chat" => ["completion", "chat"],
                "translation" => ["completion", "chat", "translation"],
                "embeddings" => ["embedding"],
                "reranking" => ["rerank"],
                "transcription" => ["audio"],
                "speech" => ["audio-output"],
                "vision" => ["completion", "chat", "vision"],
                "image-generation" => ["image"],
                _ => []
            };
        }

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
    IReadOnlyList<string> Capabilities,
    string? PackageId = null,
    string? Status = null,
    string? License = null,
    string? LicenseNotice = null,
    bool IsVerified = false);
