using System.Text.Json;
using Tomur.Config;
using Tomur.Serialization;

namespace Tomur.Models;

public sealed record InstallManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ModelsDirectory,
    IReadOnlyList<InstalledModelPackage> Packages);

public sealed record InstalledModelPackage(
    string Id,
    string ModelKey,
    string DisplayName,
    string Segment,
    string Directory,
    string PrimaryPath,
    string Status,
    string? License,
    string LicenseNotice,
    IReadOnlyList<InstalledModelAsset> Assets,
    IReadOnlyList<ModelBundleAsset> BundleAssets,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record InstalledModelAsset(
    string Path,
    string SourceRepositoryId,
    string SourceRelativePath,
    string? ExpectedSha256,
    string? ActualSha256,
    bool Sha256Verified,
    long? SizeBytes);

public sealed class InstallManifestStore
{
    private const int SchemaVersion = 1;

    private readonly DataPaths paths;

    public InstallManifestStore(DataPaths paths)
    {
        this.paths = paths;
    }

    public string ManifestPath => Path.Combine(paths.ModelsDirectory, Defaults.ModelInstallManifestFileName);

    public InstallManifest Read()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return Empty();
            }

            using var stream = File.OpenRead(ManifestPath);
            return JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.InstallManifest) ?? Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
        catch (IOException)
        {
            return Empty();
        }
        catch (UnauthorizedAccessException)
        {
            return Empty();
        }
    }

    public void Upsert(InstalledModelPackage package)
    {
        var current = Read();
        var packages = current.Packages
            .Where(existing => !string.Equals(existing.Id, package.Id, StringComparison.OrdinalIgnoreCase))
            .Append(package)
            .OrderBy(static item => item.Segment, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Write(current with
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            ModelsDirectory = paths.ModelsDirectory,
            Packages = packages
        });
    }

    private void Write(InstallManifest manifest)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);
        var json = JsonSerializer.Serialize(manifest, AppJsonSerializerContext.Default.InstallManifest);
        File.WriteAllText(ManifestPath, json);
    }

    private InstallManifest Empty()
    {
        return new InstallManifest(SchemaVersion, DateTimeOffset.UtcNow, paths.ModelsDirectory, []);
    }
}
