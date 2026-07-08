using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Config;
using Tomur.Native;

namespace Tomur.Inference;

public sealed class LlamaBackendInitializer
{
    private static readonly object BackendGate = new();
    private static readonly List<nint> RegisteredBackendLibraryHandles = [];
    private static bool backendInitialized;

    private readonly LlamaImportResolver importResolver;
    private readonly INativeLibraryResolver libraryResolver;
    private readonly ConfigurationStore? configurationStore;
    private readonly ILogger<LlamaBackendInitializer> logger;

    public LlamaBackendInitializer(
        LlamaImportResolver importResolver,
        INativeLibraryResolver libraryResolver,
        ConfigurationStore? configurationStore = null,
        ILogger<LlamaBackendInitializer>? logger = null)
    {
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
        this.configurationStore = configurationStore;
        this.logger = logger ?? NullLogger<LlamaBackendInitializer>.Instance;
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
                logger.BackendInitializationFailed(exception);
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
            logger.BackendInitialized();
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
            logger.DynamicBackendsLoaded(resolution.RuntimeRoot);
        }
        catch (EntryPointNotFoundException exception)
        {
            logger.DynamicBackendProbeSkipped(exception.Message);
        }
        catch (DllNotFoundException exception)
        {
            logger.DynamicBackendProbeSkipped(exception.Message);
        }
    }

    private static void TryLoadBackendIfPresent(string libraryName, string runtimeRoot)
    {
        var path = NativeBundlePaths.ResolveLibraryPath(libraryName, runtimeRoot);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return;
        }

        var backendRegHandle = LlamaNativeMethods.GgmlBackendLoad(path);
        if (backendRegHandle != nint.Zero)
        {
            return;
        }

        TryRegisterBackendFromKnownExport(libraryName, path);
    }

    private static void TryRegisterBackendFromKnownExport(string libraryName, string path)
    {
        var registrationExport = libraryName switch
        {
            "ggml-openvino" => "ggml_backend_openvino_reg",
            _ => null
        };

        if (registrationExport is null)
        {
            return;
        }

        nint libraryHandle = nint.Zero;
        try
        {
            libraryHandle = NativeLibrary.Load(path);
            var registrationHandle = NativeLibrary.GetExport(libraryHandle, registrationExport);
            var registrationFactory =
                Marshal.GetDelegateForFunctionPointer<BackendRegistrationFactory>(registrationHandle);
            var backendRegHandle = registrationFactory();
            if (backendRegHandle == nint.Zero)
            {
                NativeLibrary.Free(libraryHandle);
                return;
            }

            LlamaNativeMethods.GgmlBackendRegister(backendRegHandle);
            RegisteredBackendLibraryHandles.Add(libraryHandle);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                ArgumentException)
        {
            if (libraryHandle != nint.Zero)
            {
                NativeLibrary.Free(libraryHandle);
            }
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint BackendRegistrationFactory();
}
