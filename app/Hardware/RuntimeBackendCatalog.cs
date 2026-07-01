using Tomur.Native;

namespace Tomur.Hardware;

internal static class RuntimeBackendCatalog
{
    private static readonly BackendDefinition[] Backends =
    [
        new("cuda", "CUDA / CUDA 13", "ggml-cuda", "NVIDIA CUDA runtime backend, including CUDA 13 builds."),
        new("cann", "CANN / NPU", "ggml-cann", "Huawei Ascend CANN NPU runtime backend."),
        new("metal", "Metal", "ggml-metal", "Apple Metal runtime backend."),
        new("vulkan", "Vulkan", "ggml-vulkan", "Vulkan GPU runtime backend."),
        new("sycl", "SYCL", "ggml-sycl", "SYCL GPU runtime backend."),
        new("openvino", "OpenVINO / NPU", "ggml-openvino", "OpenVINO GPU/NPU runtime backend."),
        new("opencl", "OpenCL", "ggml-opencl", "OpenCL GPU runtime backend."),
        new("cpu", "CPU", "ggml-cpu", "CPU fallback runtime backend.")
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
                : $"{backend.Description} Library is not present in the managed runtime directory.");
    }

    private sealed record BackendDefinition(
        string Id,
        string DisplayName,
        string LibraryName,
        string Description);
}
