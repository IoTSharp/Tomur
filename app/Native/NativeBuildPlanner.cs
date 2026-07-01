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
        if (normalizedRid != "win-x64")
        {
            throw new ArgumentException("The native build planner currently supports win-x64 only.", nameof(rid));
        }

        var presetRid = ToPresetRid(normalizedRid);
        var steps = normalizedBackend == "all"
            ? CreateAllSteps(presetRid)
            : BuildOrder
                .Select(component => CreateStep(component, presetRid, normalizedBackend))
                .ToArray();

        return new NativeBuildPlan(normalizedRid, normalizedBackend, clean, steps);
    }

    private static NativeBuildStep[] CreateAllSteps(string rid)
    {
        var leafComponents = BuildOrder
            .Where(static component => component != "llama")
            .ToArray();

        return
        [
            CreateStep("llama", rid, "cuda13"),
            .. leafComponents.Select(component => CreateStep(component, rid, "cpu")),
            .. leafComponents.Select(component => CreateStep(component, rid, "cuda13"))
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
        var normalized = string.IsNullOrWhiteSpace(rid) ? "win-x64" : rid.Trim().ToLowerInvariant();
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
            "cuda" or "cu13" or "cuda-13" => "cuda13",
            "cpu" => "cpu",
            "cuda13" => "cuda13",
            "all" or "both" => "all",
            _ => throw new ArgumentException("Backend must be 'cuda13', 'cpu', or 'all'.", nameof(backend))
        };
    }
}
