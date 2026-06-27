namespace Tomur.Native;

public interface INativeLibraryLoader
{
    NativeLibraryLoadResult Load(string componentId, string libraryName);
}
