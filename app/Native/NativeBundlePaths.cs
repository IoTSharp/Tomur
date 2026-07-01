using System.Runtime.InteropServices;

namespace Tomur.Native;

internal static class NativeBundlePaths
{
    private static readonly string[] WindowsExtensions = [".dll"];
    private static readonly string[] LinuxExtensions = [".so"];
    private static readonly string[] MacExtensions = [".dylib"];

    public static string ResolveRid()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
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

    public static string? ResolveManifestPath()
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

    public static string ResolveSourceRuntimeRoot(string manifestPath, NativeBundleManifest manifest, string rid)
    {
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? AppContext.BaseDirectory;
        var relativeRuntimeRoot = ExpandRuntimeRoot(manifest.RuntimeRoot, rid);
        return Path.GetFullPath(Path.Combine(manifestDirectory, relativeRuntimeRoot));
    }

    public static string ResolveManagedRuntimeRoot(string runtimeDirectory, NativeBundleManifest manifest, string rid)
    {
        var bundleDirectory = Path.Combine(
            Path.GetFullPath(runtimeDirectory),
            SanitizePathSegment(manifest.BundleId),
            SanitizePathSegment(manifest.Version));
        var relativeRuntimeRoot = ExpandRuntimeRoot(manifest.RuntimeRoot, rid);

        return Path.GetFullPath(Path.Combine(bundleDirectory, relativeRuntimeRoot));
    }

    public static string ResolveComponentRoot(string runtimeRoot, NativeBundleComponent component)
        => ResolveComponentRoot(runtimeRoot, component.RuntimePath);

    public static string ResolveComponentRoot(string runtimeRoot, string runtimePath)
    {
        return runtimePath == "."
            ? runtimeRoot
            : Path.GetFullPath(Path.Combine(runtimeRoot, runtimePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static string ResolveLibraryPath(string libraryName, string directory)
    {
        var candidates = BuildLibraryCandidates(libraryName, directory);
        var existingCandidates = candidates
            .Where(File.Exists)
            .ToArray();
        var existingNonEmptyCandidate = existingCandidates
            .FirstOrDefault(static path => new FileInfo(path).Length > 0);
        if (existingNonEmptyCandidate is not null)
        {
            return existingNonEmptyCandidate;
        }

        var versionedUnixLibrary = TryFindVersionedUnixLibrary(libraryName, directory, requireNonEmpty: true)
            ?? TryFindVersionedUnixLibrary(libraryName, directory, requireNonEmpty: false);
        if (versionedUnixLibrary is not null)
        {
            return versionedUnixLibrary;
        }

        return existingCandidates.FirstOrDefault() ?? candidates[0];
    }

    private static string ExpandRuntimeRoot(string runtimeRoot, string rid)
    {
        var expanded = runtimeRoot.Replace("{rid}", rid, StringComparison.OrdinalIgnoreCase);
        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();

        return new string(buffer);
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

    private static string? TryFindVersionedUnixLibrary(string libraryName, string directory, bool requireNonEmpty)
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
            .Where(path => !requireNonEmpty || new FileInfo(path).Length > 0)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
