using System.Security.Cryptography;
using System.Text.Json;
using Tomur.Config;
using Tomur.Serialization;

namespace Tomur.Native;

public sealed class NativeBundleProbe : INativeBundleProbe
{
    private static readonly string[] WindowsExtensions = [".dll"];
    private static readonly string[] LinuxExtensions = [".so"];
    private static readonly string[] MacExtensions = [".dylib"];

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

        var manifestPath = ResolveManifestPath();
        if (manifestPath is null)
        {
            return new NativeBundleProbeResult(
                "error",
                DateTimeOffset.UtcNow,
                ResolveRid(),
                string.Empty,
                string.Empty,
                string.Empty,
                runtimeDirectory,
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

        var rid = ResolveRid();
        var runtimeRoot = Path.Combine(Path.GetFullPath(runtimeDirectory), "runtimes", rid, "native");
        var components = manifest.Components
            .Select(component => ProbeComponent(component, runtimeRoot))
            .ToArray();
        var status = ResolveStatus(components);
        var message = status switch
        {
            "ok" => "All required native bundle libraries are present.",
            "warning" => "Required native libraries are present, but optional native libraries are missing.",
            _ => "One or more required native bundle libraries are missing."
        };

        return new NativeBundleProbeResult(
            status,
            DateTimeOffset.UtcNow,
            rid,
            manifest.BundleId,
            manifest.Version,
            manifestPath,
            runtimeRoot,
            components,
            message);
    }

    private NativeBundleProbeResult ManifestError(string manifestPath, string message)
    {
        return new NativeBundleProbeResult(
            "error",
            DateTimeOffset.UtcNow,
            ResolveRid(),
            string.Empty,
            string.Empty,
            manifestPath,
            paths.RuntimeDirectory,
            Array.Empty<NativeComponentProbeResult>(),
            message);
    }

    private static NativeComponentProbeResult ProbeComponent(NativeBundleComponent component, string runtimeRoot)
    {
        var componentRoot = component.RuntimePath == "."
            ? runtimeRoot
            : Path.Combine(runtimeRoot, component.RuntimePath.Replace('/', Path.DirectorySeparatorChar));
        var libraries = component.Libraries
            .Select(library => ProbeLibrary(library, componentRoot))
            .ToArray();
        var requiredMissing = libraries.Any(static library => library.Required && !library.Exists);
        var optionalMissing = libraries.Any(static library => !library.Required && !library.Exists);
        var status = requiredMissing ? "error" : optionalMissing ? "warning" : "ok";
        var message = status switch
        {
            "ok" => "Required and optional native libraries are present.",
            "warning" => "Required native libraries are present, but optional libraries are missing.",
            _ => "A required native library is missing."
        };

        return new NativeComponentProbeResult(
            component.Id,
            component.DisplayName,
            status,
            component.Backend,
            componentRoot,
            component.Publisher,
            component.Capabilities,
            component.Source,
            component.WrapperPath,
            component.SharedDependencies,
            libraries,
            message);
    }

    private static NativeLibraryProbeResult ProbeLibrary(NativeBundleLibrary library, string componentRoot)
    {
        var candidates = BuildLibraryCandidates(library.Name, componentRoot);
        var path = candidates.FirstOrDefault(File.Exists)
            ?? TryFindVersionedUnixLibrary(library.Name, componentRoot)
            ?? candidates[0];
        if (!File.Exists(path))
        {
            return new NativeLibraryProbeResult(library.Name, library.Required, path, false, null, null);
        }

        var info = new FileInfo(path);
        return new NativeLibraryProbeResult(
            library.Name,
            library.Required,
            path,
            true,
            info.Length,
            ComputeSha256(path));
    }

    private static string[] BuildLibraryCandidates(string libraryName, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return BuildLibraryCandidates(libraryName, directory, WindowsExtensions, prefix: string.Empty);
        }

        if (OperatingSystem.IsMacOS())
        {
            return BuildLibraryCandidates(libraryName, directory, MacExtensions, prefix: "lib");
        }

        return BuildLibraryCandidates(libraryName, directory, LinuxExtensions, prefix: "lib");
    }

    private static string[] BuildLibraryCandidates(
        string libraryName,
        string directory,
        IReadOnlyList<string> extensions,
        string prefix)
    {
        var names = new List<string>(extensions.Count * 2);
        foreach (var extension in extensions)
        {
            names.Add(Path.Combine(directory, $"{prefix}{libraryName}{extension}"));
            if (prefix.Length > 0 && libraryName.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(Path.Combine(directory, $"{libraryName}{extension}"));
            }
        }

        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? TryFindVersionedUnixLibrary(string libraryName, string directory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return null;
        }

        var prefix = "lib";
        var extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        var patterns = new[]
        {
            $"{prefix}{libraryName}{extension}.*",
            libraryName.StartsWith(prefix, StringComparison.Ordinal)
                ? $"{libraryName}{extension}.*"
                : string.Empty
        };

        return patterns
            .Where(static pattern => pattern.Length > 0)
            .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
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

    private static string ResolveRid()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        return $"linux-{architecture}";
    }

    private static string? ResolveManifestPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var manifestPath = Path.Combine(current.FullName, "native", "bundle.manifest.json");
            if (File.Exists(manifestPath))
            {
                return manifestPath;
            }

            current = current.Parent;
        }

        return null;
    }
}
