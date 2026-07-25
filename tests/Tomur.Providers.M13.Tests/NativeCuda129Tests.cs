using Tomur.Hardware;
using Tomur.Multimodal;
using Tomur.Native;
using Xunit;

namespace Tomur.Providers.M13.Tests;

public sealed class NativeCuda129Tests
{
    /// <summary>验证 Linux CUDA 别名使用 CUDA 12.9，并让仅支持 CPU 的车牌组件保持 CPU preset。</summary>
    [Theory]
    [InlineData("cuda")]
    [InlineData("cuda12")]
    [InlineData("cuda12.9")]
    [InlineData("cuda-12.9")]
    [InlineData("cu129")]
    [InlineData("cuda129")]
    public void LinuxCudaAliasesSelectCuda129Presets(string backend)
    {
        var plan = NativeBuildPlanner.Create("linux-x64", backend, clean: false);

        Assert.Equal("cuda129", plan.Backend);
        Assert.Equal(6, plan.Steps.Count);
        var plate = Assert.Single(plan.Steps.Where(step => step.Component == "plate"));
        Assert.Equal("linux-x64", plate.Preset);
        Assert.All(
            plan.Steps.Where(step => step.Component != "plate"),
            step => Assert.Equal("linux-x64-cuda129", step.Preset));
    }

    /// <summary>验证完整 Linux x64 计划包含五个 CPU 步骤和五个 CUDA 12.9 步骤。</summary>
    [Fact]
    public void LinuxAllBuildsCpuLeafAssetsAndCuda129Assets()
    {
        var plan = NativeBuildPlanner.Create("linux-x64", "all", clean: true);

        Assert.True(plan.Clean);
        Assert.Equal(10, plan.Steps.Count);
        Assert.Equal("linux-x64-cuda129", plan.Steps[0].Preset);
        Assert.Equal(5, plan.Steps.Count(step => step.Preset == "linux-x64"));
        Assert.Equal(5, plan.Steps.Count(step => step.Preset == "linux-x64-cuda129"));
        Assert.Equal("linux-x64", Assert.Single(plan.Steps.Where(step => step.Component == "plate")).Preset);
    }

    /// <summary>验证 Linux ARM64 的 cpu/all 都生成六个纯 CPU 组件步骤，并兼容 aarch64 别名。</summary>
    [Theory]
    [InlineData("linux-arm64", "cpu")]
    [InlineData("linux-arm64", "all")]
    [InlineData("linux-aarch64", "all")]
    public void LinuxArm64BuildsAllComponentsWithCpuPresets(string rid, string backend)
    {
        var plan = NativeBuildPlanner.Create(rid, backend, clean: false);

        Assert.Equal("linux-arm64", plan.Rid);
        Assert.Equal(6, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.Equal("linux-arm64", step.Preset));
        Assert.Equal(
            new[] { "llama", "whisper", "stable-diffusion", "ocr", "tts", "plate" },
            plan.Steps.Select(step => step.Component).ToArray());
    }

