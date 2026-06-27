using System.Reflection;
using System.Runtime.InteropServices;
using Tomur.Native;

namespace Tomur.Inference;

public sealed class LlamaImportResolver
{
    private static readonly object RegisterLock = new();
    private static bool registered;

    private readonly INativeLibraryResolver resolver;

    public LlamaImportResolver(INativeLibraryResolver resolver)
    {
        this.resolver = resolver;
    }

    public void Register()
    {
        lock (RegisterLock)
        {
            if (registered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(LlamaNativeMethods).Assembly, ResolveImport);
            registered = true;
        }
    }

    private IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var normalized = NormalizeLibraryName(libraryName);
        if (normalized is not "llama" and not "ggml" and not "ggml-base")
        {
            return IntPtr.Zero;
        }

        var resolution = resolver.Resolve("llama", normalized);
        if (!resolution.Exists || resolution.ChecksumStatus == "mismatch")
        {
            return IntPtr.Zero;
        }

        NativeLibraryLoader.RegisterSearchPath(resolution.RuntimeRoot);
        NativeLibraryLoader.RegisterSearchPath(resolution.ComponentRuntimePath);

        try
        {
            return NativeLibrary.Load(resolution.Path, assembly, searchPath);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch (BadImageFormatException)
        {
            return IntPtr.Zero;
        }
    }

    private static string NormalizeLibraryName(string libraryName)
    {
        var fileName = Path.GetFileNameWithoutExtension(libraryName);
        if (fileName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[3..];
        }

        return fileName;
    }
}
