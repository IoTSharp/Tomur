using Tomur.Native;
using Tomur.Runtime;

namespace Tomur.Multimodal;

public sealed class MultimodalRuntimeService
{
    private static readonly BackendDefinition[] Backends =
    [
        new(
            Id: "asr",
            DisplayName: "Whisper ASR",
            Capability: "asr",
            NativeComponentId: "whisper",
            RequiredModelCapabilities: ["audio"],
            ModelRequirement: "Install whisper-large-v3-turbo-q5 for local speech-to-text.",
            ExecutionConnected: true),
        new(
            Id: "tts",
            DisplayName: "llama.cpp GGUF TTS",
            Capability: "tts",
            NativeComponentId: "tts",
            RequiredModelCapabilities: ["audio-output"],
            ModelRequirement: "Install outetts-0.2-500m-q4km for local text-to-speech.",
            ExecutionConnected: true),
        new(
            Id: "ocr",
            DisplayName: "PaddleOCR-VL",
            Capability: "ocr",
            NativeComponentId: "ocr",
            RequiredModelCapabilities: ["ocr"],
            ModelRequirement: "Install qwen3-vl-4b-instruct-q4km or another OCR-capable VLM bundle before document OCR requests.",
            ExecutionConnected: true),
        new(
            Id: "image-generation",
            DisplayName: "stable-diffusion.cpp image generation",
            Capability: "image_generation",
            NativeComponentId: "stable-diffusion",
            RequiredModelCapabilities: ["image"],
            ModelRequirement: "Install flux2-klein-4b-q4km for local image generation.",
            ExecutionConnected: true),
        new(
            Id: "vlm",
            DisplayName: "llama.cpp VLM",
            Capability: "vlm",
            NativeComponentId: "llama",
            RequiredModelCapabilities: ["vision"],
            RequiredNativeLibraryNames: ["tomur-llama-mtmd", "tomur-llama-vlm"],
            ModelRequirement: "Install qwen3-vl-4b-instruct-q4km with its mmproj sidecar for local vision-language chat.",
            ExecutionConnected: true)
    ];

    private readonly INativeBundleProbe nativeBundleProbe;
    private readonly LocalModelCatalog localModelCatalog;

    public MultimodalRuntimeService(INativeBundleProbe nativeBundleProbe, LocalModelCatalog localModelCatalog)
    {
        this.nativeBundleProbe = nativeBundleProbe;
        this.localModelCatalog = localModelCatalog;
    }

    public MultimodalRuntimeStatus GetStatus()
    {
        var nativeBundle = nativeBundleProbe.Probe();
        var visibleModels = localModelCatalog.ListModels();
        var backends = Backends
            .Select(definition => ResolveBackendStatus(definition, nativeBundle, visibleModels))
            .ToArray();

        var status = backends.Any(static backend => backend.Status == "error")
            ? "error"
            : backends.Any(static backend => backend.Status == "not_configured")
                ? "not_configured"
                : "ready";

        return new MultimodalRuntimeStatus(
            status,
            DateTimeOffset.UtcNow,
            backends,
            status == "ready"
                ? ["Multimodal native components and model assets are visible. Endpoint-specific runtime adapters still load on demand."]
                : [
                    "Run tomur native prepare to repair missing native components.",
                    "Run tomur pull recommended to install the default local model package set.",
                    "Use /api/models/installed to inspect model bundle assets."
                ]);
    }

    public MultimodalBackendStatus GetBackendStatus(string backendId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);

