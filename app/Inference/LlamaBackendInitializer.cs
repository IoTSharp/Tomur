using Tomur.Native;

namespace Tomur.Inference;

public sealed class LlamaBackendInitializer
{
    private static readonly object BackendGate = new();
    private static bool backendInitialized;

    private readonly LlamaImportResolver importResolver;
    private readonly INativeLibraryResolver libraryResolver;

    public LlamaBackendInitializer(LlamaImportResolver importResolver, INativeLibraryResolver libraryResolver)
    {
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
    }

    public void EnsureInitialized()
    {
        importResolver.Register();

        lock (BackendGate)
        {
            if (backendInitialized)
            {
                return;
            }

            try
            {
                TryLoadDynamicBackends();
                LlamaNativeMethods.BackendInit();
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new InferenceException(
                    "native_runtime_unavailable",
                    $"The llama.cpp native runtime could not be initialized: {exception.Message}",
                    [
                        "Run tomur native prepare to extract or repair the managed runtime bundle.",
                        "Run tomur doctor to inspect native runtime status."
                    ],
                    exception);
            }

            backendInitialized = true;
        }
    }

    private void TryLoadDynamicBackends()
    {
        var resolution = libraryResolver.Resolve("llama", "llama");
        if (!resolution.Exists || string.IsNullOrWhiteSpace(resolution.RuntimeRoot))
        {
            return;
        }

        NativeLibraryLoader.RegisterSearchPath(resolution.RuntimeRoot);
        NativeLibraryLoader.RegisterSearchPath(resolution.ComponentRuntimePath);

        try
        {
            LlamaNativeMethods.GgmlBackendLoadAllFromPath(resolution.RuntimeRoot);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }
}
