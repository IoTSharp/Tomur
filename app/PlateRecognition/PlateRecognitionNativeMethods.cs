using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tomur.PlateRecognition;

/// <summary>声明 Tomur 车牌识别桥接的稳定 C ABI。</summary>
internal static partial class PlateRecognitionNativeMethods
{
    private const string LibraryName = "tomur-plate";

    /// <summary>使用编码图片和 HyperLPR3 模型目录执行一次车牌识别。</summary>
    [LibraryImport(LibraryName, EntryPoint = "tomur_plate_recognize_image", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial nint RecognizeImage(
        string modelDirectory,
        byte* imageData,
        nuint imageLength,
        int maximumResults,
        float minimumConfidence);

    /// <summary>释放原生桥接分配的结果及其 UTF-8 字符串。</summary>
    [LibraryImport(LibraryName, EntryPoint = "tomur_plate_result_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void FreeResult(nint result);

    /// <summary>供 SafeHandle 在唯一位置释放原生结果。</summary>
    internal static void ReleaseResult(nint result)
        => FreeResult(result);
}

/// <summary>与 tomur_plate_result C 结构保持字段顺序一致。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PlateRecognitionNativeResult
{
    public int StatusCode;
    public nint JsonUtf8;
    public long ElapsedMs;
    public nint ErrorUtf8;
}

/// <summary>确保异常路径也能释放原生车牌识别结果。</summary>
internal sealed class PlateRecognitionResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>接管原生结果指针的所有权。</summary>
    public PlateRecognitionResultHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <summary>调用桥接释放函数并结束句柄生命周期。</summary>
    protected override bool ReleaseHandle()
    {
        PlateRecognitionNativeMethods.ReleaseResult(handle);
        return true;
    }
}
