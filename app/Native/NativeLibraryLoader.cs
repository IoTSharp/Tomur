using System.Runtime.InteropServices;
using Tomur.Config;

namespace Tomur.Native;

public sealed class NativeLibraryLoader(INativeLibraryResolver resolver) : INativeLibraryLoader
{
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private static readonly object SearchPathLock = new();
    private static readonly HashSet<string> RegisteredSearchPaths = new(StringComparer.OrdinalIgnoreCase);

    public NativeLibraryLoader(DataPaths paths)
        : this(new NativeLibraryResolver(paths))
    {
    }

    public NativeLibraryLoadResult Load(string componentId, string libraryName)
    {
        var resolution = resolver.Resolve(componentId, libraryName);
        if (!resolution.Exists)
        {
            return new NativeLibraryLoadResult(
                resolution,
                false,
                null,
                resolution.Message);
        }

        if (resolution.ChecksumStatus == "mismatch")
        {
            return new NativeLibraryLoadResult(
                resolution,
                false,
                null,
                "Native library checksum mismatch; run tomur native prepare before loading.");
        }

        RegisterSearchPath(resolution.RuntimeRoot);
        RegisterSearchPath(resolution.ComponentRuntimePath);

        try
        {
            var handle = LoadNativeLibrary(resolution.Path);
            return new NativeLibraryLoadResult(
                resolution,
                true,
                $"0x{handle.ToInt64():x}",
                "Native library loaded successfully.");
        }
        catch (DllNotFoundException exception)
        {
            return LoadFailed(resolution, exception.Message);
        }
        catch (BadImageFormatException exception)
        {
            return LoadFailed(resolution, exception.Message);
        }
    }

    private static IntPtr LoadNativeLibrary(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeLibrary.Load(path);
        }

        var handle = LoadLibraryEx(
            path,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (handle == IntPtr.Zero)
        {
            throw new DllNotFoundException($"LoadLibraryEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return handle;
    }

    private static NativeLibraryLoadResult LoadFailed(NativeLibraryResolution resolution, string message)
        => new(
            resolution,
            false,
            null,
            $"Native library was found but could not be loaded: {message}");

    private static void RegisterSearchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        lock (SearchPathLock)
        {
            if (!RegisteredSearchPaths.Add(fullPath))
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                _ = AddDllDirectory(fullPath);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string libraryFileName, nint fileHandle, uint flags);
}
