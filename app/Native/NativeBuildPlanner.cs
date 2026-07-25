namespace Tomur.Native;

public static class NativeBuildPlanner
{
    private static readonly string[] BuildOrder =
    [
        "llama",
        "whisper",
        "stable-diffusion",
        "ocr",
        "tts",
        "plate"
    ];

    /// <summary>根据目标 RID、后端和清理选项生成按依赖顺序执行的原生构建计划。</summary>
    public static NativeBuildPlan Create(string rid, string backend, bool clean)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedBackend = NormalizeBackend(backend);
        if (normalizedRid is not ("win-x64" or "linux-x64" or "linux-arm64"))
        {
            throw new ArgumentException(
                "The native build planner supports win-x64, linux-x64, and linux-arm64 only.",
                nameof(rid));
        }

        if (normalizedRid == "linux-x64" &&
            normalizedBackend is not ("all" or "cpu" or "cuda129"))
        {
            throw new ArgumentException(
                "Linux x64 native builds support the 'all', 'cpu', and 'cuda129' backends.",
                nameof(backend));
        }

        if (normalizedRid == "linux-arm64" &&
            normalizedBackend is not ("all" or "cpu"))
        {
            throw new ArgumentException(
                "Linux arm64 native builds support the 'all' and 'cpu' backends only.",
                nameof(backend));
        }

        var presetRid = ToPresetRid(normalizedRid);
        NativeBuildStep[] steps = normalizedBackend switch
        {
            "all" when normalizedRid == "linux-arm64" => CreateCpuSteps(presetRid),
            "all" => CreateAllSteps(presetRid),
            "intel" => CreateIntelSteps(presetRid),
            "vulkan" or "openvino" or "sycl" => [CreateStep("llama", presetRid, normalizedBackend)],
            _ => BuildOrder
                .Select(component => CreateStep(
                    component,
                    presetRid,
                    component == "plate" ? "cpu" : normalizedBackend))
                .ToArray()
        };

        return new NativeBuildPlan(normalizedRid, normalizedBackend, clean, steps);
    }

    /// <summary>生成 CPU fallback、CUDA 主组件及支持 CUDA 的叶子组件构建步骤。</summary>
    private static NativeBuildStep[] CreateAllSteps(string rid)
    {
        var leafComponents = BuildOrder
            .Where(static component => component != "llama")
            .ToArray();
        var acceleratedLeafComponents = leafComponents
            .Where(static component => component != "plate")
            .ToArray();

        return
        [
            CreateStep("llama", rid, "cuda129"),
            .. leafComponents.Select(component => CreateStep(component, rid, "cpu")),
            .. acceleratedLeafComponents.Select(component => CreateStep(component, rid, "cuda129"))
        ];
    }

    /// <summary>为不支持加速器的目标生成全组件 CPU 构建步骤。</summary>
    private static NativeBuildStep[] CreateCpuSteps(string rid)
        => BuildOrder
            .Select(component => CreateStep(component, rid, "cpu"))
            .ToArray();

    /// <summary>生成 Intel 平台按优先级构建的 llama.cpp 动态后端步骤。</summary>
    private static NativeBuildStep[] CreateIntelSteps(string rid)
    {
        return
        [
            CreateStep("llama", rid, "sycl"),
            CreateStep("llama", rid, "openvino"),
            CreateStep("llama", rid, "vulkan")
        ];
    }

    /// <summary>把组件名称和后端转换为对应源码目录与 CMake preset。</summary>
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

    /// <summary>规范化命令行 RID，并兼容常见的 Windows/Linux ARM64 别名。</summary>
    private static string NormalizeRid(string rid)
    {
        var normalized = string.IsNullOrWhiteSpace(rid) ? NativeBundlePaths.ResolveRid() : rid.Trim().ToLowerInvariant();
        return normalized switch
        {
            "windows-x64" => "win-x64",
            "linux-aarch64" => "linux-arm64",
            _ => normalized
        };
    }

    /// <summary>转换为 native CMake 工程使用的 preset RID 名称。</summary>
    private static string ToPresetRid(string rid)
        => rid switch
        {
            "win-x64" => "windows-x64",
            _ => rid
        };

    /// <summary>规范化后端别名，并对未知值给出稳定错误。</summary>
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
