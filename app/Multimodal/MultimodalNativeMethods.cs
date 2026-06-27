using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tomur.Multimodal;

internal static partial class MultimodalNativeMethods
{
    private const string VlmLibraryName = "tomur-llama-vlm";
    private const string OcrLibraryName = "tomur-ocr";
    private const string StableDiffusionLibraryName = "stable-diffusion";
    private const string WhisperLibraryName = "whisper";
    private const string TtsLibraryName = "tomur-tts";

    [DllImport(VlmLibraryName, EntryPoint = "tomur_llama_vlm_generate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint LlamaVlmGenerate(ref VlmRequest request);

    [LibraryImport(VlmLibraryName, EntryPoint = "tomur_llama_vlm_result_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void LlamaVlmResultFree(nint result);

    [LibraryImport(OcrLibraryName, EntryPoint = "tomur_paddleocrvl_recognize_image", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint OcrRecognizeImage(
        string modelPath,
        string mmprojPath,
        nint imageData,
        nuint imageLength,
        string? prompt,
        string? language,
        int contextSize,
        int batchSize,
        int threads,
        int gpuLayers,
        int maxOutputTokens,
        float temperature,
        float topP,
        int seed,
        [MarshalAs(UnmanagedType.I1)] bool useGpu,
        [MarshalAs(UnmanagedType.I1)] bool flashAttention,
        [MarshalAs(UnmanagedType.I1)] bool warmup);

    [LibraryImport(OcrLibraryName, EntryPoint = "tomur_paddleocrvl_result_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OcrResultFree(nint result);

    [DllImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_create_ctx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint StableDiffusionCreateContext(in StableDiffusionContextParameters parameters);

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_free_ctx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void StableDiffusionFreeContext(nint context);

    [DllImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_generate_png")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool StableDiffusionGeneratePng(
        nint context,
        in StableDiffusionGenerationParameters parameters,
        out StableDiffusionEncodedImage image);

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_free_buffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void StableDiffusionFreeBuffer(nint buffer);

    [DllImport(WhisperLibraryName, EntryPoint = "whisper_context_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern WhisperContextParameters WhisperContextDefaultParameters();

    [DllImport(WhisperLibraryName, EntryPoint = "whisper_init_from_file_with_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint WhisperInitFromFileWithParams(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        WhisperContextParameters parameters);

    [LibraryImport(WhisperLibraryName, EntryPoint = "whisper_full_default_params_by_ref")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint WhisperFullDefaultParametersByReference(WhisperSamplingStrategy strategy);

    [LibraryImport(WhisperLibraryName, EntryPoint = "tomur_whisper_full_with_params", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int WhisperFullWithParameters(
        nint context,
        nint parameters,
        string? language,
        [MarshalAs(UnmanagedType.I1)] bool detectLanguage,
        [MarshalAs(UnmanagedType.I1)] bool translate,
        float* samples,
        int sampleCount);

    [LibraryImport(WhisperLibraryName, EntryPoint = "whisper_full_n_segments")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int WhisperFullSegmentCount(nint context);

    [LibraryImport(WhisperLibraryName, EntryPoint = "whisper_full_get_segment_text")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint WhisperFullGetSegmentText(nint context, int segmentIndex);

    [LibraryImport(WhisperLibraryName, EntryPoint = "whisper_free_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void WhisperFreeParameters(nint parameters);

    [LibraryImport(WhisperLibraryName, EntryPoint = "whisper_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void WhisperFree(nint context);

    [DllImport(TtsLibraryName, EntryPoint = "tomur_tts_synthesize_to_pcm")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint TtsSynthesizeToPcm(in TtsRequest request);

    [LibraryImport(TtsLibraryName, EntryPoint = "tomur_tts_result_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void TtsResultFree(nint result);

    internal static void FreeLlamaVlmResult(nint result)
        => LlamaVlmResultFree(result);

    internal static void FreeOcrResult(nint result)
        => OcrResultFree(result);

    internal static void FreeStableDiffusionContext(nint context)
        => StableDiffusionFreeContext(context);

    internal static void FreeWhisperContext(nint context)
        => WhisperFree(context);

    internal static void FreeTtsResult(nint result)
        => TtsResultFree(result);

    internal static unsafe int WhisperFull(
        nint context,
        nint parameters,
        float[] samples,
        string? language,
        bool detectLanguage,
        bool translate)
    {
        fixed (float* samplesPointer = samples)
        {
            return WhisperFullWithParameters(
                context,
                parameters,
                language,
                detectLanguage,
                translate,
                samplesPointer,
                samples.Length);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct VlmImage
{
    public nint Data;
    public nuint Size;
    public nint MediaType;
    public nint Detail;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VlmRequest
{
    public nint ModelPath;
    public nint MmprojPath;
    public nint PromptUtf8;
    public nint Images;
    public nuint ImageCount;
    public int ContextSize;
    public int BatchSize;
    public int Threads;
    public int GpuLayers;
    public int MaxOutputTokens;
    public float Temperature;
    public float TopP;
    public int TopK;
    public int PenaltyLastTokens;
    public float RepeatPenalty;
    public float FrequencyPenalty;
    public float PresencePenalty;
    public int Seed;
    public nint StopSequences;
    public nuint StopSequenceCount;

    [MarshalAs(UnmanagedType.I1)]
    public bool UseGpu;

    [MarshalAs(UnmanagedType.I1)]
    public bool FlashAttention;

    [MarshalAs(UnmanagedType.I1)]
    public bool Warmup;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VlmResult
{
    public int StatusCode;
    public nint TextUtf8;
    public nint DiagnosticsJson;
    public long ElapsedMs;
    public nint ErrorUtf8;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OcrResult
{
    public int StatusCode;
    public nint Text;
    public double Confidence;
    public nint DiagnosticsJson;
    public long ElapsedMs;
    public nint Error;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StableDiffusionContextParameters
{
    public nint ModelPath;
    public nint DiffusionModelPath;
    public nint ClipLPath;
    public nint ClipGPath;
    public nint T5XxlPath;
    public nint LlmPath;
    public nint VaePath;
    public int Threads;

    [MarshalAs(UnmanagedType.I1)]
    public bool OffloadParamsToCpu;

    [MarshalAs(UnmanagedType.I1)]
    public bool EnableMmap;

    [MarshalAs(UnmanagedType.I1)]
    public bool KeepClipOnCpu;

    [MarshalAs(UnmanagedType.I1)]
    public bool KeepVaeOnCpu;

    [MarshalAs(UnmanagedType.I1)]
    public bool FlashAttention;

    [MarshalAs(UnmanagedType.I1)]
    public bool DiffusionFlashAttention;

    [MarshalAs(UnmanagedType.I1)]
    public bool VaeDecodeOnly;

    [MarshalAs(UnmanagedType.I1)]
    public bool FreeParamsImmediately;

    public nint Backend;
    public nint ParamsBackend;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StableDiffusionGenerationParameters
{
    public nint Prompt;
    public nint NegativePrompt;
    public nint SampleMethod;
    public nint Scheduler;
    public int Width;
    public int Height;
    public int Steps;
    public float CfgScale;
    public float DistilledGuidance;
    public float FlowShift;
    public long Seed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StableDiffusionEncodedImage
{
    public nint Data;
    public int Length;
}

internal enum WhisperSamplingStrategy
{
    Greedy = 0,
    BeamSearch = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct WhisperContextParameters
{
    [MarshalAs(UnmanagedType.I1)]
    public bool UseGpu;

    [MarshalAs(UnmanagedType.I1)]
    public bool FlashAttention;

    public int GpuDevice;

    [MarshalAs(UnmanagedType.I1)]
    public bool DtwTokenTimestamps;

    public int DtwAheadsPreset;
    public int DtwNTop;
    public nuint DtwAheadsNHeads;
    public nint DtwAheadsHeads;
    public nuint DtwMemSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TtsRequest
{
    public nint TextUtf8;
    public nint AcousticModelPath;
    public nint VoiceModelPath;
    public nint SpeakerPromptUtf8;
    public int SampleRate;
    public int Threads;
    public int GpuLayers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TtsResult
{
    public int StatusCode;
    public nint Pcm;
    public nuint PcmLength;
    public int SampleRate;
    public nint DiagnosticsJson;
    public nint ErrorUtf8;
}

internal sealed class VlmResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public VlmResultHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.FreeLlamaVlmResult(handle);
        return true;
    }
}

internal sealed class OcrResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OcrResultHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.FreeOcrResult(handle);
        return true;
    }
}

internal sealed class StableDiffusionContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public StableDiffusionContextHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.FreeStableDiffusionContext(handle);
        return true;
    }
}

internal sealed class WhisperContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public WhisperContextHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.FreeWhisperContext(handle);
        return true;
    }
}

internal sealed class WhisperParametersHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public WhisperParametersHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.WhisperFreeParameters(handle);
        return true;
    }
}

internal sealed class TtsResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public TtsResultHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        MultimodalNativeMethods.FreeTtsResult(handle);
        return true;
    }
}
