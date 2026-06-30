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
        var componentId = ResolveComponentId(normalized);
        if (componentId is null)
        {
            return IntPtr.Zero;
        }

        var resolution = resolver.Resolve(componentId, normalized);
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

    private static string? ResolveComponentId(string libraryName)
    {
        return libraryName switch
        {
            "llama" or "ggml" or "ggml-base" or "ggml-cpu" or "ggml-cuda" or "ggml-cann" or "ggml-metal" or
            "ggml-vulkan" or "ggml-sycl" or "ggml-openvino" or "ggml-opencl" or "tomur-llama-mtmd" or "tomur-llama-vlm" => "llama",
            "whisper" or "parakeet" => "whisper",
            "tomur-ocr" or "tomur-mtmd" => "ocr",
            "tomur-tts" => "tts",
            "stable-diffusion" => "stable-diffusion",
            _ => null
        };
    }
}
