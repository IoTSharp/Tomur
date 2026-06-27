using Tomur.Config;

namespace Tomur.Native;

public sealed class NativeLibraryResolver(INativeBundleProbe probe) : INativeLibraryResolver
{
    public NativeLibraryResolver(DataPaths paths)
        : this(new NativeBundleProbe(paths))
    {
    }

    public NativeLibraryResolution Resolve(string componentId, string libraryName)
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
                string.Empty,
                result.RuntimeRoot,
                string.Empty,
                false,
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
                component.RuntimePath,
                result.RuntimeRoot,
                component.RuntimePath,
                false,
                component.Status,
                $"Native library '{libraryName}' is not declared for component '{component.Id}'.");
        }

        return new NativeLibraryResolution(
            component.Id,
            library.Name,
            result.Rid,
            library.Path,
            result.RuntimeRoot,
            component.RuntimePath,
            library.Exists,
            component.Status,
            library.Exists
                ? "Native library was resolved from the managed runtime directory."
                : "Native library is declared but not present in the managed runtime directory.");
    }
}
