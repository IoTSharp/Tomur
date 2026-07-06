using Tomur.Inference;

namespace Tomur.Hardware;

internal static class LlamaBackendDeviceCatalog
{
    public static IReadOnlyList<LlamaBackendDeviceDescriptor> Enumerate()
    {
        var rawDevices = new List<LlamaBackendDeviceDescriptor>();
        var deviceCount = checked((int)LlamaNativeMethods.BackendDeviceCount());

        for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
        {
            var deviceHandle = LlamaNativeMethods.BackendDeviceGet((nuint)deviceIndex);
            if (deviceHandle == nint.Zero)
            {
                continue;
            }

            var deviceType = LlamaNativeMethods.BackendDeviceType(deviceHandle);
            if (!IsSelectable(deviceType))
            {
                continue;
            }

            var backendReg = LlamaNativeMethods.BackendDeviceBackendReg(deviceHandle);
            var backendName = NormalizeBackendName(LlamaNativeMethods.GetBackendRegName(backendReg));
            if (string.Equals(backendName, "rpc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var props = LlamaNativeMethods.GetBackendDeviceProperties(deviceHandle);
            var kind = MapKind(backendName, deviceType, props);
            var isIntegrated = deviceType == GgmlBackendDeviceType.IntegratedGpu;
            var displayName = BuildDisplayName(kind, props.Description, props.Name, deviceIndex);
            var selectionKey = BuildSelectionKey(backendName, props.DeviceId, deviceIndex);

            rawDevices.Add(new LlamaBackendDeviceDescriptor(
                deviceIndex,
                deviceHandle,
                kind,
                displayName,
                props.MemoryTotalBytes == 0UL ? null : props.MemoryTotalBytes,
                selectionKey,
                backendName,
                isIntegrated,
                props.DeviceId));
        }

        return rawDevices.Count == 0 ? [] : Deduplicate(rawDevices);
    }

    public static LlamaBackendDeviceDescriptor? FindBySelectionKey(string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return null;
        }

        return Enumerate().FirstOrDefault(device => MatchesSelectionKey(device, selectionKey));
    }

    private static IReadOnlyList<LlamaBackendDeviceDescriptor> Deduplicate(IReadOnlyList<LlamaBackendDeviceDescriptor> rawDevices)
    {
        var uniqueByDeviceId = new Dictionary<string, LlamaBackendDeviceDescriptor>(StringComparer.OrdinalIgnoreCase);
        var devicesWithoutIds = new List<LlamaBackendDeviceDescriptor>();

        foreach (var device in rawDevices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceId))
            {
                devicesWithoutIds.Add(device);
                continue;
            }

            if (!uniqueByDeviceId.TryGetValue(device.DeviceId, out var existing) || ShouldReplace(existing, device))
            {
                uniqueByDeviceId[device.DeviceId] = device;
            }
        }

        return uniqueByDeviceId.Values
            .Concat(devicesWithoutIds)
            .OrderBy(device => device.DeviceIndex)
            .ToArray();
    }

    private static bool ShouldReplace(LlamaBackendDeviceDescriptor existing, LlamaBackendDeviceDescriptor candidate)
    {
        var existingScore = GetPreferenceScore(existing);
        var candidateScore = GetPreferenceScore(candidate);

        if (candidateScore != existingScore)
        {
            return candidateScore > existingScore;
        }

        if ((candidate.MemoryBytes ?? 0UL) != (existing.MemoryBytes ?? 0UL))
        {
            return (candidate.MemoryBytes ?? 0UL) > (existing.MemoryBytes ?? 0UL);
        }

        return candidate.DeviceIndex < existing.DeviceIndex;
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

    private static bool IsSelectable(GgmlBackendDeviceType deviceType)
        => deviceType is GgmlBackendDeviceType.Gpu or GgmlBackendDeviceType.IntegratedGpu or GgmlBackendDeviceType.Accelerator;

    private static AcceleratorKind MapKind(string backendName, GgmlBackendDeviceType deviceType, GgmlBackendDeviceProperties props)
    {
        if (backendName.Contains("cuda", StringComparison.OrdinalIgnoreCase))
        {
            return AcceleratorKind.Cuda;
        }

        if (backendName.Contains("cann", StringComparison.OrdinalIgnoreCase) ||
            backendName.Contains("ascend", StringComparison.OrdinalIgnoreCase))
        {
            return AcceleratorKind.Npu;
        }

        if (backendName.Contains("metal", StringComparison.OrdinalIgnoreCase))
        {
            return AcceleratorKind.Metal;
        }

        if (backendName.Contains("vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return AcceleratorKind.Vulkan;
        }

        if (backendName.Contains("sycl", StringComparison.OrdinalIgnoreCase))
        {
            return AcceleratorKind.Sycl;
        }

        if (backendName.Contains("openvino", StringComparison.OrdinalIgnoreCase))
        {
            return ContainsNpuHint(backendName, props.Description, props.Name, props.DeviceId)
                ? AcceleratorKind.Npu
                : AcceleratorKind.OpenVino;
        }

        if (deviceType == GgmlBackendDeviceType.Accelerator)
        {
            return ContainsNpuHint(backendName, props.Description, props.Name, props.DeviceId)
                ? AcceleratorKind.Npu
                : AcceleratorKind.OpenVino;
        }

        return deviceType is GgmlBackendDeviceType.Gpu or GgmlBackendDeviceType.IntegratedGpu
            ? AcceleratorKind.Unknown
            : AcceleratorKind.Cpu;
    }

    private static bool ContainsNpuHint(params string?[] values)
        => values.Any(static value => !string.IsNullOrWhiteSpace(value)
            && (value.Contains("npu", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("neural", StringComparison.OrdinalIgnoreCase)));

    private static string BuildDisplayName(AcceleratorKind kind, string? description, string? name, int deviceIndex)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return $"{kind} device #{deviceIndex}";
    }

    private static string BuildSelectionKey(string backendName, string? deviceId, int deviceIndex)
    {
        var normalizedBackend = string.IsNullOrWhiteSpace(backendName)
            ? "unknown"
            : backendName.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return $"{normalizedBackend}:{deviceId.Trim().ToLowerInvariant()}";
        }

        return $"{normalizedBackend}:index-{deviceIndex}";
    }

    internal static bool MatchesSelectionKey(LlamaBackendDeviceDescriptor device, string selectionKey)
    {
        var normalized = selectionKey.Trim();
        if (string.Equals(device.SelectionKey, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceId) &&
            string.Equals($"{device.Backend}:{device.DeviceId}", normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var backend = string.IsNullOrWhiteSpace(device.Backend)
            ? "unknown"
            : device.Backend.Trim();

        return string.Equals($"{backend}:{device.DeviceIndex}", normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{backend}:index-{device.DeviceIndex}", normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{device.Kind}:{device.DeviceIndex}", normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{device.Kind}:index-{device.DeviceIndex}", normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBackendName(string? backendName)
        => string.IsNullOrWhiteSpace(backendName) ? "unknown" : backendName.Trim();
}

internal sealed record LlamaBackendDeviceDescriptor(
    int DeviceIndex,
    nint DeviceHandle,
    AcceleratorKind Kind,
    string Name,
    ulong? MemoryBytes,
    string SelectionKey,
    string Backend,
    bool IsIntegrated,
    string? DeviceId)
{
    public AcceleratorDevice ToAcceleratorDevice()
        => new(DeviceIndex, Kind.ToString(), Name, MemoryBytes, SelectionKey, Backend, IsIntegrated, DeviceId);
}
