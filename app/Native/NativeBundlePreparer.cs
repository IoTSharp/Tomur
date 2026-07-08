using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Config;
using Tomur.Serialization;

namespace Tomur.Native;

public sealed class NativeBundlePreparer : INativeBundlePreparer
{
    private readonly DataPaths paths;
    private readonly ILogger<NativeBundlePreparer> logger;

    public NativeBundlePreparer(DataPaths paths, ILogger<NativeBundlePreparer>? logger = null)
    {
        this.paths = paths;
        this.logger = logger ?? NullLogger<NativeBundlePreparer>.Instance;
    }

    public NativeBundlePrepareResult Prepare()
        => Prepare(paths.RuntimeDirectory);

    public NativeBundlePrepareResult Prepare(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);

        var manifestPath = NativeBundlePaths.ResolveManifestPath();
        if (manifestPath is null)
        {
            return Error(
                NativeBundlePaths.ResolveRid(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Path.GetFullPath(runtimeDirectory),
                "Native bundle manifest was not found.");
        }

        NativeBundleManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.NativeBundleManifest);
        }
        catch (IOException exception)
        {
            logger.BundleManifestInvalid(exception.Message);
            return Error(
                NativeBundlePaths.ResolveRid(),
                string.Empty,
                string.Empty,
                manifestPath,
                string.Empty,
                Path.GetFullPath(runtimeDirectory),
                $"Native bundle manifest could not be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            logger.BundleManifestInvalid(exception.Message);
            return Error(
                NativeBundlePaths.ResolveRid(),
                string.Empty,
                string.Empty,
                manifestPath,
                string.Empty,
                Path.GetFullPath(runtimeDirectory),
                $"Native bundle manifest is invalid JSON: {exception.Message}");
        }

        if (manifest is null)
        {
            return Error(
                NativeBundlePaths.ResolveRid(),
                string.Empty,
                string.Empty,
                manifestPath,
                string.Empty,
                Path.GetFullPath(runtimeDirectory),
                "Native bundle manifest is empty.");
        }

        var rid = NativeBundlePaths.ResolveRid();
        var sourceRuntimeRoot = NativeBundlePaths.ResolveSourceRuntimeRoot(manifestPath, manifest, rid);
        var managedRuntimeRoot = NativeBundlePaths.ResolveManagedRuntimeRoot(runtimeDirectory, manifest, rid);

        if (!Directory.Exists(sourceRuntimeRoot))
        {
            return Error(
                rid,
                manifest.BundleId,
                manifest.Version,
                manifestPath,
                sourceRuntimeRoot,
                managedRuntimeRoot,
                $"Native bundle source runtime directory was not found for RID '{rid}'.");
        }

        var files = new List<NativeBundleFilePrepareResult>();
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRuntimeRoot, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            if (TryResolveZeroByteNativeAlias(sourcePath, out var aliasTargetPath))
            {
                files.Add(PrepareFile(
                    sourceRuntimeRoot,
                    managedRuntimeRoot,
                    sourcePath,
                    aliasTargetPath,
                    "aliased",
                    "Zero-byte native library alias was materialized from its versioned library."));
                continue;
            }

            files.Add(PrepareFile(
                sourceRuntimeRoot,
                managedRuntimeRoot,
                sourcePath,
                sourcePath,
                copiedStatus: "copied",
                copiedMessage: "Managed runtime file was copied from the native bundle."));
        }

        var status = files.Any(static file => file.Status == "error")
            ? "error"
            : files.Any(static file => file.Status is "copied" or "repaired" or "aliased")
                ? "prepared"
                : "ok";
        var message = status switch
        {
            "ok" => "Native runtime bundle is already prepared.",
            "prepared" => "Native runtime bundle was prepared or repaired.",
            _ => "Native runtime bundle could not be fully prepared."
        };

        var changedFiles = files.Count(static file =>
            file.Status is "copied" or "repaired" or "aliased" or "error");
        logger.BundlePrepared(changedFiles, status);

        return new NativeBundlePrepareResult(
            status,
            DateTimeOffset.UtcNow,
            rid,
            manifest.BundleId,
            manifest.Version,
            manifestPath,
            sourceRuntimeRoot,
            managedRuntimeRoot,
            files,
            message);
    }

    private NativeBundleFilePrepareResult PrepareFile(
        string sourceRuntimeRoot,
        string managedRuntimeRoot,
        string sourcePath,
        string copySourcePath,
        string copiedStatus,
        string copiedMessage)
    {
        var destinationPath = ResolveDestinationPath(sourceRuntimeRoot, managedRuntimeRoot, sourcePath);
        try
        {
            var sourceInfo = new FileInfo(copySourcePath);
            var sourceSha256 = ComputeSha256(copySourcePath);

            if (File.Exists(destinationPath))
            {
                var destinationInfo = new FileInfo(destinationPath);
                if (destinationInfo.Length == sourceInfo.Length &&
                    string.Equals(ComputeSha256(destinationPath), sourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new NativeBundleFilePrepareResult(
                        sourcePath,
                        destinationPath,
                        "unchanged",
                        sourceInfo.Length,
                        sourceSha256,
                        "Managed runtime file is already up to date.");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var status = File.Exists(destinationPath) ? "repaired" : copiedStatus;
            File.Copy(copySourcePath, destinationPath, overwrite: true);

            return new NativeBundleFilePrepareResult(
                sourcePath,
                destinationPath,
                status,
                sourceInfo.Length,
                sourceSha256,
                status == copiedStatus
                    ? copiedMessage
                    : "Managed runtime file was replaced because it was missing, stale, or damaged.");
        }
        catch (IOException exception)
        {
            return PrepareError(sourcePath, destinationPath, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return PrepareError(sourcePath, destinationPath, exception.Message);
        }
    }

    private NativeBundleFilePrepareResult PrepareError(string sourcePath, string destinationPath, string message)
    {
        logger.BundleFilePrepareFailed(destinationPath, message);
        return new NativeBundleFilePrepareResult(
            sourcePath,
            destinationPath,
            "error",
            File.Exists(sourcePath) ? new FileInfo(sourcePath).Length : null,
            null,
            $"Managed runtime file could not be prepared: {message}");
    }

    private static NativeBundlePrepareResult Error(
        string rid,
        string bundleId,
        string version,
        string manifestPath,
        string sourceRuntimeRoot,
        string runtimeRoot,
        string message)
    {
        return new NativeBundlePrepareResult(
            "error",
            DateTimeOffset.UtcNow,
            rid,
            bundleId,
            version,
            manifestPath,
            sourceRuntimeRoot,
            runtimeRoot,
            Array.Empty<NativeBundleFilePrepareResult>(),
            message);
    }

    private static string ResolveDestinationPath(string sourceRuntimeRoot, string managedRuntimeRoot, string sourcePath)
    {
        var relativePath = Path.GetRelativePath(sourceRuntimeRoot, sourcePath);
        return Path.GetFullPath(Path.Combine(managedRuntimeRoot, relativePath));
    }

    private static bool TryResolveZeroByteNativeAlias(string sourcePath, out string aliasTargetPath)
    {
        aliasTargetPath = string.Empty;

        var fileInfo = new FileInfo(sourcePath);
        if (fileInfo.Length != 0)
        {
            return false;
        }

        var fileName = fileInfo.Name;
        var extension = fileInfo.Extension;
        var isNativeLibrary = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".dylib.", StringComparison.OrdinalIgnoreCase);
        if (!isNativeLibrary || fileInfo.DirectoryName is null)
        {
            return false;
        }

        aliasTargetPath = Directory.EnumerateFiles(
                fileInfo.DirectoryName,
                $"{fileInfo.Name}.*",
                SearchOption.TopDirectoryOnly)
            .Where(path => new FileInfo(path).Length > 0)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;

        return aliasTargetPath.Length > 0;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
