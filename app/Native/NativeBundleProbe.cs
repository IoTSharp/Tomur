using System.Security.Cryptography;
using System.Text.Json;
using Tomur.Config;
using Tomur.Serialization;

namespace Tomur.Native;

public sealed class NativeBundleProbe : INativeBundleProbe
{
    private readonly DataPaths paths;

    public NativeBundleProbe(DataPaths paths)
    {
        this.paths = paths;
    }

    public NativeBundleProbeResult Probe()
        => Probe(paths.RuntimeDirectory);

    public NativeBundleProbeResult Probe(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);

        var manifestPath = NativeBundlePaths.ResolveManifestPath();
        if (manifestPath is null)
        {
            return new NativeBundleProbeResult(
                "error",
                DateTimeOffset.UtcNow,
                NativeBundlePaths.ResolveRid(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Path.GetFullPath(runtimeDirectory),
                Array.Empty<NativeComponentProbeResult>(),
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
            return ManifestError(manifestPath, $"Native bundle manifest could not be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return ManifestError(manifestPath, $"Native bundle manifest is invalid JSON: {exception.Message}");
        }

        if (manifest is null)
        {
            return ManifestError(manifestPath, "Native bundle manifest is empty.");
        }

        var rid = NativeBundlePaths.ResolveRid();
        var sourceRuntimeRoot = NativeBundlePaths.ResolveSourceRuntimeRoot(manifestPath, manifest, rid);
        var runtimeRoot = NativeBundlePaths.ResolveManagedRuntimeRoot(runtimeDirectory, manifest, rid);
        var components = manifest.Components
            .Select(component => ProbeComponent(component, sourceRuntimeRoot, runtimeRoot))
            .ToArray();
        var status = ResolveStatus(components);
        var message = status switch
        {
            "ok" => "All required native bundle libraries are present.",
            "warning" => "Required native libraries are present, but optional native libraries are missing or unverified.",
            _ => "One or more required native bundle libraries are missing or damaged."
        };

        return new NativeBundleProbeResult(
            status,
            DateTimeOffset.UtcNow,
            rid,
            manifest.BundleId,
            manifest.Version,
            manifestPath,
            sourceRuntimeRoot,
            runtimeRoot,
            components,
            message);
    }

    private NativeBundleProbeResult ManifestError(string manifestPath, string message)
    {
        return new NativeBundleProbeResult(
            "error",
            DateTimeOffset.UtcNow,
            NativeBundlePaths.ResolveRid(),
            string.Empty,
            string.Empty,
            manifestPath,
            string.Empty,
            paths.RuntimeDirectory,
            Array.Empty<NativeComponentProbeResult>(),
            message);
    }

    private static NativeComponentProbeResult ProbeComponent(
        NativeBundleComponent component,
        string sourceRuntimeRoot,
        string runtimeRoot)
    {
        var variants = ResolveVariants(component);
        var probedVariants = variants
            .Select(variant => ProbeVariant(component, variant, sourceRuntimeRoot, runtimeRoot))
            .OrderByDescending(static variant => variant.Variant.Priority)
            .ToArray();
        var selected = probedVariants.FirstOrDefault(static variant => variant.Status == "ok")
            ?? probedVariants.FirstOrDefault(static variant => variant.Status == "warning")
            ?? probedVariants.First();
        var libraries = selected.Libraries;
        var requiredUnverified = libraries.Any(static library =>
            library.Required && library.Exists && library.ChecksumStatus == "unverified");
        var optionalIssue = libraries.Any(static library =>
            !library.Required && library.Exists && library.ChecksumStatus is "mismatch" or "unverified");
        var status = selected.Status == "ok" && (requiredUnverified || optionalIssue)
            ? "warning"
            : selected.Status;
        var message = status switch
        {
            "ok" => selected.Variant.Id == "default"
                ? "Required native libraries are present; optional accelerator libraries may be absent."
                : $"Required native libraries are present for variant '{selected.Variant.Id}'.",
            "warning" => selected.Variant.Id == "default"
                ? "Required native libraries are present, but some present optional libraries are unverified or damaged."
                : $"Required native libraries are present for variant '{selected.Variant.Id}', but some libraries are unverified or damaged.",
            _ => selected.Variant.Id == "default"
                ? "A required native library is missing or failed checksum verification."
                : $"A required native library is missing or damaged for preferred variant '{selected.Variant.Id}'."
        };

        return new NativeComponentProbeResult(
            component.Id,
            component.DisplayName,
            status,
            selected.Variant.Backend,
            selected.ComponentRoot,
            selected.Variant.Id,
            probedVariants
                .Where(static variant => variant.Status is "ok" or "warning")
                .Select(static variant => variant.Variant.Id)
                .ToArray(),
            component.Publisher,
            component.Capabilities,
            component.Source,
            component.WrapperPath,
            component.SharedDependencies,
            libraries,
            message);
    }

    private static IReadOnlyList<NativeBundleVariant> ResolveVariants(NativeBundleComponent component)
    {
        if (component.Variants is { Count: > 0 } variants)
        {
            return variants;
        }

        return
        [
            new NativeBundleVariant(
                "default",
                component.DisplayName,
                component.Backend,
                component.RuntimePath,
                0,
                [],
                component.Libraries)
        ];
    }

    private static ProbedVariant ProbeVariant(
        NativeBundleComponent component,
        NativeBundleVariant variant,
        string sourceRuntimeRoot,
        string runtimeRoot)
    {
        var sourceComponentRoot = NativeBundlePaths.ResolveComponentRoot(sourceRuntimeRoot, variant.RuntimePath);
        var componentRoot = NativeBundlePaths.ResolveComponentRoot(runtimeRoot, variant.RuntimePath);
        var libraries = (variant.Libraries ?? component.Libraries)
            .Select(library => ProbeLibrary(library, sourceComponentRoot, componentRoot))
            .ToArray();
        var requiredProblem = libraries.Any(static library =>
            library.Required && (!library.Exists || library.ChecksumStatus == "mismatch"));
        var requiredUnverified = libraries.Any(static library =>
            library.Required && library.Exists && library.ChecksumStatus == "unverified");
        var optionalIssue = libraries.Any(static library =>
            !library.Required && library.Exists && library.ChecksumStatus is "mismatch" or "unverified");

        var requiredBackendProblem = variant.RequiredBackends.Any(requiredBackend =>
            !RuntimeBackendCatalogProbe(runtimeRoot, requiredBackend));
        var requiredSharedDependencyProblem = component.SharedDependencies.Any(sharedDependency =>
            !RuntimeBackendCatalogProbe(runtimeRoot, sharedDependency));
        var status = requiredProblem || requiredBackendProblem || requiredSharedDependencyProblem
            ? "error"
            : requiredUnverified || optionalIssue
                ? "warning"
                : "ok";

        return new ProbedVariant(variant, componentRoot, libraries, status);
    }

    private static bool RuntimeBackendCatalogProbe(string runtimeRoot, string libraryName)
    {
        var path = NativeBundlePaths.ResolveLibraryPath(libraryName, runtimeRoot);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static NativeLibraryProbeResult ProbeLibrary(
        NativeBundleLibrary library,
        string sourceComponentRoot,
        string componentRoot)
    {
        var sourcePath = NativeBundlePaths.ResolveLibraryPath(library.Name, sourceComponentRoot);
        var expectedSha256 = library.Sha256;
        if (string.IsNullOrWhiteSpace(expectedSha256) && File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 0)
        {
            expectedSha256 = ComputeSha256(sourcePath);
        }

        var path = NativeBundlePaths.ResolveLibraryPath(library.Name, componentRoot);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return new NativeLibraryProbeResult(
                library.Name,
                library.Required,
                path,
                false,
                File.Exists(path) ? new FileInfo(path).Length : null,
                null,
                expectedSha256,
                "missing");
        }

        var info = new FileInfo(path);
        var sha256 = ComputeSha256(path);
        var checksumStatus = string.IsNullOrWhiteSpace(expectedSha256)
            ? "unverified"
            : string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? "ok"
                : "mismatch";

        return new NativeLibraryProbeResult(
            library.Name,
            library.Required,
            path,
            true,
            info.Length,
            sha256,
            expectedSha256,
            checksumStatus);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveStatus(IReadOnlyList<NativeComponentProbeResult> components)
    {
        if (components.Any(static component => component.Status == "error"))
        {
            return "error";
        }

        if (components.Any(static component => component.Status == "warning"))
        {
            return "warning";
        }

        return "ok";
    }

    private sealed record ProbedVariant(
        NativeBundleVariant Variant,
        string ComponentRoot,
        IReadOnlyList<NativeLibraryProbeResult> Libraries,
        string Status);
}