    /// <summary>验证 Linux ARM64 明确拒绝所有加速器后端。</summary>
    [Theory]
    [InlineData("cuda129")]
    [InlineData("cuda13")]
    [InlineData("vulkan")]
    [InlineData("openvino")]
    [InlineData("sycl")]
    [InlineData("intel")]
    public void LinuxArm64RejectsAcceleratedBackends(string backend)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            NativeBuildPlanner.Create("linux-arm64", backend, clean: false));

        Assert.Contains("cpu", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证车牌交叉编译必须显式提供目标依赖，且查找过程禁止回退到宿主默认路径。</summary>
    [Fact]
    public void PlateCrossCompileRequiresTargetArchitectureDependencies()
    {
        var cmake = File.ReadAllText(FindRepositoryFile("native", "plate.native", "CMakeLists.txt"));

        Assert.Contains("if(CMAKE_CROSSCOMPILING)", cmake, StringComparison.Ordinal);
        Assert.Contains("TOMUR_HYPERLPR3_INCLUDE_DIR", cmake, StringComparison.Ordinal);
        Assert.Contains("TOMUR_HYPERLPR3_LIBRARY", cmake, StringComparison.Ordinal);
        Assert.Contains("OpenCV_DIR", cmake, StringComparison.Ordinal);
        Assert.Contains("NO_DEFAULT_PATH", cmake, StringComparison.Ordinal);
        Assert.Contains("requires a shared HyperLPR3 library", cmake, StringComparison.Ordinal);
    }

    /// <summary>验证原生桥接在调用 OpenCV 完整解码前已经完成图片头尺寸限制。</summary>
    [Fact]
    public void PlateNativePreflightsDimensionsBeforeOpenCvDecode()
    {
        var source = File.ReadAllText(FindRepositoryFile("native", "plate.native", "tomur_plate_bridge.cpp"));
        var decodeFunction = source.IndexOf("bool decode_image(", StringComparison.Ordinal);
        Assert.True(decodeFunction >= 0);

        var preflight = source.IndexOf(
            "try_read_encoded_dimensions(image_data, image_length, dimensions)",
            decodeFunction,
            StringComparison.Ordinal);
        var fullDecode = source.IndexOf("cv::imdecode(encoded, cv::IMREAD_COLOR)", decodeFunction, StringComparison.Ordinal);

        Assert.True(preflight > decodeFunction);
        Assert.True(fullDecode > preflight);
        Assert.Contains("if (data[offset] != 0xffU)", source, StringComparison.Ordinal);
        Assert.Contains("declared_end != length", source, StringComparison.Ordinal);
        Assert.Contains("return found && offset == end;", source, StringComparison.Ordinal);
    }

    /// <summary>验证完整抓拍图固定使用 HyperLPR3 的 640 高精度检测器。</summary>
    [Fact]
    public void PlateNativeUsesHighResolutionDetectorForFullImages()
    {
        var source = File.ReadAllText(FindRepositoryFile("native", "plate.native", "tomur_plate_bridge.cpp"));

        Assert.Contains("configuration.det_level = DETECT_LEVEL_HIGH;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration.det_level = DETECT_LEVEL_LOW;", source, StringComparison.Ordinal);
    }

    /// <summary>验证 Linux x64 不接受只在其他平台开放的 CUDA 13 构建。</summary>
    [Fact]
    public void LinuxRejectsCuda13Builds()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            NativeBuildPlanner.Create("linux-x64", "cuda13", clean: false));

        Assert.Contains("cuda129", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证 CUDA 加速计划选择 CUDA 12.9 多模态 runtime 变体。</summary>
    [Fact]
    public void CudaAccelerationSelectsCuda129RuntimeVariant()
    {
        var plan = CreateAccelerationPlan("cuda", effectiveGpuLayers: 99);

        Assert.Equal("cuda129", MultimodalExecutionService.ResolveNativeVariant(plan));
    }

    /// <summary>验证 CPU 加速计划选择 CPU 多模态 runtime 变体。</summary>
    [Fact]
    public void CpuAccelerationSelectsCpuRuntimeVariant()
    {
        var plan = CreateAccelerationPlan("cpu", effectiveGpuLayers: 0);

        Assert.Equal("cpu", MultimodalExecutionService.ResolveNativeVariant(plan));
    }

    /// <summary>构造仅包含本测试需要字段的硬件加速计划。</summary>
    private static AccelerationPlan CreateAccelerationPlan(string backend, int effectiveGpuLayers)
        => new(
            "ok",
            backend,
            backend,
            effectiveGpuLayers,
            effectiveGpuLayers,
            effectiveGpuLayers,
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            [],
            [],
            []);

    /// <summary>从测试输出目录向上定位 Tomur 仓库中的静态工程文件。</summary>
    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, Path.Combine(relativeSegments));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}' from '{AppContext.BaseDirectory}'.");
    }
}