        var status = GetStatus();
        return status.Backends.FirstOrDefault(item =>
                string.Equals(item.Id, backendId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Capability, backendId, StringComparison.OrdinalIgnoreCase))
            ?? new MultimodalBackendStatus(
                backendId,
                backendId,
                backendId,
                "error",
                string.Empty,
                null,
                null,
                "Unknown backend.",
                [],
                $"Multimodal backend '{backendId}' is not declared.",
                ["Use /api/runtime/multimodal to inspect declared backends."]);
    }

    private static MultimodalBackendStatus ResolveBackendStatus(
        BackendDefinition definition,
        NativeBundleProbeResult nativeBundle,
        IReadOnlyList<LocalModelDescriptor> visibleModels)
    {
        var component = nativeBundle.Components.FirstOrDefault(item =>
            string.Equals(item.Id, definition.NativeComponentId, StringComparison.OrdinalIgnoreCase));
        var nativeReady = component is not null &&
            string.Equals(component.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
            RequiredLibrariesReady(component, definition.RequiredNativeLibraries);
        var matchingModels = visibleModels
            .Where(model => definition.RequiredModelCapabilities.Any(required =>
                model.Capabilities.Any(capability => string.Equals(capability, required, StringComparison.OrdinalIgnoreCase))))
            .Select(static model => model.Id)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modelReady = matchingModels.Length > 0;

        var status = nativeReady && modelReady
            ? "ready"
            : component is null || string.Equals(component.Status, "error", StringComparison.OrdinalIgnoreCase)
                ? "error"
                : "not_configured";

        return new MultimodalBackendStatus(
            definition.Id,
            definition.DisplayName,
            definition.Capability,
            status,
            definition.NativeComponentId,
            component?.Status,
            component is null
                ? $"Native component '{definition.NativeComponentId}' is not declared in the bundle manifest."
                : BuildNativeMessage(component, definition.RequiredNativeLibraries),
            definition.ModelRequirement,
            matchingModels,
            BuildMessage(definition, nativeReady, modelReady, component),
            BuildActions(definition, nativeReady, modelReady));
    }

    private static bool RequiredLibrariesReady(NativeComponentProbeResult component, IReadOnlyList<string> requiredLibraries)
    {
        foreach (var libraryName in requiredLibraries)
        {
            var library = component.Libraries.FirstOrDefault(item =>
                string.Equals(item.Name, libraryName, StringComparison.OrdinalIgnoreCase));
            if (library is null || !library.Exists || string.Equals(library.ChecksumStatus, "mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildNativeMessage(NativeComponentProbeResult component, IReadOnlyList<string> requiredLibraries)
    {
        if (requiredLibraries.Count == 0)
        {
            return component.Message;
        }

        var missing = requiredLibraries
            .Where(libraryName =>
            {
                var library = component.Libraries.FirstOrDefault(item =>
                    string.Equals(item.Name, libraryName, StringComparison.OrdinalIgnoreCase));
                return library is null ||
                    !library.Exists ||
                    string.Equals(library.ChecksumStatus, "mismatch", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        return missing.Length == 0
            ? component.Message
            : $"Required libraries are missing or damaged: {string.Join(", ", missing)}.";
    }

    private static string BuildMessage(
        BackendDefinition definition,
        bool nativeReady,
        bool modelReady,
        NativeComponentProbeResult? component)
    {
        if (nativeReady && modelReady)
        {
            return definition.ExecutionConnected
                ? $"{definition.DisplayName} has visible native assets and model assets; connected R8 endpoint adapters load on demand."
                : $"{definition.DisplayName} has visible native assets and model assets; execution remains diagnostic until its R8 adapter is connected.";
        }

        if (!nativeReady)
        {
            return component is null
                ? $"Native component '{definition.NativeComponentId}' is not declared in the bundle manifest."
                : $"Native component '{definition.NativeComponentId}' is not ready: {BuildNativeMessage(component, definition.RequiredNativeLibraries)}";
        }

        return $"No visible local model asset satisfies {definition.DisplayName}.";
    }

    private static IReadOnlyList<string> BuildActions(BackendDefinition definition, bool nativeReady, bool modelReady)
    {
        var actions = new List<string>();
        if (!nativeReady)
        {
            actions.Add("Run tomur native prepare to extract or repair native runtime assets.");
        }

        if (!modelReady)
        {
            actions.Add(definition.ModelRequirement);
        }

        if (actions.Count == 0)
        {
            actions.Add(definition.ExecutionConnected
                ? "Use the dedicated R8 endpoint adapter for an on-demand execution attempt."
                : "Track the remaining R8 adapter task before expecting real execution.");
        }

        return actions;
    }

    private sealed record BackendDefinition(
        string Id,
        string DisplayName,
        string Capability,
        string NativeComponentId,
        IReadOnlyList<string> RequiredModelCapabilities,
        string ModelRequirement,
        IReadOnlyList<string>? RequiredNativeLibraryNames = null,
        bool ExecutionConnected = false)
    {
        public IReadOnlyList<string> RequiredNativeLibraries { get; } = RequiredNativeLibraryNames ?? [];
    }
}
