using System.Collections.ObjectModel;

namespace Tomur.Models;

public sealed class ModelCatalog
{
    public IReadOnlyList<ModelPackage> Packages { get; } = new ReadOnlyCollection<ModelPackage>(
    [
        new ModelPackage(
            Id: "qwen35-9b-q4km",
            DisplayName: "Qwen3.5 9B Q4_K_M",
            Description: "Default local general assistant for chat, code, tool use and structured output.",
            Segment: "text",
            FolderName: "qwen3.5-9b-q4-k-m",
            Task: "chat",
            Runtime: "llama.cpp",
            Family: "llama",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: "apache-2.0",
            SizeBytes: 8_953_803_264,
            ParameterCount: 9_000_000_000,
            PrimaryFileName: "Qwen3.5-9B-Q4_K_M.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "chat", "assistant", "code", "tools"],
            Assets:
            [
                DownloadAsset.HuggingFace(
                    "unsloth/Qwen3.5-9B-GGUF",
                    "Qwen3.5-9B-Q4_K_M.gguf",
                    expectedSha256: "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8")
            ],
            BundleAssets: [],
            MinimumMemoryBytes: Gib(16),
            HardwareTier: "standard",
            LicenseNotice: "Apache-2.0 model package. Review upstream model card before redistribution.",
            LowMemoryAlternativeId: "qwen35-4b-q4km"),

        new ModelPackage(
            Id: "qwen35-4b-q4km",
            DisplayName: "Qwen3.5 4B Q4_K_M",
            Description: "Lower-memory local assistant fallback for machines that should not pull the 9B default.",
            Segment: "text",
            FolderName: "qwen3.5-4b-q4-k-m",
            Task: "chat",
            Runtime: "llama.cpp",
            Family: "llama",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: "apache-2.0",
            SizeBytes: null,
            ParameterCount: 4_000_000_000,
            PrimaryFileName: "Qwen3.5-4B-Q4_K_M.gguf",
            Recommended: false,
            Optional: true,
            Research: false,
            Tags: ["local", "chat", "assistant", "fallback", "low-memory"],
            Assets:
            [
                DownloadAsset.HuggingFace(
                    "unsloth/Qwen3.5-4B-GGUF",
                    "Qwen3.5-4B-Q4_K_M.gguf",
                    expectedSha256: "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4")
            ],
            BundleAssets: [],
            MinimumMemoryBytes: Gib(8),
            HardwareTier: "low-memory",
            LicenseNotice: "Apache-2.0 model package. Review upstream model card before redistribution."),

        new ModelPackage(
            Id: "hunyuan-mt-7b-q4km",
            DisplayName: "Hunyuan-MT 7B Q4_K_M",
            Description: "Default local text translation model for multilingual translation workflows.",
            Segment: "text",
            FolderName: "hunyuan-mt-7b-q4-k-m",
            Task: "translation",
            Runtime: "llama.cpp",
            Family: "llama",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: null,
            SizeBytes: null,
            ParameterCount: 7_000_000_000,
            PrimaryFileName: "Hunyuan-MT-7B-q4_k_m.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "translation", "mt", "zh"],
            Assets:
            [
                DownloadAsset.HuggingFace("Mungert/Hunyuan-MT-7B-GGUF", "Hunyuan-MT-7B-q4_k_m.gguf")
            ],
            BundleAssets: [],
            MinimumMemoryBytes: Gib(12),
            HardwareTier: "standard",
            LicenseNotice: "Review upstream license and model card before commercial use.",
            LowMemoryAlternativeId: "qwen35-4b-q4km"),

        new ModelPackage(
            Id: "embeddinggemma-300m-q8",
            DisplayName: "embeddinggemma 300M Q8_0",
            Description: "Default local embeddings model for retrieval and local file question answering.",
            Segment: "embeddings",
            FolderName: "embeddinggemma-300m-q8-0",
            Task: "embeddings",
            Runtime: "llama.cpp",
            Family: "embedding",
            Format: "gguf",
            Quantization: "Q8_0",
            License: null,
            SizeBytes: null,
            ParameterCount: 300_000_000,
            PrimaryFileName: "embeddinggemma-300M-Q8_0.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "embeddings", "retrieval"],
            Assets:
            [
                DownloadAsset.HuggingFace("ggml-org/embeddinggemma-300M-GGUF", "embeddinggemma-300M-Q8_0.gguf")
            ],
            BundleAssets: [],
            MinimumMemoryBytes: Gib(4),
            HardwareTier: "low-memory",
            LicenseNotice: "Review upstream license and model card before redistribution."),

        new ModelPackage(
            Id: "bge-reranker-v2-m3-q8",
            DisplayName: "BGE Reranker v2 M3 Q8_0",
            Description: "Default local reranker model for hybrid retrieval.",
            Segment: "rerank",
            FolderName: "bge-reranker-v2-m3-q8-0",
            Task: "reranking",
            Runtime: "llama.cpp",
            Family: "rerank",
            Format: "gguf",
            Quantization: "Q8_0",
            License: "mit",
            SizeBytes: null,
            ParameterCount: null,
            PrimaryFileName: "bge-reranker-v2-m3-Q8_0.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "rerank", "retrieval"],
            Assets:
            [
                DownloadAsset.HuggingFace("gpustack/bge-reranker-v2-m3-GGUF", "bge-reranker-v2-m3-Q8_0.gguf")
            ],
            BundleAssets: [],
            MinimumMemoryBytes: Gib(4),
            HardwareTier: "low-memory",
            LicenseNotice: "MIT model package. Review upstream model card before redistribution."),

        new ModelPackage(
            Id: "whisper-large-v3-turbo-q5",
            DisplayName: "Whisper large-v3 turbo q5_0",
            Description: "Default offline ASR model with a Silero VAD sidecar.",
            Segment: "speech/asr",
            FolderName: "whisper-large-v3-turbo-q5-0",
            Task: "transcription",
            Runtime: "whisper.cpp",
            Family: "whisper",
            Format: "ggml",
            Quantization: "Q5_0",
            License: null,
            SizeBytes: null,
            ParameterCount: null,
            PrimaryFileName: "ggml-large-v3-turbo-q5_0.bin",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "asr", "whisper", "vad"],
            Assets:
            [
                DownloadAsset.HuggingFace(
                    "ggerganov/whisper.cpp",
                    "ggml-large-v3-turbo-q5_0.bin",
                    expectedSha256: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
                DownloadAsset.HuggingFace(
                    "ggml-org/whisper-vad",
                    "ggml-silero-v6.2.0.bin",
                    "vad/ggml-silero-v6.2.0.bin",
                    "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987")
            ],
            BundleAssets:
            [
                new ModelBundleAsset("primary", "asr-model", true, "ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3-turbo-q5_0.bin", "ggml", "Q5_0"),
                new ModelBundleAsset("vad", "vad-model", true, "vad/ggml-silero-v6.2.0.bin", "ggml-silero-v6.2.0.bin", "ggml", null)
            ],
            MinimumMemoryBytes: Gib(8),
            HardwareTier: "standard",
            LicenseNotice: "Review upstream whisper.cpp and model license terms before redistribution."),

        new ModelPackage(
            Id: "outetts-0.2-500m-q4km",
            DisplayName: "OuteTTS 0.2 500M Q4_K_M",
            Description: "Default llama.cpp TTS / GGUF TTS bundle candidate with WavTokenizer sidecar.",
            Segment: "speech/tts",
            FolderName: "outetts-0.2-500m-q4-k-m",
            Task: "speech",
            Runtime: "llama.cpp-tts",
            Family: "tts",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: "cc-by-nc-4.0",
            SizeBytes: null,
            ParameterCount: 500_000_000,
            PrimaryFileName: "OuteTTS-0.2-500M-Q4_K_M.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "tts", "gguf", "llama.cpp-tts"],
            Assets:
            [
                DownloadAsset.HuggingFace(
                    "OuteAI/OuteTTS-0.2-500M-GGUF",
                    "OuteTTS-0.2-500M-Q4_K_M.gguf",
                    expectedSha256: "5d4b55202869991b4bfdeea7dc825c254d1a0f982986f89fc3be1a83bdc17eb7"),
                DownloadAsset.HuggingFace(
                    "gaianet/OuteTTS-0.2-500M-GGUF",
                    "wavtokenizer-large-75-ggml-f16.gguf",
                    expectedSha256: "1e39b3f8770e6907e80bb32db8041e957aa767485b7d6bbc864ce78fd92d8510")
            ],
            BundleAssets:
            [
                new ModelBundleAsset("primary", "tts-model", true, "OuteTTS-0.2-500M-Q4_K_M.gguf", "OuteTTS-0.2-500M-Q4_K_M.gguf", "gguf", "Q4_K_M", "cc-by-nc-4.0", ExpectedSha256: "5d4b55202869991b4bfdeea7dc825c254d1a0f982986f89fc3be1a83bdc17eb7"),
                new ModelBundleAsset("wavtokenizer", "audio-codec", true, "wavtokenizer-large-75-ggml-f16.gguf", "wavtokenizer-large-75-ggml-f16.gguf", "gguf", "F16", ExpectedSha256: "1e39b3f8770e6907e80bb32db8041e957aa767485b7d6bbc864ce78fd92d8510")
            ],
            MinimumMemoryBytes: Gib(6),
            HardwareTier: "low-memory",
            LicenseNotice: "CC-BY-NC-4.0 package. Review upstream model card before commercial use or redistribution."),

        new ModelPackage(
            Id: "qwen3-vl-4b-instruct-q4km",
            DisplayName: "Qwen3-VL 4B Instruct Q4_K_M",
            Description: "Default local vision-language bundle with required mmproj asset.",
            Segment: "text",
            FolderName: "qwen3-vl-4b-instruct-q4-k-m",
            Task: "vision",
            Runtime: "llama.cpp",
            Family: "llama",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: "apache-2.0",
            SizeBytes: null,
            ParameterCount: 4_000_000_000,
            PrimaryFileName: "Qwen3-VL-4B-Instruct-Q4_K_M.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "vision", "vlm", "mmproj"],
            Assets:
            [
                DownloadAsset.HuggingFace("unsloth/Qwen3-VL-4B-Instruct-GGUF", "Qwen3-VL-4B-Instruct-Q4_K_M.gguf"),
                DownloadAsset.HuggingFace("unsloth/Qwen3-VL-4B-Instruct-GGUF", "mmproj-F16.gguf")
            ],
            BundleAssets:
            [
                new ModelBundleAsset("primary", "vision-language-model", true, "Qwen3-VL-4B-Instruct-Q4_K_M.gguf", "Qwen3-VL-4B-Instruct-Q4_K_M.gguf", "gguf", "Q4_K_M", "apache-2.0"),
                new ModelBundleAsset("mmproj", "clip-vision-encoder", true, "mmproj-F16.gguf", "mmproj-F16.gguf", "gguf", "F16", "apache-2.0")
            ],
            MinimumMemoryBytes: Gib(12),
            HardwareTier: "standard",
            LicenseNotice: "Apache-2.0 model package. Review upstream model card before redistribution.",
            LowMemoryAlternativeId: "qwen35-4b-q4km"),

        new ModelPackage(
            Id: "smolvlm-500m-q8",
            DisplayName: "SmolVLM 500M Instruct Q8_0",
            Description: "Small optional vision-language bundle for fast local smoke validation.",
            Segment: "text",
            FolderName: "smolvlm-500m-instruct-q8-0",
            Task: "vision",
            Runtime: "llama.cpp",
            Family: "llama",
            Format: "gguf",
            Quantization: "Q8_0",
            License: "apache-2.0",
            SizeBytes: 820_422_912,
            ParameterCount: 500_000_000,
            PrimaryFileName: "SmolVLM-500M-Instruct-Q8_0.gguf",
            Recommended: false,
            Optional: true,
            Research: true,
            Tags: ["local", "vision", "vlm", "mmproj", "smoke"],
            Assets:
            [
                DownloadAsset.HuggingFace("ggml-org/SmolVLM-500M-Instruct-GGUF", "SmolVLM-500M-Instruct-Q8_0.gguf"),
                DownloadAsset.HuggingFace("ggml-org/SmolVLM-500M-Instruct-GGUF", "mmproj-SmolVLM-500M-Instruct-Q8_0.gguf")
            ],
            BundleAssets:
            [
                new ModelBundleAsset("primary", "vision-language-model", true, "SmolVLM-500M-Instruct-Q8_0.gguf", "SmolVLM-500M-Instruct-Q8_0.gguf", "gguf", "Q8_0", "apache-2.0"),
                new ModelBundleAsset("mmproj", "clip-vision-encoder", true, "mmproj-SmolVLM-500M-Instruct-Q8_0.gguf", "mmproj-SmolVLM-500M-Instruct-Q8_0.gguf", "gguf", "Q8_0", "apache-2.0")
            ],
            MinimumMemoryBytes: Gib(4),
            HardwareTier: "low-memory",
            LicenseNotice: "Apache-2.0 model package. Intended as an optional fast smoke-validation VLM, not the default vision package."),

        new ModelPackage(
            Id: "flux2-klein-4b-q4km",
            DisplayName: "FLUX.2 klein 4B Q4_K_M",
            Description: "Default local image generation bundle with VAE and Qwen3 text encoder sidecars.",
            Segment: "image",
            FolderName: "flux-2-klein-4b-q4-k-m",
            Task: "image-generation",
            Runtime: "stable-diffusion.cpp",
            Family: "image",
            Format: "gguf",
            Quantization: "Q4_K_M",
            License: null,
            SizeBytes: null,
            ParameterCount: 4_000_000_000,
            PrimaryFileName: "flux-2-klein-4b-Q4_K_M.gguf",
            Recommended: true,
            Optional: false,
            Research: false,
            Tags: ["default", "local", "image", "flux"],
            Assets:
            [
                DownloadAsset.HuggingFace(
                    "unsloth/FLUX.2-klein-4B-GGUF",
                    "flux-2-klein-4b-Q4_K_M.gguf",
                    expectedSha256: "0b25d143c8469b342bc5af3bce92b783bf6b0636d285f7b2f75e38af63af9a15"),
                DownloadAsset.HuggingFace("Comfy-Org/vae-text-encorder-for-flux-klein-4b", "split_files/vae/flux2-vae.safetensors", "vae/flux2-vae.safetensors"),
                DownloadAsset.HuggingFace("Comfy-Org/vae-text-encorder-for-flux-klein-4b", "split_files/text_encoders/qwen_3_4b.safetensors", "text-encoders/qwen_3_4b.safetensors")
            ],
            BundleAssets:
            [
                new ModelBundleAsset("diffusion-model", "diffusion-model", true, "flux-2-klein-4b-Q4_K_M.gguf", "flux-2-klein-4b-Q4_K_M.gguf", "gguf", "Q4_K_M"),
                new ModelBundleAsset("vae", "vae", true, "vae/flux2-vae.safetensors", "flux2-vae.safetensors", "safetensors"),
                new ModelBundleAsset("llm-text-encoder", "text-encoder", true, "text-encoders/qwen_3_4b.safetensors", "qwen_3_4b.safetensors", "safetensors")
            ],
            MinimumMemoryBytes: Gib(16),
            HardwareTier: "standard",
            LicenseNotice: "Review upstream FLUX.2 klein and sidecar licenses before commercial use.",
            LowMemoryAlternativeId: "qwen35-4b-q4km")
    ]);

