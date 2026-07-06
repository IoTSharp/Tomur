using Tomur.Native;

namespace Tomur.Hardware;

internal static class RuntimeBackendCatalog
{
    private static readonly BackendDefinition[] Backends =
    [
        new(
            "cuda",
            "CUDA / CUDA 13",
            "ggml-cuda",
            "NVIDIA CUDA runtime backend, including CUDA 13 builds.",
            ["Build or prepare the CUDA13 native runtime when NVIDIA offload is required."]),
        new(
            "cann",
            "CANN / NPU",
            "ggml-cann",
            "Huawei Ascend CANN NPU runtime backend.",
            ["CPU inference remains available when the CANN backend is absent."]),
        new(
            "metal",
            "Metal",
            "ggml-metal",
            "Apple Metal runtime backend.",
            ["Metal is expected only on macOS runtime bundles."]),
        new(
            "vulkan",
            "Vulkan",
            "ggml-vulkan",
            "Vulkan GPU runtime backend.",
            [
                "Run tomur native build --rid win-x64 --backend vulkan, then run tomur native prepare.",
                "Install a GPU driver with Vulkan runtime support if devices are not enumerated."
            ]),
        new(
            "sycl",
            "SYCL",
            "ggml-sycl",
            "SYCL GPU runtime backend.",
            [
                "Run tomur native build --rid win-x64 --backend sycl, then run tomur native prepare.",
                "Install Intel oneAPI / SYCL runtime support if the backend is present but devices are not enumerated."
            ]),
        new(
            "openvino",
            "OpenVINO / NPU",
            "ggml-openvino",
            "OpenVINO GPU/NPU runtime backend.",
            [
                "Run tomur native build --rid win-x64 --backend openvino, then run tomur native prepare.",
                "Set runtime.accelerator.openvino_device to GPU, GPU.0 or NPU as needed; NPU selection also requires allow_npu."
            ]),
        new(
            "opencl",
            "OpenCL",
            "ggml-opencl",
            "OpenCL GPU runtime backend.",
            ["OpenCL is optional; Tomur prefers SYCL, OpenVINO or Vulkan for Intel acceleration."]),
        new(
            "cpu",
            "CPU",
            "ggml-cpu",
            "CPU fallback runtime backend.",
            ["Run tomur native prepare to repair the runtime bundle if the CPU backend is missing."])
    ];

    public static IReadOnlyList<AccelerationBackendStatus> ProbeBackends(string runtimeRoot)
        => Backends.Select(backend => ProbeBackend(backend, runtimeRoot)).ToArray();

    private static AccelerationBackendStatus ProbeBackend(BackendDefinition backend, string runtimeRoot)
    {
        var path = string.IsNullOrWhiteSpace(runtimeRoot)
            ? string.Empty
            : NativeBundlePaths.ResolveLibraryPath(backend.LibraryName, runtimeRoot);
        var exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;

        return new AccelerationBackendStatus(
            backend.Id,
            backend.DisplayName,
            backend.LibraryName,
            exists ? "available" : "missing",
            exists ? path : null,
            exists
                ? $"{backend.Description} Library is present in the managed runtime directory."
                : $"{backend.Description} Library is not present in the managed runtime directory.",
            exists
                ? [$"{backend.DisplayName} backend is visible to the managed runtime probe."]
                : backend.Actions);
    }

    private sealed record BackendDefinition(
        string Id,
        string DisplayName,
        string LibraryName,
        string Description,
        IReadOnlyList<string> Actions);
}
