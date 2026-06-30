using Tomur.Inference;
using Tomur.Models;
using Tomur.Native;
using Tomur.Runtime;

namespace Tomur.Hardware;

public sealed class HardwareAccelerationService
{
    private const int AutoConfiguredGpuLayers = 0;
    private const int FullOffloadGpuLayers = 999;
    private const long SafetyMarginBytes = 1L * 1024L * 1024L * 1024L;

    private readonly LlamaBackendInitializer backendInitializer;
    private readonly INativeBundleProbe nativeBundleProbe;

    public HardwareAccelerationService(
        LlamaBackendInitializer backendInitializer,
        INativeBundleProbe nativeBundleProbe)
    {
        this.backendInitializer = backendInitializer;
        this.nativeBundleProbe = nativeBundleProbe;
    }

    public AccelerationPlan GetProfile()
        => ResolvePlan(model: null);

    public AccelerationPlan ResolvePlan(LocalModelDescriptor? model)
    {
        var nativeBundle = nativeBundleProbe.Probe();
        var backends = RuntimeBackendCatalog.ProbeBackends(nativeBundle.RuntimeRoot);
        var cpuBackendReady = backends.Any(static backend =>
            backend.Id == "cpu" && backend.Status == "available");

        if (!cpuBackendReady)
        {
            return new AccelerationPlan(
                "error",
                "cpu",
                "unavailable",
                AutoConfiguredGpuLayers,
                0,
                0,
                null,
                null,
                [],
                backends,
                [
                    "Run tomur native prepare to extract or repair llama.cpp native runtime assets.",
                    "The CPU backend is required even when CUDA, NPU or other accelerator backends are available."
                ]);
        }

        IReadOnlyList<LlamaBackendDeviceDescriptor> nativeDevices;
        try
        {
            backendInitializer.EnsureInitialized();
            nativeDevices = LlamaBackendDeviceCatalog.Enumerate();
        }
        catch (Exception exception) when (exception is InferenceException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new AccelerationPlan(
                "cpu",
                "cpu",
                "cpu",
                AutoConfiguredGpuLayers,
                0,
                0,
                null,
                null,
                [],
                backends,
                [
                    $"Accelerator probe could not use the native backend catalog: {exception.Message}",
                    "Tomur will use CPU inference until an accelerator backend is available."
                ]);
        }

        var devices = nativeDevices
            .Select(static device => device.ToAcceleratorDevice())
            .ToArray();
        var selected = nativeDevices
            .OrderByDescending(GetPreferenceScore)
            .ThenByDescending(static device => device.MemoryBytes ?? 0UL)
            .ThenBy(static device => device.DeviceIndex)
            .FirstOrDefault();

        if (selected is null)
        {
            return new AccelerationPlan(
                "cpu",
                "cpu",
                "cpu",
                AutoConfiguredGpuLayers,
                0,
                0,
                null,
                null,
                devices,
                backends,
                [
                    "No selectable GPU or NPU device was exposed by the loaded llama.cpp backend catalog.",
                    "Tomur will use CPU inference. Add a matching ggml accelerator backend, such as ggml-cuda for CUDA, to enable offload."
                ]);
        }

        var recommendedGpuLayers = ResolveRecommendedGpuLayers(selected, model);
        var selectedDevice = selected.ToAcceleratorDevice();

        return new AccelerationPlan(
            "accelerated",
            selected.Backend,
            selected.Backend,
            AutoConfiguredGpuLayers,
            recommendedGpuLayers,
            recommendedGpuLayers,
            selected.SelectionKey,
            selectedDevice,
            devices,
            backends,
            [
                $"Selected {selected.Kind} accelerator '{selected.Name}' through backend '{selected.Backend}'.",
                recommendedGpuLayers >= FullOffloadGpuLayers
                    ? "Tomur will request full llama.cpp layer offload for this model."
                    : $"Tomur will request partial llama.cpp layer offload ({recommendedGpuLayers} layers) for this model."
            ]);
    }

    private static int ResolveRecommendedGpuLayers(LlamaBackendDeviceDescriptor selected, LocalModelDescriptor? model)
    {
        if (model is null || model.SizeBytes <= 0 || selected.MemoryBytes is not { } memoryBytes || memoryBytes == 0UL)
        {
            return FullOffloadGpuLayers;
        }

        var desiredBytes = checked((ulong)Math.Max(1L, model.SizeBytes + SafetyMarginBytes));
        if (memoryBytes >= desiredBytes)
        {
            return FullOffloadGpuLayers;
        }

        var estimated = (int)Math.Round(64.0 * memoryBytes / Math.Max(1UL, desiredBytes));
        return Math.Clamp(estimated, 1, 64);
    }

    private static int GetPreferenceScore(LlamaBackendDeviceDescriptor device)
    {
        return device.Kind switch
        {
            AcceleratorKind.Cuda => 500,
            AcceleratorKind.Npu => 425,
            AcceleratorKind.Metal => 400,
            AcceleratorKind.Vulkan when !device.IsIntegrated => 350,
            AcceleratorKind.Sycl => 325,
            AcceleratorKind.OpenVino => 300,
            AcceleratorKind.Vulkan => 250,
            _ => 100
        };
    }
}