    public IReadOnlyList<ModelPackage> GetAll()
    {
        return Packages;
    }

    public ModelPackage? Find(string id)
    {
        return Packages.FirstOrDefault(package => string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ModelPackage> Select(IReadOnlyList<string> selections, HardwareProfile? hardwareProfile = null)
    {
        if (selections.Count == 0)
        {
            return SelectRecommended(hardwareProfile);
        }

        var selected = new Dictionary<string, ModelPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawToken in selections)
        {
            var token = rawToken.Trim();
            switch (token.ToLowerInvariant())
            {
                case "recommended":
                    AddPackages(selected, SelectRecommended(hardwareProfile));
                    break;
                case "optional":
                    AddPackages(selected, Packages.Where(static package => package.Optional));
                    break;
                case "research":
                    AddPackages(selected, Packages.Where(static package => package.Research));
                    break;
                case "all":
                    AddPackages(selected, Packages);
                    break;
                case "native-deps":
                case "native":
                case "toolchains":
                    throw new InvalidOperationException("Native runtime preparation is managed by 'tomur native prepare'; model downloads only install model assets.");
                case "deepseek-r1-0528-qwen3-8b-q4km":
                    throw new InvalidOperationException("DeepSeek-R1 local GGUF is not part of the default Tomur catalog. Use qwen35-9b-q4km locally and configure DeepSeek only as a remote provider later.");
                case "spark-tts-0.5b-research":
                case "qwen3-tts-12hz-0.6b-customvoice-onnx":
                    throw new InvalidOperationException("Tomur R6 keeps local TTS on the llama.cpp TTS / GGUF TTS route. Use outetts-0.2-500m-q4km for the default TTS bundle.");
                default:
                    var package = Find(token);
                    if (package is null)
                    {
                        throw new InvalidOperationException($"Unknown model package '{token}'. Run 'tomur list --catalog' to inspect package ids.");
                    }

                    selected[package.Id] = package;
                    break;
            }
        }

        return Sort(selected.Values);
    }

    public IReadOnlyList<ModelPackage> SelectRecommended(HardwareProfile? hardwareProfile)
    {
        var selected = new Dictionary<string, ModelPackage>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in Packages.Where(static package => package.Recommended))
        {
            var selectedPackage = ResolveHardwareFit(package, hardwareProfile);
            selected[selectedPackage.Id] = selectedPackage;
        }

        return Sort(selected.Values);
    }

