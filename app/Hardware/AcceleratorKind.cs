namespace Tomur.Hardware;

internal enum AcceleratorKind
{
    Cpu = 0,
    Cuda,
    Vulkan,
    Metal,
    OpenVino,
    Npu,
    Sycl,
    Unknown
}
