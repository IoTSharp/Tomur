using Tomur.Config;

namespace Tomur.Native;

public sealed class NativeLibraryResolver(INativeBundleProbe probe) : INativeLibraryResolver
{
    public NativeLibraryResolver(DataPaths paths)
        : this(new NativeBundleProbe(paths))
    {
    }

    public NativeLibraryResolution Resolve(string componentId, string libraryName)
        => Resolve(componentId, libraryName, variantId: null);

    public NativeLibraryResolution Resolve(string componentId, string libraryName, string? variantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);

        var result = probe.Probe();
        var component = result.Components.FirstOrDefault(item =>
            string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            return new NativeLibraryResolution(
                componentId,
                libraryName,
                result.Rid,
                null,
                string.Empty,
                result.RuntimeRoot,
                string.Empty,
                false,
                null,
                null,
                null,
                "missing",
                "missing",
                $"Native component '{componentId}' is not declared in the bundle manifest.");
        }

        var library = component.Libraries.FirstOrDefault(item =>
            string.Equals(item.Name, libraryName, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return new NativeLibraryResolution(
                component.Id,
                libraryName,
                result.Rid,
                component.SelectedVariant,
                string.Empty,
                result.RuntimeRoot,
                component.RuntimePath,
                false,
                null,
                null,
                null,
                "missing",
                component.Status,
                $"Native library '{libraryName}' is not declared for component '{component.Id}'.");
        }

        if (!string.IsNullOrWhiteSpace(variantId) &&
            !string.Equals(component.SelectedVariant, variantId, StringComparison.OrdinalIgnoreCase))
        {
            var variantResolution = TryResolveVariant(result, component.Id, libraryName, variantId.Trim());
            if (variantResolution is not null)
            {
                return variantResolution;
            }
        }

        return new NativeLibraryResolution(
            component.Id,
            library.Name,
            result.Rid,
            component.SelectedVariant,
            library.Path,
            result.RuntimeRoot,
            component.RuntimePath,
            library.Exists,
            library.SizeBytes,
            library.Sha256,
            library.ExpectedSha256,
            library.ChecksumStatus,
            component.Status,
            library.Exists && library.ChecksumStatus != "mismatch"
                ? "Native library was resolved from the managed runtime directory."
                : library.ChecksumStatus == "mismatch"
                    ? "Native library is present but failed checksum verification; run tomur native prepare."
                    : "Native library is declared but not present in the managed runtime directory.");
    }

    private static NativeLibraryResolution? TryResolveVariant(
        NativeBundleProbeResult result,
        string componentId,
        string libraryName,
        string variantId)
    {
        var manifest = TryReadManifest(result.ManifestPath);
        var component = manifest?.Components.FirstOrDefault(item =>
            string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
        var variant = component?.Variants?.FirstOrDefault(item =>
            string.Equals(item.Id, variantId, StringComparison.OrdinalIgnoreCase));
        if (component is null || variant is null)
        {
            return null;
        }

        if (variant.RequiredBackends.Any(requiredBackend =>
            !RuntimeBackendReady(result.RuntimeRoot, requiredBackend)))
        {
            return null;
        }

        if (component.SharedDependencies.Any(sharedDependency =>
            !RuntimeBackendReady(result.RuntimeRoot, sharedDependency)))
        {
            return null;
        }

        var libraries = variant.Libraries ?? component.Libraries;
        var library = libraries.FirstOrDefault(item =>
            string.Equals(item.Name, libraryName, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return null;
        }

        var runtimePath = NativeBundlePaths.ResolveComponentRoot(result.RuntimeRoot, variant.RuntimePath);
        var path = NativeBundlePaths.ResolveLibraryPath(library.Name, runtimePath);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        var info = new FileInfo(path);
        var sha256 = ComputeSha256(path);
        var expectedSha256 = library.Sha256;
        var checksumStatus = string.IsNullOrWhiteSpace(expectedSha256)
            ? "unverified"
            : string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? "ok"
                : "mismatch";
        if (checksumStatus == "mismatch")
        {
            return null;
        }

        return new NativeLibraryResolution(
            component.Id,
            library.Name,
            result.Rid,
            variant.Id,
            path,
            result.RuntimeRoot,
            runtimePath,
            true,
            info.Length,
            sha256,
            expectedSha256,
            checksumStatus,
            "ok",
            $"Native library was resolved from variant '{variant.Id}'.");
    }

    private static NativeBundleManifest? TryReadManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return System.Text.Json.JsonSerializer.Deserialize(
                stream,
                Tomur.Serialization.AppJsonSerializerContext.Default.NativeBundleManifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool RuntimeBackendReady(string runtimeRoot, string libraryName)
    {
        var path = NativeBundlePaths.ResolveLibraryPath(libraryName, runtimeRoot);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }
}