    public ModelPackage ResolveHardwareFit(ModelPackage package, HardwareProfile? hardwareProfile)
    {
        if (hardwareProfile?.TotalMemoryBytes is not { } memoryBytes)
        {
            return package;
        }

        if (memoryBytes >= (ulong)package.MinimumMemoryBytes)
        {
            return package;
        }

        if (string.IsNullOrWhiteSpace(package.LowMemoryAlternativeId))
        {
            return package;
        }

        var alternative = Find(package.LowMemoryAlternativeId);
        return alternative ?? package;
    }

    private static IReadOnlyList<ModelPackage> Sort(IEnumerable<ModelPackage> packages)
    {
        return packages
            .OrderBy(static package => GetSegmentOrder(package.Segment))
            .ThenBy(static package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetSegmentOrder(string segment)
    {
        return segment switch
        {
            "text" => 0,
            "embeddings" => 1,
            "rerank" => 2,
            "speech/asr" => 3,
            "speech/tts" => 4,
            "image" => 5,
            _ => 99
        };
    }

    private static void AddPackages(IDictionary<string, ModelPackage> selected, IEnumerable<ModelPackage> packages)
    {
        foreach (var package in packages)
        {
            selected[package.Id] = package;
        }
    }

    private static long Gib(long value)
    {
        return value * 1024L * 1024L * 1024L;
    }
}
