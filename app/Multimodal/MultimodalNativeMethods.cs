using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tomur.Multimodal;

internal static partial class MultimodalNativeMethods
{
    private const string VlmLibraryName = "tomur-llama-vlm";
    private const string OcrLibraryName = "tomur-ocr";
    private const string StableDiffusionLibraryName = "stable-diffusion";

    [LibraryImport(VlmLibraryName, EntryPoint = "tomur_llama_vlm_generate", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint LlamaVlmGenerate(ref VlmRequest request);

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

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_create_ctx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint StableDiffusionCreateContext(in StableDiffusionContextParameters parameters);

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_free_ctx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void StableDiffusionFreeContext(nint context);

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_generate_png")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool StableDiffusionGeneratePng(
        nint context,
        in StableDiffusionGenerationParameters parameters,
        out StableDiffusionEncodedImage image);

    [LibraryImport(StableDiffusionLibraryName, EntryPoint = "tomur_sd_free_buffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void StableDiffusionFreeBuffer(nint buffer);

    internal static void FreeLlamaVlmResult(nint result)
        => LlamaVlmResultFree(result);

    internal static void FreeOcrResult(nint result)
        => OcrResultFree(result);

    internal static void FreeStableDiffusionContext(nint context)
        => StableDiffusionFreeContext(context);
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
