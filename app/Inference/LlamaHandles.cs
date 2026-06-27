using Microsoft.Win32.SafeHandles;

namespace Tomur.Inference;

internal sealed class LlamaModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public LlamaModelHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        LlamaNativeMethods.ModelFree(handle);
        return true;
    }
}

internal sealed class LlamaContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public LlamaContextHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        LlamaNativeMethods.ContextFree(handle);
        return true;
    }
}

internal sealed class LlamaSamplerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public LlamaSamplerHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        LlamaNativeMethods.SamplerFree(handle);
        return true;
    }
}
