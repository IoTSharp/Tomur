using System.Runtime.InteropServices;
using System.Text.Json;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Models;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Multimodal;

public sealed class MultimodalExecutionService
{
    private readonly MultimodalRuntimeService runtimeService;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;
    private readonly DataPaths paths;
    private readonly LlamaImportResolver importResolver;
    private readonly INativeLibraryResolver libraryResolver;

    public MultimodalExecutionService(
        MultimodalRuntimeService runtimeService,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        DataPaths paths,
        LlamaImportResolver importResolver,
        INativeLibraryResolver libraryResolver)
    {
        this.runtimeService = runtimeService;
        this.diagnosticsProvider = diagnosticsProvider;
        this.paths = paths;
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
    }

    public MultimodalBackendStatus GetBackendStatus(string backendId)
        => runtimeService.GetBackendStatus(backendId);

    public RuntimeDiagnostic CreateUnavailableDiagnostic(string backendId, string? model)
    {
        var backend = runtimeService.GetBackendStatus(backendId);
        return diagnosticsProvider.GetMultimodalRuntimeUnavailable(model, backend);
    }

    public RuntimeDiagnostic CreateCapabilityMismatchDiagnostic(
        string? model,
        string requiredCapability,
        string route)
    {
        return new RuntimeDiagnostic(
            "error",
            "model_capability_mismatch",
            $"The requested model does not expose the '{requiredCapability}' capability required by {route}.",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                "Use /v1/models or /api/models/installed to inspect model capabilities.",
                "Run tomur pull recommended to install the default local multimodal model packages.",
                "Use /api/runtime/multimodal to inspect backend readiness."
            ]);
    }

    public MultimodalOperationResponse CreateUnavailableResponse(
        string route,
        string backendId,
        string? model,
        MultimodalInputSummary input)
    {
        var backend = runtimeService.GetBackendStatus(backendId);
        var diagnostic = diagnosticsProvider.GetMultimodalRuntimeUnavailable(model, backend);
        return CreateResponse(route, backend, model, diagnostic, input);
    }

    public MultimodalOperationResponse CreateDiagnosticResponse(
        string route,
        string backendId,
        string? model,
        RuntimeDiagnostic diagnostic,
        MultimodalInputSummary input)
    {
        var backend = runtimeService.GetBackendStatus(backendId);
        return CreateResponse(route, backend, model, diagnostic, input);
    }

    public MultimodalTextResponse CreateTextResponse(
        string route,
        string backendId,
        string? model,
        NativeOperationResult result,
        MultimodalInputSummary input)
    {
        var backend = runtimeService.GetBackendStatus(backendId);
        return new MultimodalTextResponse(
            "ok",
            route,
            backend.Id,
            string.IsNullOrWhiteSpace(model) ? null : model,
            result.Text,
            (long)Math.Round(result.Elapsed.TotalMilliseconds),
            result.Diagnostics,
            backend,
            input);
    }

    public NativeOperationResult AnalyzeVision(
        LocalModelDescriptor model,
        string prompt,
        IReadOnlyList<ImageInputBytes> images,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBackendReady("vlm", model.Id);
        EnsureImages(images);
        var mmprojPath = ResolveRequiredBundleAsset(model, "mmproj");

        try
        {
            importResolver.Register();
            using var memory = new VlmInteropMemory(model.AbsolutePath, mmprojPath, prompt, images, options.StopSequences);
            var request = new VlmRequest
            {
                ModelPath = memory.ModelPath,
                MmprojPath = memory.MmprojPath,
                PromptUtf8 = memory.Prompt,
                Images = memory.Images,
                ImageCount = (nuint)images.Count,
                ContextSize = Math.Clamp(options.ContextSize, 1024, 131072),
                BatchSize = 512,
                Threads = Environment.ProcessorCount,
                GpuLayers = 0,
                MaxOutputTokens = Math.Clamp(options.MaxOutputTokens, 1, 4096),
                Temperature = options.Temperature,
                TopP = options.TopP,
                TopK = options.TopK,
                PenaltyLastTokens = options.PenaltyLastTokens,
                RepeatPenalty = options.RepeatPenalty,
                FrequencyPenalty = options.FrequencyPenalty,
                PresencePenalty = options.PresencePenalty,
                Seed = options.Seed,
                StopSequences = memory.StopSequences,
                StopSequenceCount = (nuint)memory.StopSequenceCount,
                UseGpu = false,
                FlashAttention = false,
                Warmup = false
            };

            using var resultHandle = new VlmResultHandle(MultimodalNativeMethods.LlamaVlmGenerate(ref request));
            return ReadVlmResult(resultHandle);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CreateNativeRuntimeException("vlm", exception);
        }
    }

    public NativeOperationResult AnalyzeOcr(
        LocalModelDescriptor model,
        ImageInputBytes image,
        string? prompt,
        string? language,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBackendReady("ocr", model.Id);
        var mmprojPath = ResolveRequiredBundleAsset(model, "mmproj");

        try
        {
            importResolver.Register();
            var imageHandle = GCHandle.Alloc(image.Bytes, GCHandleType.Pinned);
            try
            {
                using var resultHandle = new OcrResultHandle(MultimodalNativeMethods.OcrRecognizeImage(
                    modelPath: model.AbsolutePath,
                    mmprojPath: mmprojPath,
                    imageData: imageHandle.AddrOfPinnedObject(),
                    imageLength: (nuint)image.Bytes.Length,
                    prompt: prompt,
                    language: language,
                    contextSize: Math.Clamp(options.ContextSize, 1024, 131072),
                    batchSize: 512,
                    threads: Environment.ProcessorCount,
                    gpuLayers: 0,
                    maxOutputTokens: Math.Clamp(options.MaxOutputTokens, 16, 4096),
                    temperature: options.Temperature,
                    topP: options.TopP,
                    seed: options.Seed,
                    useGpu: false,
                    flashAttention: false,
                    warmup: false));

                return ReadOcrResult(resultHandle);
            }
            finally
            {
                imageHandle.Free();
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CreateNativeRuntimeException("ocr", exception);
        }
    }

    public NativeImageResult GenerateImage(
        LocalModelDescriptor model,
        ImageGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureBackendReady("image-generation", model.Id);

        var started = DateTimeOffset.UtcNow;
        var bundle = ResolveStableDiffusionBundle(model);
        try
        {
            importResolver.Register();
            using var memory = new StableDiffusionInteropMemory(bundle, options);
            var contextParameters = new StableDiffusionContextParameters
            {
                ModelPath = nint.Zero,
                DiffusionModelPath = memory.DiffusionModelPath,
                ClipLPath = nint.Zero,
                ClipGPath = nint.Zero,
                T5XxlPath = nint.Zero,
                LlmPath = memory.LlmPath,
                VaePath = memory.VaePath,
                Threads = Environment.ProcessorCount,
                OffloadParamsToCpu = true,
                EnableMmap = false,
                KeepClipOnCpu = false,
                KeepVaeOnCpu = false,
                FlashAttention = false,
                DiffusionFlashAttention = true,
                VaeDecodeOnly = true,
                FreeParamsImmediately = true
            };

            using var contextHandle = new StableDiffusionContextHandle(
                MultimodalNativeMethods.StableDiffusionCreateContext(in contextParameters));
            if (contextHandle.IsInvalid)
            {
                throw new InferenceException(
                    "image_generation_context_failed",
                    "stable-diffusion.cpp could not create an image generation context.",
                    [
                        "Verify the diffusion model, VAE and text encoder bundle assets are installed.",
                        "Use /api/runtime/multimodal to inspect backend readiness."
                    ]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var generationParameters = new StableDiffusionGenerationParameters
            {
                Prompt = memory.Prompt,
                NegativePrompt = memory.NegativePrompt,
                SampleMethod = memory.SampleMethod,
                Scheduler = memory.Scheduler,
                Width = options.Width,
                Height = options.Height,
                Steps = options.Steps,
                CfgScale = options.CfgScale,
                DistilledGuidance = float.NaN,
                FlowShift = float.NaN,
                Seed = options.Seed
            };

            var generated = MultimodalNativeMethods.StableDiffusionGeneratePng(
                contextHandle.DangerousGetHandle(),
                in generationParameters,
                out var encodedImage);
            if (!generated || encodedImage.Data == nint.Zero || encodedImage.Length <= 0)
            {
                throw new InferenceException(
                    "image_generation_failed",
                    "stable-diffusion.cpp did not return an encoded PNG image.",
                    [
                        "Try a smaller size such as 1024x1024.",
                        "Verify the selected image model bundle is complete."
                    ]);
            }

            try
            {
                var bytes = new byte[encodedImage.Length];
                Marshal.Copy(encodedImage.Data, bytes, 0, encodedImage.Length);
                var diagnostics = new List<string>
                {
                    "source: stable-diffusion.cpp",
                    $"image-size: {options.Width}x{options.Height}",
                    $"steps: {options.Steps}",
                    $"cfg-scale: {options.CfgScale:0.##}",
                    $"sample-method: {NormalizeSamplingToken(options.SampleMethod)}",
                    $"scheduler: {NormalizeSamplingToken(options.Scheduler)}",
                    "format: png"
                };

                return new NativeImageResult(
                    bytes,
                    "png",
                    DateTimeOffset.UtcNow - started,
                    diagnostics);
            }
            finally
            {
                MultimodalNativeMethods.StableDiffusionFreeBuffer(encodedImage.Data);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CreateNativeRuntimeException("image-generation", exception);
        }
    }

    public static RuntimeDiagnostic CreateInvalidRequestDiagnostic(
        string route,
        string message,
        string? model = null)
    {
        return new RuntimeDiagnostic(
            "error",
            "invalid_request",
            message,
            string.IsNullOrWhiteSpace(model) ? null : model,
            [$"Fix the request body for {route}."]);
    }

    private static MultimodalOperationResponse CreateResponse(
        string route,
        MultimodalBackendStatus backend,
        string? model,
        RuntimeDiagnostic diagnostic,
        MultimodalInputSummary input)
    {
        return new MultimodalOperationResponse(
            diagnostic.Status,
            route,
            backend.Id,
            string.IsNullOrWhiteSpace(model) ? null : model,
            diagnostic.Message,
            diagnostic,
            backend,
            input);
    }

    private void EnsureBackendReady(string backendId, string model)
    {
        var backend = runtimeService.GetBackendStatus(backendId);
        if (string.Equals(backend.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostic = diagnosticsProvider.GetMultimodalRuntimeUnavailable(model, backend);
        throw new InferenceException(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Actions);
    }

    private StableDiffusionBundle ResolveStableDiffusionBundle(LocalModelDescriptor model)
    {
        var diffusionModelPath = ResolveRequiredBundleAsset(model, "diffusion-model");
        var vaePath = ResolveRequiredBundleAsset(model, "vae");
        var llmPath = ResolveRequiredBundleAsset(model, "llm-text-encoder");
        return new StableDiffusionBundle(diffusionModelPath, vaePath, llmPath);
    }

    private string ResolveRequiredBundleAsset(LocalModelDescriptor model, string assetKey)
    {
        if (string.IsNullOrWhiteSpace(model.PackageId))
        {
            throw new InferenceException(
                "model_bundle_asset_missing",
                $"The selected model '{model.Id}' is not installed from a bundle, so required asset '{assetKey}' could not be resolved.",
                ["Install the recommended multimodal package with tomur pull recommended."]);
        }

        var manifest = new InstallManifestStore(paths).Read();
        var package = manifest.Packages.FirstOrDefault(item =>
            string.Equals(item.Id, model.PackageId, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            throw new InferenceException(
                "model_bundle_asset_missing",
                $"The install manifest does not contain package '{model.PackageId}'.",
                ["Run tomur pull recommended to reinstall the required multimodal bundle."]);
        }

        var bundleAsset = package.BundleAssets.FirstOrDefault(item =>
            string.Equals(item.AssetKey, assetKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Role, assetKey, StringComparison.OrdinalIgnoreCase));
        if (bundleAsset is null)
        {
            throw new InferenceException(
                "model_bundle_asset_missing",
                $"Package '{package.Id}' does not declare required asset '{assetKey}'.",
                ["Use /api/models/installed to inspect package bundle assets."]);
        }

        var relativePath = ModelPackage.CombineUrlPath(package.Directory, bundleAsset.RelativePath);
        var asset = package.Assets.FirstOrDefault(item =>
            string.Equals(item.Path, relativePath, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            throw new InferenceException(
                "model_bundle_asset_missing",
                $"Required bundle asset '{relativePath}' is not registered as installed.",
                ["Run tomur pull recommended to reinstall the required multimodal bundle."]);
        }

        var path = Path.GetFullPath(Path.Combine(paths.ModelsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
        {
            throw new InferenceException(
                "model_bundle_asset_missing",
                $"Required bundle asset '{relativePath}' is missing from the models directory.",
                ["Run tomur pull recommended to repair the required multimodal bundle."]);
        }

        return path;
    }

    private InferenceException CreateNativeRuntimeException(string backendId, Exception exception)
    {
        var resolution = backendId switch
        {
            "vlm" => libraryResolver.Resolve("llama", "tomur-llama-vlm"),
            "ocr" => libraryResolver.Resolve("ocr", "tomur-ocr"),
            "image-generation" => libraryResolver.Resolve("stable-diffusion", "stable-diffusion"),
            _ => null
        };
        var detail = resolution is null ? exception.Message : $"{exception.Message} ({resolution.Message})";
        return new InferenceException(
            "native_runtime_unavailable",
            $"The {backendId} native runtime could not be used: {detail}",
            [
                "Run tomur native prepare to extract or repair the managed runtime bundle.",
                "Use /api/runtime/multimodal to inspect backend readiness."
            ],
            exception);
    }

    private static void EnsureImages(IReadOnlyList<ImageInputBytes> images)
    {
        if (images.Count == 0)
        {
            throw new InferenceException(
                "invalid_request",
                "At least one image is required.",
                ["Attach a data URI or base64 image payload."]);
        }
    }

    private static NativeOperationResult ReadVlmResult(VlmResultHandle handle)
    {
        if (handle.IsInvalid)
        {
            throw new InferenceException(
                "native_runtime_failure",
                "The VLM native bridge returned an empty result handle.",
                ["Use /api/runtime/multimodal to inspect backend readiness."]);
        }

        var result = Marshal.PtrToStructure<VlmResult>(handle.DangerousGetHandle());
        return ToOperationResult(
            "vlm",
            result.StatusCode,
            result.TextUtf8,
            result.DiagnosticsJson,
            result.ElapsedMs,
            result.ErrorUtf8);
    }

    private static NativeOperationResult ReadOcrResult(OcrResultHandle handle)
    {
        if (handle.IsInvalid)
        {
            throw new InferenceException(
                "native_runtime_failure",
                "The OCR native bridge returned an empty result handle.",
                ["Use /api/runtime/multimodal to inspect backend readiness."]);
        }

        var result = Marshal.PtrToStructure<OcrResult>(handle.DangerousGetHandle());
        return ToOperationResult(
            "ocr",
            result.StatusCode,
            result.Text,
            result.DiagnosticsJson,
            result.ElapsedMs,
            result.Error);
    }

    private static NativeOperationResult ToOperationResult(
        string backendId,
        int statusCode,
        nint textUtf8,
        nint diagnosticsJson,
        long elapsedMs,
        nint errorUtf8)
    {
        var text = Marshal.PtrToStringUTF8(textUtf8) ?? string.Empty;
        var diagnostics = ReadDiagnostics(diagnosticsJson);
        if (statusCode != 0)
        {
            var error = Marshal.PtrToStringUTF8(errorUtf8);
            throw new InferenceException(
                $"multimodal_{backendId}_execution_failed",
                string.IsNullOrWhiteSpace(error) ? $"{backendId} native execution failed with status {statusCode}." : error,
                diagnostics.Count == 0 ? ["Use /api/runtime/multimodal to inspect backend readiness."] : diagnostics);
        }

        return new NativeOperationResult(text, TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs)), diagnostics);
    }

    private static IReadOnlyList<string> ReadDiagnostics(nint diagnosticsJson)
    {
        var json = Marshal.PtrToStringUTF8(diagnosticsJson);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.StringArray) ?? [];
        }
        catch (JsonException)
        {
            return [json];
        }
    }

    private static string NormalizeSamplingToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();

    private sealed record StableDiffusionBundle(
        string DiffusionModelPath,
        string VaePath,
        string LlmPath);

    private sealed class VlmInteropMemory : IDisposable
    {
        private readonly List<GCHandle> pinnedImages = [];
        private readonly List<nint> utf8Strings = [];
        private readonly nint[] stopPointers;
        private readonly GCHandle stopPointersHandle;
        private readonly VlmImage[] nativeImages;
        private readonly GCHandle nativeImagesHandle;
        private bool disposed;

        public VlmInteropMemory(
            string modelPath,
            string mmprojPath,
            string prompt,
            IReadOnlyList<ImageInputBytes> images,
            IReadOnlyList<string> stopSequences)
        {
            ModelPath = AllocateUtf8(modelPath);
            MmprojPath = AllocateUtf8(mmprojPath);
            Prompt = AllocateUtf8(prompt);
            nativeImages = new VlmImage[images.Count];
            for (var index = 0; index < images.Count; index++)
            {
                var image = images[index];
                var imageHandle = GCHandle.Alloc(image.Bytes, GCHandleType.Pinned);
                pinnedImages.Add(imageHandle);
                nativeImages[index] = new VlmImage
                {
                    Data = imageHandle.AddrOfPinnedObject(),
                    Size = (nuint)image.Bytes.Length,
                    MediaType = string.IsNullOrWhiteSpace(image.MediaType) ? nint.Zero : AllocateUtf8(image.MediaType),
                    Detail = string.IsNullOrWhiteSpace(image.Detail) ? nint.Zero : AllocateUtf8(image.Detail)
                };
            }

            nativeImagesHandle = GCHandle.Alloc(nativeImages, GCHandleType.Pinned);
            Images = nativeImagesHandle.AddrOfPinnedObject();
            stopPointers = stopSequences
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(AllocateUtf8)
                .ToArray();
            StopSequenceCount = stopPointers.Length;
            stopPointersHandle = GCHandle.Alloc(stopPointers, GCHandleType.Pinned);
            StopSequences = StopSequenceCount == 0 ? nint.Zero : stopPointersHandle.AddrOfPinnedObject();
        }

        public nint ModelPath { get; }
        public nint MmprojPath { get; }
        public nint Prompt { get; }
        public nint Images { get; }
        public nint StopSequences { get; }
        public int StopSequenceCount { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (stopPointersHandle.IsAllocated)
            {
                stopPointersHandle.Free();
            }

            if (nativeImagesHandle.IsAllocated)
            {
                nativeImagesHandle.Free();
            }

            foreach (var handle in pinnedImages)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }

            foreach (var pointer in utf8Strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            disposed = true;
        }

        private nint AllocateUtf8(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            utf8Strings.Add(pointer);
            return pointer;
        }
    }

    private sealed class StableDiffusionInteropMemory : IDisposable
    {
        private readonly List<nint> utf8Strings = [];
        private bool disposed;

        public StableDiffusionInteropMemory(StableDiffusionBundle bundle, ImageGenerationOptions options)
        {
            DiffusionModelPath = AllocateUtf8(bundle.DiffusionModelPath);
            VaePath = AllocateUtf8(bundle.VaePath);
            LlmPath = AllocateUtf8(bundle.LlmPath);
            Prompt = AllocateUtf8(options.Prompt);
            NegativePrompt = string.IsNullOrWhiteSpace(options.NegativePrompt)
                ? nint.Zero
                : AllocateUtf8(options.NegativePrompt);
            SampleMethod = MarshalSamplingToken(options.SampleMethod);
            Scheduler = MarshalSamplingToken(options.Scheduler);
        }

        public nint DiffusionModelPath { get; }
        public nint VaePath { get; }
        public nint LlmPath { get; }
        public nint Prompt { get; }
        public nint NegativePrompt { get; }
        public nint SampleMethod { get; }
        public nint Scheduler { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            foreach (var pointer in utf8Strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            disposed = true;
        }

        private nint MarshalSamplingToken(string? value)
        {
            var normalized = NormalizeSamplingToken(value);
            return normalized == "auto" ? nint.Zero : AllocateUtf8(normalized);
        }

        private nint AllocateUtf8(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            utf8Strings.Add(pointer);
            return pointer;
        }
    }
}
