using Tomur.Config;
using Tomur.Native;

namespace Tomur.Inference;

public sealed class LlamaBackendInitializer
{
    private static readonly object BackendGate = new();
    private static bool backendInitialized;

    private readonly LlamaImportResolver importResolver;
    private readonly INativeLibraryResolver libraryResolver;
    private readonly ConfigurationStore? configurationStore;

    public LlamaBackendInitializer(
        LlamaImportResolver importResolver,
        INativeLibraryResolver libraryResolver,
        ConfigurationStore? configurationStore = null)
    {
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
        this.configurationStore = configurationStore;
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
                ConfigureBackendEnvironment();
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

    private void ConfigureBackendEnvironment()
    {
        var configuration = configurationStore?.EnsureConfiguration().Configuration;
        var accelerator = RuntimeAcceleratorConfiguration.Normalize(configuration?.Runtime?.Accelerator);
        var openVinoDevice = ResolveOpenVinoDevice(accelerator);
        if (!string.IsNullOrWhiteSpace(openVinoDevice))
        {
            Environment.SetEnvironmentVariable("GGML_OPENVINO_DEVICE", openVinoDevice);
        }

        if (accelerator.NpuPrefillChunk is { } npuPrefillChunk &&
            IsOpenVinoNpuDevice(openVinoDevice))
        {
            Environment.SetEnvironmentVariable(
                "GGML_OPENVINO_PREFILL_CHUNK_SIZE",
                npuPrefillChunk.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? ResolveOpenVinoDevice(RuntimeAcceleratorConfiguration accelerator)
    {
        if (!string.IsNullOrWhiteSpace(accelerator.OpenVinoDevice))
        {
            if (IsOpenVinoNpuDevice(accelerator.OpenVinoDevice) &&
                !accelerator.AllowNpu)
            {
                return null;
            }

            return accelerator.OpenVinoDevice;
        }

        return accelerator.Preference == "openvino" ? "GPU" : null;
    }

    private static bool IsOpenVinoNpuDevice(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
            value.Trim().StartsWith("NPU", StringComparison.OrdinalIgnoreCase);

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
            TryLoadBackendIfPresent("ggml-cuda", resolution.RuntimeRoot);
            TryLoadBackendIfPresent("ggml-vulkan", resolution.RuntimeRoot);
            TryLoadBackendIfPresent("ggml-openvino", resolution.RuntimeRoot);
            TryLoadBackendIfPresent("ggml-sycl", resolution.RuntimeRoot);
            TryLoadBackendIfPresent("ggml-opencl", resolution.RuntimeRoot);
            TryLoadBackendIfPresent("ggml-cann", resolution.RuntimeRoot);
            TryLoadCpuBackend(resolution.RuntimeRoot);
            LlamaNativeMethods.GgmlBackendLoadAllFromPath(resolution.RuntimeRoot);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static void TryLoadBackendIfPresent(string libraryName, string runtimeRoot)
    {
        var path = NativeBundlePaths.ResolveLibraryPath(libraryName, runtimeRoot);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return;
        }

        _ = LlamaNativeMethods.GgmlBackendLoad(path);
    }

    private static void TryLoadCpuBackend(string runtimeRoot)
    {
        var preferredNames = new[]
        {
            "ggml-cpu-alderlake",
            "ggml-cpu-haswell",
            "ggml-cpu-x64",
            "ggml-cpu"
        };

        foreach (var name in preferredNames)
        {
            var path = NativeBundlePaths.ResolveLibraryPath(name, runtimeRoot);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                continue;
            }

            _ = LlamaNativeMethods.GgmlBackendLoad(path);
            return;
        }
    }
}
