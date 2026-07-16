using Tomur.Config;
using Tomur.Models;
using Tomur.Providers;

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
    private readonly ModelProviderRegistry? providerRegistry;
    private readonly ModelCatalog packageCatalog = new();

    public LocalModelCatalog(DataPaths paths)
        : this(paths, null)
    {
    }

    public LocalModelCatalog(DataPaths paths, ModelProviderRegistry? providerRegistry)
    {
        this.paths = paths;
        this.providerRegistry = providerRegistry;
    }

    public IReadOnlyList<LocalModelDescriptor> ListModels()
        => ListModelCandidates()
            .Where(IsVisibleModel)
            .ToArray();

    public IReadOnlyList<LocalModelDescriptor> ListModelCandidates()
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

        var providerModelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerManifestPath in EnumerateModelFiles(ModelProviderManifest.FileName))
        {
            var relativePath = BuildRelativePath(providerManifestPath);
            if (relativePath.StartsWith($"{Defaults.DownloadCacheDirectoryName}/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            providerModelDirectories.Add(Path.GetDirectoryName(providerManifestPath)!);
            var descriptor = TryCreateProviderDescriptor(providerManifestPath);
            if (descriptor is null)
            {
                continue;
            }

            descriptors.Add(descriptor);
        }

        foreach (var file in EnumerateModelFiles("*"))
        {
            if (string.Equals(Path.GetFileName(file), ModelProviderManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                providerModelDirectories.Any(directory => IsWithinDirectory(file, directory)))
            {
                continue;
            }

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

    private bool IsVisibleModel(LocalModelDescriptor model)
    {
        if (!string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (providerRegistry is not null)
        {
            var readiness = providerRegistry.InspectModel(model);
            return readiness.MetadataValid && readiness.AssetsComplete;
        }

        if (!ModelProviderManifestReader.TryRead(model.AbsolutePath, out var manifest, out _) || manifest is null)
        {
            return false;
        }

        try
        {
            var modelDirectory = Path.GetDirectoryName(model.AbsolutePath)!;
            var configPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile);
            var tokenizerPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile);
            return File.Exists(configPath) &&
                File.Exists(tokenizerPath) &&
                Directory.EnumerateFiles(
                    modelDirectory,
                    manifest.TensorPattern,
                    SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private LocalModelDescriptor? TryCreateProviderDescriptor(string manifestPath)
    {
        try
        {
            if (!ModelProviderManifestReader.TryRead(manifestPath, out var manifest, out _) || manifest is null)
            {
                return null;
            }

            var modelDirectory = Path.GetDirectoryName(manifestPath)!;
            var relativeDirectory = Path.GetRelativePath(paths.ModelsDirectory, modelDirectory).Replace('\\', '/');
            var id = NormalizeModelId(relativeDirectory == "." ? manifest.Provider : relativeDirectory);
            var assets = new List<string>
            {
                manifestPath,
                ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile),
                ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile)
            };
            assets.AddRange(Directory.EnumerateFiles(modelDirectory, manifest.TensorPattern, SearchOption.TopDirectoryOnly));

            var existingAssets = assets
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(static path => new FileInfo(path))
                .Where(static info => info.Exists)
                .ToArray();
            var sizeBytes = existingAssets.Sum(static info => info.Length);
            var lastModified = existingAssets.Length == 0
                ? File.GetLastWriteTimeUtc(manifestPath)
                : existingAssets.Max(static info => info.LastWriteTimeUtc);

            return new LocalModelDescriptor(
                id,
                manifest.DisplayName,
                Path.GetFileName(manifestPath),
                BuildRelativePath(manifestPath),
                Path.GetFullPath(manifestPath),
                sizeBytes,
                lastModified,
                "managed-model",
                manifest.Architecture,
                manifest.Quantization,
                manifest.Capabilities);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
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

        if (string.Equals(info.Name, ModelProviderManifest.FileName, StringComparison.OrdinalIgnoreCase))
        {
            var providerDescriptor = TryCreateProviderDescriptor(info.FullName);
            if (providerDescriptor is null)
            {
                return null;
            }

            var providerPrimaryAsset = installedPackage.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Path, installedPackage.PrimaryPath, StringComparison.OrdinalIgnoreCase));
            return providerDescriptor with
            {
                Id = installedPackage.ModelKey,
                Name = installedPackage.DisplayName,
                PackageId = installedPackage.Id,
                Status = installedPackage.Status,
                License = installedPackage.License,
                LicenseNotice = installedPackage.LicenseNotice,
                IsVerified = providerPrimaryAsset?.Sha256Verified == true
            };
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

    private IReadOnlyList<string> EnumerateModelFiles(string pattern)
    {
        try
        {
            return Directory
                .EnumerateFiles(paths.ModelsDirectory, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                })
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string NormalizeModelId(string value)
    {
        return value.Trim().Replace('\\', '/');
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
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
                "vision" => ["completion", "chat", "vision", "ocr"],
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
