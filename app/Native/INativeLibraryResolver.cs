namespace Tomur.Native;

public interface INativeLibraryResolver
{
    NativeLibraryResolution Resolve(string componentId, string libraryName);

    NativeLibraryResolution Resolve(string componentId, string libraryName, string? variantId);
}
