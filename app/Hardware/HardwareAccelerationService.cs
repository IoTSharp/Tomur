using Tomur.Config;
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
    private readonly ConfigurationStore? configurationStore;

    public HardwareAccelerationService(
        LlamaBackendInitializer backendInitializer,
        INativeBundleProbe nativeBundleProbe,
        ConfigurationStore? configurationStore = null)
    {
        this.backendInitializer = backendInitializer;
        this.nativeBundleProbe = nativeBundleProbe;
        this.configurationStore = configurationStore;
    }

    public AccelerationPlan GetProfile()
        => ResolvePlan(model: null);

    public AccelerationPlan ResolvePlan(LocalModelDescriptor? model)
    {
        var preference = ResolvePreference();
        var nativeBundle = nativeBundleProbe.Probe();
        var backends = RuntimeBackendCatalog.ProbeBackends(nativeBundle.RuntimeRoot);
        var cpuBackendReady = backends.Any(static backend =>
            backend.Id == "cpu" && backend.Status == "available");

        if (!cpuBackendReady)
        {
            return new AccelerationPlan(
                "error",
                preference.Preference,
                "unavailable",
                preference.ConfiguredGpuLayers,
                0,
                0,
                null,
                null,
                preference.DeviceSelectionKey,
                preference.OpenVinoDevice,
                preference.AllowNpu,
                preference.NpuPrefillChunk,
                "The required CPU backend is missing.",
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
                preference.Preference,
                "cpu",
                preference.ConfiguredGpuLayers,
                0,
                0,
                null,
                null,
                preference.DeviceSelectionKey,
                preference.OpenVinoDevice,
                preference.AllowNpu,
                preference.NpuPrefillChunk,
                $"The native backend catalog could not be queried: {exception.Message}",
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

        if (preference.Preference == "cpu")
        {
            return CreateCpuFallbackPlan(
                preference,
                backends,
                devices,
                "CPU acceleration preference is configured.",
                ["Runtime accelerator preference is set to CPU. Tomur will not request GPU or NPU offload."]);
        }

        if (!BackendPreferenceAvailable(backends, preference.Preference))
        {
            return CreateCpuFallbackPlan(
                preference,
                backends,
                devices,
                $"Configured accelerator backend '{preference.Preference}' is not available in the managed runtime directory.",
                [
                    $"Build or prepare the '{preference.Preference}' backend, then rerun tomur native prepare.",
                    "CPU inference remains available when the required Intel backend is missing."
                ]);
        }

        var selected = SelectDevice(nativeDevices, backends, preference);

        if (!string.IsNullOrWhiteSpace(preference.DeviceSelectionKey) && selected is null)
        {
            return CreateCpuFallbackPlan(
                preference,
                backends,
                devices,
                $"Configured accelerator device '{preference.DeviceSelectionKey}' is not available for the selected backend.",
                [
                    "Inspect /api/runtime/status or tomur doctor for the current accelerator selection keys.",
                    "Update runtime.accelerator.device_selection_key or remove it to allow automatic selection."
                ]);
        }

        selected ??= nativeDevices
            .Where(device => IsAllowedByPreference(device, preference) &&
                BackendAvailable(backends, device.Backend))
            .OrderByDescending(device => GetPreferenceScore(device, preference))
            .ThenByDescending(static device => device.MemoryBytes ?? 0UL)
            .ThenBy(static device => device.DeviceIndex)
            .FirstOrDefault();

        if (selected is null)
        {
            var allowedDeviceWithoutRuntimeBackend = nativeDevices.Any(device =>
                IsAllowedByPreference(device, preference) &&
                !BackendAvailable(backends, device.Backend));
            var reason = allowedDeviceWithoutRuntimeBackend
                ? "Accelerator devices were exposed by llama.cpp, but the matching ggml backend library is not available in the managed runtime directory."
                : preference.AllowNpu
                ? "No selectable GPU or NPU device was exposed by the loaded llama.cpp backend catalog."
                : "No selectable GPU device was exposed by the loaded llama.cpp backend catalog; NPU selection is disabled unless runtime.accelerator.allow_npu is true.";

            return CreateCpuFallbackPlan(
                preference,
                backends,
                devices,
                reason,
                [
                    reason,
                    "Tomur will use CPU inference until a matching accelerator backend and device are available."
                ]);
        }

        var recommendedGpuLayers = ResolveRecommendedGpuLayers(selected, model);
        var effectiveGpuLayers = preference.GpuLayers ?? recommendedGpuLayers;
        var selectedDevice = selected.ToAcceleratorDevice();
        var npuRequested = selected.Kind == AcceleratorKind.Npu ||
            IsOpenVinoNpuDevice(preference.OpenVinoDevice);

        return new AccelerationPlan(
            "accelerated",
            preference.Preference,
            selected.Backend,
            preference.ConfiguredGpuLayers,
            effectiveGpuLayers,
            recommendedGpuLayers,
            selected.SelectionKey,
            selectedDevice,
            preference.DeviceSelectionKey,
            preference.OpenVinoDevice,
            preference.AllowNpu,
            preference.NpuPrefillChunk,
            null,
            devices,
            backends,
            [
                $"Selected {selected.Kind} accelerator '{selected.Name}' through backend '{selected.Backend}'.",
                npuRequested
                    ? "Intel NPU selection is opt-in; Tomur rejects unsafe contexts above 4096 tokens before native execution and returns NPU-specific diagnostics for model, context or decode incompatibility."
                    : "Backend visibility and device enumeration are diagnostics; successful inference is confirmed by a real request and token usage.",
                npuRequested
                    ? "Backend visibility and device enumeration do not imply that this model has passed real NPU inference; keep /v1/chat/completions token usage or the structured failure response as smoke evidence."
                    : "Record backend, device, model, context and token usage in smoke evidence before claiming real accelerator inference.",
                effectiveGpuLayers >= FullOffloadGpuLayers
                    ? "Tomur will request full llama.cpp layer offload for this model."
                    : $"Tomur will request partial llama.cpp layer offload ({effectiveGpuLayers} layers) for this model."
            ]);
    }

    private AccelerationPreference ResolvePreference()
    {
        var configuration = configurationStore?.EnsureConfiguration().Configuration;
        var accelerator = RuntimeAcceleratorConfiguration.Normalize(configuration?.Runtime?.Accelerator);
        return new AccelerationPreference(
            accelerator.Preference,
            accelerator.DeviceSelectionKey,
            accelerator.GpuLayers,
            accelerator.GpuLayers ?? AutoConfiguredGpuLayers,
            accelerator.OpenVinoDevice,
            accelerator.AllowNpu,
            accelerator.NpuPrefillChunk);
    }

    private static AccelerationPlan CreateCpuFallbackPlan(
        AccelerationPreference preference,
        IReadOnlyList<AccelerationBackendStatus> backends,
        IReadOnlyList<AcceleratorDevice> devices,
        string fallbackReason,
        IReadOnlyList<string> actions)
    {
        return new AccelerationPlan(
            "cpu",
            preference.Preference,
            "cpu",
            preference.ConfiguredGpuLayers,
            0,
            0,
            null,
            null,
            preference.DeviceSelectionKey,
            preference.OpenVinoDevice,
            preference.AllowNpu,
            preference.NpuPrefillChunk,
            fallbackReason,
            devices,
            backends,
            actions);
    }

    private static LlamaBackendDeviceDescriptor? SelectDevice(
        IReadOnlyList<LlamaBackendDeviceDescriptor> nativeDevices,
        IReadOnlyList<AccelerationBackendStatus> backends,
        AccelerationPreference preference)
    {
        if (string.IsNullOrWhiteSpace(preference.DeviceSelectionKey))
        {
            return null;
        }

        return nativeDevices.FirstOrDefault(device =>
            IsAllowedByPreference(device, preference) &&
            BackendAvailable(backends, device.Backend) &&
            LlamaBackendDeviceCatalog.MatchesSelectionKey(device, preference.DeviceSelectionKey!));
    }

    private static bool IsAllowedByPreference(
        LlamaBackendDeviceDescriptor device,
        AccelerationPreference preference)
    {
        if (device.Kind == AcceleratorKind.Npu && !preference.AllowNpu)
        {
            return false;
        }

        if (device.Kind == AcceleratorKind.Npu &&
            !string.Equals(device.Backend, "openvino", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return preference.Preference switch
        {
            "auto" => true,
            "cuda" => device.Kind == AcceleratorKind.Cuda ||
                device.Backend.Contains("cuda", StringComparison.OrdinalIgnoreCase),
            "vulkan" => device.Kind == AcceleratorKind.Vulkan ||
                device.Backend.Contains("vulkan", StringComparison.OrdinalIgnoreCase),
            "sycl" => device.Kind == AcceleratorKind.Sycl ||
                device.Backend.Contains("sycl", StringComparison.OrdinalIgnoreCase),
            "openvino" => device.Kind is AcceleratorKind.OpenVino or AcceleratorKind.Npu ||
                device.Backend.Contains("openvino", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool BackendPreferenceAvailable(
        IReadOnlyList<AccelerationBackendStatus> backends,
        string preference)
    {
        if (preference is "auto" or "cpu")
        {
            return true;
        }

        return backends.Any(backend =>
            string.Equals(backend.Id, preference, StringComparison.OrdinalIgnoreCase) &&
            backend.Status == "available");
    }

    private static bool BackendAvailable(
        IReadOnlyList<AccelerationBackendStatus> backends,
        string backendName)
    {
        return backends.Any(backend =>
            backend.Status == "available" &&
            (string.Equals(backend.Id, backendName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(backend.LibraryName, backendName, StringComparison.OrdinalIgnoreCase) ||
             backendName.Contains(backend.Id, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsOpenVinoNpuDevice(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
            value.Trim().StartsWith("NPU", StringComparison.OrdinalIgnoreCase);

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

    private static int GetPreferenceScore(LlamaBackendDeviceDescriptor device, AccelerationPreference preference)
    {
        if (preference.Preference != "auto" &&
            IsAllowedByPreference(device, preference))
        {
            return 1_000;
        }

        return device.Kind switch
        {
            AcceleratorKind.Cuda => 500,
            AcceleratorKind.Npu when preference.AllowNpu => 425,
            AcceleratorKind.Metal => 400,
            AcceleratorKind.Sycl => 375,
            AcceleratorKind.OpenVino => 360,
            AcceleratorKind.Vulkan when !device.IsIntegrated => 330,
            AcceleratorKind.Vulkan => 250,
            _ => 100
        };
    }

    private sealed record AccelerationPreference(
        string Preference,
        string? DeviceSelectionKey,
        int? GpuLayers,
        int ConfiguredGpuLayers,
        string? OpenVinoDevice,
        bool AllowNpu,
        int? NpuPrefillChunk);
}
