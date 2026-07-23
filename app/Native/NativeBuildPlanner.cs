namespace Tomur.Native;

public static class NativeBuildPlanner
{
    private static readonly string[] BuildOrder =
    [
        "llama",
        "whisper",
        "stable-diffusion",
        "ocr",
        "tts"
    ];

    public static NativeBuildPlan Create(string rid, string backend, bool clean)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedBackend = NormalizeBackend(backend);
        if (normalizedRid is not ("win-x64" or "linux-x64"))
        {
            throw new ArgumentException("The native build planner supports win-x64 and linux-x64 only.", nameof(rid));
        }

        if (normalizedRid == "linux-x64" &&
            normalizedBackend is not ("all" or "cpu" or "cuda129"))
        {
            throw new ArgumentException(
                "Linux x64 native builds support the 'all', 'cpu', and 'cuda129' backends.",
                nameof(backend));
        }

        var presetRid = ToPresetRid(normalizedRid);
        NativeBuildStep[] steps = normalizedBackend switch
        {
            "all" => CreateAllSteps(presetRid),
            "intel" => CreateIntelSteps(presetRid),
            "vulkan" or "openvino" or "sycl" => [CreateStep("llama", presetRid, normalizedBackend)],
            _ => BuildOrder
                .Select(component => CreateStep(component, presetRid, normalizedBackend))
                .ToArray()
        };

        return new NativeBuildPlan(normalizedRid, normalizedBackend, clean, steps);
    }

    private static NativeBuildStep[] CreateAllSteps(string rid)
    {
        var leafComponents = BuildOrder
            .Where(static component => component != "llama")
            .ToArray();

        return
        [
            CreateStep("llama", rid, "cuda129"),
            .. leafComponents.Select(component => CreateStep(component, rid, "cpu")),
            .. leafComponents.Select(component => CreateStep(component, rid, "cuda129"))
        ];
    }

    private static NativeBuildStep[] CreateIntelSteps(string rid)
    {
        return
        [
            CreateStep("llama", rid, "sycl"),
            CreateStep("llama", rid, "openvino"),
            CreateStep("llama", rid, "vulkan")
        ];
    }

    private static NativeBuildStep CreateStep(string component, string rid, string backend)
    {
        var sourceDirectory = component switch
        {
            "stable-diffusion" => "stable-diffusion.native",
            _ => $"{component}.native"
        };
        var preset = backend == "cpu"
            ? rid
            : $"{rid}-{backend}";

        return new NativeBuildStep(
            component,
            sourceDirectory,
            preset,
            preset,
            Required: true);
    }

    private static string NormalizeRid(string rid)
    {
        var normalized = string.IsNullOrWhiteSpace(rid) ? NativeBundlePaths.ResolveRid() : rid.Trim().ToLowerInvariant();
        return normalized is "windows-x64" ? "win-x64" : normalized;
    }

    private static string ToPresetRid(string rid)
        => rid switch
        {
            "win-x64" => "windows-x64",
            _ => rid
        };

    private static string NormalizeBackend(string backend)
    {
        var normalized = string.IsNullOrWhiteSpace(backend) ? "all" : backend.Trim().ToLowerInvariant();
        return normalized switch
        {
            "cuda" or "cuda12" or "cuda12.9" or "cuda-12.9" or "cu129" => "cuda129",
            "cpu" => "cpu",
            "cuda129" => "cuda129",
            "cu13" or "cuda-13" => "cuda13",
            "cuda13" => "cuda13",
            "vk" or "vulkan" => "vulkan",
            "ov" or "openvino" => "openvino",
            "sycl" or "oneapi" => "sycl",
            "intel" => "intel",
            "all" or "both" => "all",
            _ => throw new ArgumentException("Backend must be 'all', 'cpu', 'cuda129', 'cuda13', 'vulkan', 'openvino', 'sycl', or 'intel'.", nameof(backend))
        };
    }
}
