using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tomur.Config;
using Tomur.Hardware;
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
    private readonly HardwareAccelerationService? accelerationService;
    private readonly NativeRuntimePreference runtimePreference;

    public MultimodalExecutionService(
        MultimodalRuntimeService runtimeService,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        DataPaths paths,
        LlamaImportResolver importResolver,
        INativeLibraryResolver libraryResolver,
        HardwareAccelerationService? accelerationService = null,
        NativeRuntimePreference? runtimePreference = null)
    {
        this.runtimeService = runtimeService;
        this.diagnosticsProvider = diagnosticsProvider;
        this.paths = paths;
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
        this.accelerationService = accelerationService;
        this.runtimePreference = runtimePreference ?? new NativeRuntimePreference();
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
            var acceleration = ResolveAcceleration(model);
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
                GpuLayers = acceleration.EffectiveGpuLayers,
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
                UseGpu = acceleration.EffectiveGpuLayers > 0,
                FlashAttention = acceleration.EffectiveGpuLayers > 0,
                Warmup = false
            };

            using var resultHandle = new VlmResultHandle(MultimodalNativeMethods.LlamaVlmGenerate(ref request));
            return AddAccelerationDiagnostics(
                ReadVlmResult(resultHandle),
                acceleration,
                nativeRuntime: null);
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
            var acceleration = ResolveAcceleration(model);
            var nativeRuntime = ResolveNativeRuntime("ocr", "tomur-ocr", acceleration);
            using var variantScope = runtimePreference.UsePreferredVariant(nativeRuntime.EffectiveVariant);
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
                    gpuLayers: nativeRuntime.GpuLayers,
                    maxOutputTokens: Math.Clamp(options.MaxOutputTokens, 16, 4096),
                    temperature: options.Temperature,
                    topP: options.TopP,
                    seed: options.Seed,
                    useGpu: nativeRuntime.UseGpu,
                    flashAttention: nativeRuntime.UseGpu,
                    warmup: false));

                return AddAccelerationDiagnostics(
                    ReadOcrResult(resultHandle),
                    acceleration,
                    nativeRuntime);
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
        var acceleration = ResolveAcceleration(model);
        var nativeRuntime = ResolveNativeRuntime("stable-diffusion", "stable-diffusion", acceleration);
        using var variantScope = runtimePreference.UsePreferredVariant(nativeRuntime.EffectiveVariant);
        try
        {
            importResolver.Register();
            using var memory = new StableDiffusionInteropMemory(bundle, options, ResolveStableDiffusionRuntimeBackend(nativeRuntime));
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
                OffloadParamsToCpu = ToNativeBool(!nativeRuntime.UseGpu),
                EnableMmap = ToNativeBool(false),
                KeepClipOnCpu = ToNativeBool(false),
                KeepVaeOnCpu = ToNativeBool(false),
                FlashAttention = ToNativeBool(nativeRuntime.UseGpu),
                DiffusionFlashAttention = ToNativeBool(nativeRuntime.UseGpu),
                VaeDecodeOnly = ToNativeBool(true),
                FreeParamsImmediately = ToNativeBool(true),
                Backend = memory.Backend,
                ParamsBackend = memory.ParamsBackend
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
                DistilledGuidance = options.DistilledGuidance ?? float.NaN,
                FlowShift = options.FlowShift ?? float.NaN,
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
                    $"backend: {memory.BackendLabel}",
                    $"params-backend: {memory.ParamsBackendLabel}",
                    $"diffusion-flash-attention: {nativeRuntime.UseGpu}",
                    $"acceleration: {acceleration.Status}",
                    $"accelerator: {acceleration.SelectedAcceleratorKey ?? "cpu"}",
                    $"gpu-layers: {nativeRuntime.GpuLayers}",
                    $"use-gpu: {nativeRuntime.UseGpu}",
                    $"native-library: {nativeRuntime.Resolution.Path}",
                    $"native-component-path: {nativeRuntime.Resolution.ComponentRuntimePath}",
                    $"native-variant-requested: {nativeRuntime.RequestedVariant}",
                    $"native-variant: {nativeRuntime.EffectiveVariant}",
                    "format: png"
                };
                if (options.DistilledGuidance is { } distilledGuidance)
                {
                    diagnostics.Add($"distilled-guidance: {distilledGuidance:0.##}");
                }

                if (options.FlowShift is { } flowShift)
                {
                    diagnostics.Add($"flow-shift: {flowShift:0.##}");
                }

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

    public NativeOperationResult TranscribeAudio(
        LocalModelDescriptor model,
        byte[] audioBytes,
        string? language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureBackendReady("asr", model.Id);

        var started = DateTimeOffset.UtcNow;
        var samples = DecodeWavPcm16To16KhzMono(audioBytes);
        if (samples.Length == 0)
        {
            throw new InferenceException(
                "invalid_audio",
                "The audio payload did not contain any PCM samples.",
                ["Send a mono or stereo PCM16 WAV file with a non-empty data chunk."]);
        }

        try
        {
            importResolver.Register();
            var acceleration = ResolveAcceleration(model);
            var nativeRuntime = ResolveNativeRuntime("whisper", "whisper", acceleration);
            using var variantScope = runtimePreference.UsePreferredVariant(nativeRuntime.EffectiveVariant);
            var contextParameters = MultimodalNativeMethods.WhisperContextDefaultParameters();
            contextParameters.UseGpu = nativeRuntime.UseGpu;
            contextParameters.FlashAttention = nativeRuntime.UseGpu;
            contextParameters.GpuDevice = nativeRuntime.GpuDeviceIndex;

            using var contextHandle = new WhisperContextHandle(
                MultimodalNativeMethods.WhisperInitFromFileWithParams(model.AbsolutePath, contextParameters));
            if (contextHandle.IsInvalid)
            {
                throw new InferenceException(
                    "asr_context_failed",
                    "whisper.cpp could not create an ASR context.",
                    [
                        "Verify the Whisper model file is installed and not corrupted.",
                        "Use /api/runtime/multimodal to inspect backend readiness."
                    ]);
            }

            using var parametersHandle = new WhisperParametersHandle(
                MultimodalNativeMethods.WhisperFullDefaultParametersByReference(WhisperSamplingStrategy.Greedy));
            if (parametersHandle.IsInvalid)
            {
                throw new InferenceException(
                    "asr_parameters_failed",
                    "whisper.cpp could not create ASR parameters.",
                    ["Use /api/runtime/multimodal to inspect backend readiness."]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var normalizedLanguage = NormalizeLanguage(language);
            var statusCode = MultimodalNativeMethods.WhisperFull(
                contextHandle.DangerousGetHandle(),
                parametersHandle.DangerousGetHandle(),
                samples,
                normalizedLanguage,
                detectLanguage: string.IsNullOrWhiteSpace(normalizedLanguage),
                translate: false);
            if (statusCode != 0)
            {
                throw new InferenceException(
                    "asr_execution_failed",
                    $"whisper.cpp transcription failed with status {statusCode}.",
                    ["Verify the audio is a 16-bit PCM WAV file and the selected model is a Whisper model."]);
            }

            var segmentCount = MultimodalNativeMethods.WhisperFullSegmentCount(contextHandle.DangerousGetHandle());
            var transcript = new StringBuilder();
            for (var index = 0; index < segmentCount; index++)
            {
                var textPointer = MultimodalNativeMethods.WhisperFullGetSegmentText(contextHandle.DangerousGetHandle(), index);
                var text = Marshal.PtrToStringUTF8(textPointer);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    transcript.Append(text.Trim()).Append(' ');
                }
            }

            var diagnostics = new List<string>
            {
                "source: whisper.cpp",
                "audio-format: wav/pcm16",
                "sample-rate: 16000",
                $"samples: {samples.Length}",
                $"segments: {segmentCount}",
                $"acceleration: {acceleration.Status}",
                $"accelerator: {acceleration.SelectedAcceleratorKey ?? "cpu"}",
                $"gpu-device: {nativeRuntime.GpuDeviceIndex}",
                $"gpu-layers: {nativeRuntime.GpuLayers}",
                $"use-gpu: {contextParameters.UseGpu}",
                $"native-library: {nativeRuntime.Resolution.Path}",
                $"native-component-path: {nativeRuntime.Resolution.ComponentRuntimePath}",
                $"native-variant-requested: {nativeRuntime.RequestedVariant}",
                $"native-variant: {nativeRuntime.EffectiveVariant}"
            };
            if (!string.IsNullOrWhiteSpace(normalizedLanguage))
            {
                diagnostics.Add($"language: {normalizedLanguage}");
            }

            return new NativeOperationResult(
                transcript.ToString().Trim(),
                DateTimeOffset.UtcNow - started,
                diagnostics);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CreateNativeRuntimeException("asr", exception);
        }
    }

    public NativeAudioResult SynthesizeSpeech(
        LocalModelDescriptor model,
        SpeechSynthesisOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureBackendReady("tts", model.Id);

        var started = DateTimeOffset.UtcNow;
        var voiceModelPath = ResolveRequiredBundleAsset(model, "wavtokenizer");
        try
        {
            importResolver.Register();
            var acceleration = ResolveAcceleration(model);
            var nativeRuntime = ResolveNativeRuntime("tts", "tomur-tts", acceleration);
            using var variantScope = runtimePreference.UsePreferredVariant(nativeRuntime.EffectiveVariant);
            using var memory = new TtsInteropMemory(model.AbsolutePath, voiceModelPath, options);
            var request = new TtsRequest
            {
                TextUtf8 = memory.Text,
                AcousticModelPath = memory.AcousticModelPath,
                VoiceModelPath = memory.VoiceModelPath,
                SpeakerPromptUtf8 = memory.SpeakerPrompt,
                SampleRate = 24000,
                Threads = Environment.ProcessorCount,
                GpuLayers = nativeRuntime.GpuLayers
            };

            using var resultHandle = new TtsResultHandle(MultimodalNativeMethods.TtsSynthesizeToPcm(in request));
            if (resultHandle.IsInvalid)
            {
                throw new InferenceException(
                    "tts_execution_failed",
                    "The TTS native bridge returned an empty result handle.",
                    ["Use /api/runtime/multimodal to inspect backend readiness."]);
            }

            var result = Marshal.PtrToStructure<TtsResult>(resultHandle.DangerousGetHandle());
            var diagnostics = ReadDiagnostics(result.DiagnosticsJson);
            if (result.StatusCode != 0)
            {
                var error = Marshal.PtrToStringUTF8(result.ErrorUtf8);
                throw new InferenceException(
                    "tts_execution_failed",
                    string.IsNullOrWhiteSpace(error) ? $"TTS native execution failed with status {result.StatusCode}." : error,
                    diagnostics.Count == 0 ? ["Use /api/runtime/multimodal to inspect backend readiness."] : diagnostics);
            }

            if (result.Pcm == nint.Zero || result.PcmLength == 0)
            {
                throw new InferenceException(
                    "tts_execution_failed",
                    "TTS native execution completed without returning PCM samples.",
                    diagnostics.Count == 0 ? ["Verify the selected TTS model bundle is complete."] : diagnostics);
            }

            if (result.PcmLength > int.MaxValue / sizeof(short))
            {
                throw new InferenceException(
                    "tts_audio_too_large",
                    "TTS native execution returned too many PCM samples for this process.",
                    ["Use a shorter input for /v1/audio/speech."]);
            }

            var sampleCount = checked((int)result.PcmLength);
            var pcmBytes = new byte[checked(sampleCount * sizeof(short))];
            Marshal.Copy(result.Pcm, pcmBytes, 0, pcmBytes.Length);
            var sampleRate = result.SampleRate <= 0 ? 24000 : result.SampleRate;
            var wavBytes = EncodePcm16Wav(pcmBytes, sampleRate, channels: 1);
            var outputDiagnostics = AddAccelerationDiagnostics(diagnostics, acceleration, nativeRuntime);
            return new NativeAudioResult(
                wavBytes,
                "wav",
                "audio/wav",
                sampleRate,
                DateTimeOffset.UtcNow - started,
                outputDiagnostics);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CreateNativeRuntimeException("tts", exception);
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
            "asr" => libraryResolver.Resolve("whisper", "whisper"),
            "tts" => libraryResolver.Resolve("tts", "tomur-tts"),
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

    private AccelerationPlan ResolveAcceleration(LocalModelDescriptor model)
        => accelerationService?.ResolvePlan(model)
            ?? new AccelerationPlan(
                "cpu",
                "cpu",
                "cpu",
                0,
                0,
                0,
                null,
                null,
                null,
                null,
                false,
                null,
                "Acceleration service is not available in this execution context.",
                [],
                [],
                ["Acceleration service is not available in this execution context; CPU runtime variant is used."]);

    private NativeRuntimeSelection ResolveNativeRuntime(
        string componentId,
        string libraryName,
        AccelerationPlan acceleration)
    {
        var requestedVariant = ResolveNativeVariant(acceleration);
        var resolution = libraryResolver.Resolve(componentId, libraryName, requestedVariant);
        var effectiveVariant = string.IsNullOrWhiteSpace(resolution.Variant)
            ? requestedVariant
            : resolution.Variant;
        var useGpu = IsCudaAcceleration(acceleration) &&
            string.Equals(effectiveVariant, requestedVariant, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(effectiveVariant, "cuda13", StringComparison.OrdinalIgnoreCase) &&
            resolution.Exists &&
            !string.Equals(resolution.ChecksumStatus, "mismatch", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolution.ComponentStatus, "error", StringComparison.OrdinalIgnoreCase);

        return new NativeRuntimeSelection(
            requestedVariant,
            effectiveVariant,
            resolution,
            useGpu ? acceleration.EffectiveGpuLayers : 0,
            useGpu,
            useGpu ? ResolveGpuDeviceIndex(acceleration) : 0);
    }

    private static NativeOperationResult AddAccelerationDiagnostics(
        NativeOperationResult result,
        AccelerationPlan acceleration,
        NativeRuntimeSelection? nativeRuntime)
        => result with
        {
            Diagnostics = AddAccelerationDiagnostics(result.Diagnostics, acceleration, nativeRuntime)
        };

    private static IReadOnlyList<string> AddAccelerationDiagnostics(
        IReadOnlyList<string> diagnostics,
        AccelerationPlan acceleration,
        NativeRuntimeSelection? nativeRuntime)
    {
        var output = new List<string>(diagnostics);
        output.Add($"acceleration: {acceleration.Status}");
        output.Add($"accelerator: {acceleration.SelectedAcceleratorKey ?? "cpu"}");

        if (nativeRuntime is not null)
        {
            output.Add($"gpu-device: {nativeRuntime.GpuDeviceIndex}");
            output.Add($"gpu-layers: {nativeRuntime.GpuLayers}");
            output.Add($"use-gpu: {nativeRuntime.UseGpu}");
            output.Add($"native-library: {nativeRuntime.Resolution.Path}");
            output.Add($"native-component-path: {nativeRuntime.Resolution.ComponentRuntimePath}");
            output.Add($"native-variant-requested: {nativeRuntime.RequestedVariant}");
            output.Add($"native-variant: {nativeRuntime.EffectiveVariant}");
        }

        return output;
    }

    private static string ResolveNativeVariant(AccelerationPlan acceleration)
        => IsCudaAcceleration(acceleration) ? "cuda13" : "cpu";

    private static int ResolveGpuDeviceIndex(AccelerationPlan acceleration)
        => IsCudaAcceleration(acceleration) && acceleration.SelectedAccelerator is { } accelerator
            ? accelerator.DeviceIndex
            : 0;

    private static string ResolveStableDiffusionRuntimeBackend(NativeRuntimeSelection nativeRuntime)
        => nativeRuntime.UseGpu
            ? $"cuda{nativeRuntime.GpuDeviceIndex}"
            : "cpu";

    private static bool IsCudaAcceleration(AccelerationPlan acceleration)
        => acceleration.EffectiveGpuLayers > 0 &&
            string.Equals(acceleration.EffectiveBackend, "cuda", StringComparison.OrdinalIgnoreCase);

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

    private static byte ToNativeBool(bool value) => value ? (byte)1 : (byte)0;

    private static string NormalizeSamplingToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "auto" ? null : normalized;
    }

    private static float[] DecodeWavPcm16To16KhzMono(byte[] bytes)
    {
        if (bytes.Length < 44 ||
            !HasAscii(bytes, 0, "RIFF") ||
            !HasAscii(bytes, 8, "WAVE"))
        {
            throw new InferenceException(
                "unsupported_audio_format",
                "Only PCM16 WAV audio is supported by the local ASR adapter.",
                [
                    "Convert the audio to 16-bit PCM WAV before sending /v1/audio/transcriptions.",
                    "For smoke tests, use 16 kHz mono WAV."
                ]);
        }

        var offset = 12;
        ushort audioFormat = 0;
        ushort channels = 0;
        var sampleRate = 0;
        ushort bitsPerSample = 0;
        var dataOffset = -1;
        var dataSize = 0;

        while (offset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = ReadInt32LittleEndian(bytes, offset + 4);
            if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length)
            {
                throw new InferenceException(
                    "invalid_audio",
                    "The WAV file contains an invalid chunk size.",
                    ["Send a valid PCM16 WAV file."]);
            }

            var chunkDataOffset = offset + 8;
            if (string.Equals(chunkId, "fmt ", StringComparison.Ordinal))
            {
                if (chunkSize < 16)
                {
                    throw new InferenceException(
                        "invalid_audio",
                        "The WAV fmt chunk is too short.",
                        ["Send a valid PCM16 WAV file."]);
                }

                audioFormat = ReadUInt16LittleEndian(bytes, chunkDataOffset);
                channels = ReadUInt16LittleEndian(bytes, chunkDataOffset + 2);
                sampleRate = ReadInt32LittleEndian(bytes, chunkDataOffset + 4);
                bitsPerSample = ReadUInt16LittleEndian(bytes, chunkDataOffset + 14);
            }
            else if (string.Equals(chunkId, "data", StringComparison.Ordinal))
            {
                dataOffset = chunkDataOffset;
                dataSize = chunkSize;
            }

            offset = chunkDataOffset + chunkSize + (chunkSize % 2);
        }

        if (audioFormat != 1 || bitsPerSample != 16 || channels is < 1 or > 2 || sampleRate <= 0)
        {
            throw new InferenceException(
                "unsupported_audio_format",
                "The ASR adapter currently supports mono or stereo PCM16 WAV audio only.",
                ["Convert the audio to 16-bit PCM WAV before sending /v1/audio/transcriptions."]);
        }

        if (dataOffset < 0 || dataSize <= 0)
        {
            throw new InferenceException(
                "invalid_audio",
                "The WAV file does not contain a non-empty data chunk.",
                ["Send a valid PCM16 WAV file."]);
        }

        var frameSize = channels * sizeof(short);
        var frameCount = dataSize / frameSize;
        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var baseOffset = dataOffset + frame * frameSize;
            var left = ReadInt16LittleEndian(bytes, baseOffset) / 32768f;
            if (channels == 1)
            {
                mono[frame] = left;
                continue;
            }

            var right = ReadInt16LittleEndian(bytes, baseOffset + sizeof(short)) / 32768f;
            mono[frame] = (left + right) * 0.5f;
        }

        return sampleRate == 16000 ? mono : ResampleLinear(mono, sampleRate, 16000);
    }

    private static float[] ResampleLinear(float[] samples, int sourceRate, int targetRate)
    {
        if (samples.Length == 0 || sourceRate == targetRate)
        {
            return samples;
        }

        var outputLength = Math.Max(1, (int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];
        var ratio = (double)sourceRate / targetRate;
        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * ratio;
            var leftIndex = Math.Min(samples.Length - 1, (int)Math.Floor(sourcePosition));
            var rightIndex = Math.Min(samples.Length - 1, leftIndex + 1);
            var fraction = sourcePosition - leftIndex;
            output[index] = (float)(samples[leftIndex] + (samples[rightIndex] - samples[leftIndex]) * fraction);
        }

        return output;
    }

    private static byte[] EncodePcm16Wav(byte[] pcmBytes, int sampleRate, short channels)
    {
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var output = new byte[44 + pcmBytes.Length];

        WriteAscii(output, 0, "RIFF");
        WriteInt32LittleEndian(output, 4, 36 + pcmBytes.Length);
        WriteAscii(output, 8, "WAVE");
        WriteAscii(output, 12, "fmt ");
        WriteInt32LittleEndian(output, 16, 16);
        WriteInt16LittleEndian(output, 20, 1);
        WriteInt16LittleEndian(output, 22, channels);
        WriteInt32LittleEndian(output, 24, sampleRate);
        WriteInt32LittleEndian(output, 28, byteRate);
        WriteInt16LittleEndian(output, 32, blockAlign);
        WriteInt16LittleEndian(output, 34, bitsPerSample);
        WriteAscii(output, 36, "data");
        WriteInt32LittleEndian(output, 40, pcmBytes.Length);
        Buffer.BlockCopy(pcmBytes, 0, output, 44, pcmBytes.Length);
        return output;
    }

    private static bool HasAscii(byte[] bytes, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (bytes[offset + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteAscii(byte[] bytes, int offset, string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            bytes[offset + index] = (byte)value[index];
        }
    }

    private static short ReadInt16LittleEndian(byte[] bytes, int offset)
        => unchecked((short)ReadUInt16LittleEndian(bytes, offset));

    private static ushort ReadUInt16LittleEndian(byte[] bytes, int offset)
        => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static int ReadInt32LittleEndian(byte[] bytes, int offset)
        => bytes[offset] |
            (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) |
            (bytes[offset + 3] << 24);

    private static void WriteInt16LittleEndian(byte[] bytes, int offset, short value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32LittleEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private sealed record StableDiffusionBundle(
        string DiffusionModelPath,
        string VaePath,
        string LlmPath);

    private sealed record NativeRuntimeSelection(
        string RequestedVariant,
        string EffectiveVariant,
        NativeLibraryResolution Resolution,
        int GpuLayers,
        bool UseGpu,
        int GpuDeviceIndex);

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

        public StableDiffusionInteropMemory(
            StableDiffusionBundle bundle,
            ImageGenerationOptions options,
            string runtimeBackend)
        {
            var backend = string.IsNullOrWhiteSpace(runtimeBackend) ? "cpu" : runtimeBackend.Trim();
            var paramsBackend = string.Equals(backend, "cpu", StringComparison.OrdinalIgnoreCase)
                ? "cpu"
                : backend;

            DiffusionModelPath = AllocateUtf8(bundle.DiffusionModelPath);
            VaePath = AllocateUtf8(bundle.VaePath);
            LlmPath = AllocateUtf8(bundle.LlmPath);
            Prompt = AllocateUtf8(options.Prompt);
            NegativePrompt = string.IsNullOrWhiteSpace(options.NegativePrompt)
                ? nint.Zero
                : AllocateUtf8(options.NegativePrompt);
            SampleMethod = MarshalSamplingToken(options.SampleMethod);
            Scheduler = MarshalSamplingToken(options.Scheduler);
            BackendLabel = backend;
            ParamsBackendLabel = paramsBackend;
            Backend = AllocateUtf8(backend);
            ParamsBackend = AllocateUtf8(paramsBackend);
        }

        public nint DiffusionModelPath { get; }
        public nint VaePath { get; }
        public nint LlmPath { get; }
        public nint Prompt { get; }
        public nint NegativePrompt { get; }
        public nint SampleMethod { get; }
        public nint Scheduler { get; }
        public nint Backend { get; }
        public nint ParamsBackend { get; }
        public string BackendLabel { get; }
        public string ParamsBackendLabel { get; }

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

    private sealed class TtsInteropMemory : IDisposable
    {
        private readonly List<nint> utf8Strings = [];
        private bool disposed;

        public TtsInteropMemory(string acousticModelPath, string voiceModelPath, SpeechSynthesisOptions options)
        {
            Text = AllocateUtf8(options.Text);
            AcousticModelPath = AllocateUtf8(acousticModelPath);
            VoiceModelPath = AllocateUtf8(voiceModelPath);
            var speakerFile = ResolveSpeakerFile(options.Voice);
            SpeakerPrompt = string.IsNullOrWhiteSpace(speakerFile)
                ? nint.Zero
                : AllocateUtf8(speakerFile);
        }

        public nint Text { get; }
        public nint AcousticModelPath { get; }
        public nint VoiceModelPath { get; }
        public nint SpeakerPrompt { get; }

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

        private nint AllocateUtf8(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            utf8Strings.Add(pointer);
            return pointer;
        }

        private static string? ResolveSpeakerFile(string? voice)
        {
            if (string.IsNullOrWhiteSpace(voice))
            {
                return null;
            }

            var trimmed = voice.Trim();
            if (!trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.IsPathFullyQualified(trimmed) && File.Exists(trimmed)
                ? trimmed
                : null;
        }
    }
}
