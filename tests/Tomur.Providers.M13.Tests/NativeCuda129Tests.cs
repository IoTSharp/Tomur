using Tomur.Hardware;
using Tomur.Multimodal;
using Tomur.Native;
using Xunit;

namespace Tomur.Providers.M13.Tests;

public sealed class NativeCuda129Tests
{
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
        Assert.Equal(5, plan.Steps.Count);
        Assert.All(plan.Steps, step => Assert.Equal("linux-x64-cuda129", step.Preset));
    }

    [Fact]
    public void LinuxAllBuildsCpuLeafAssetsAndCuda129Assets()
    {
        var plan = NativeBuildPlanner.Create("linux-x64", "all", clean: true);

        Assert.True(plan.Clean);
        Assert.Equal(9, plan.Steps.Count);
        Assert.Equal("linux-x64-cuda129", plan.Steps[0].Preset);
        Assert.Equal(4, plan.Steps.Count(step => step.Preset == "linux-x64"));
        Assert.Equal(5, plan.Steps.Count(step => step.Preset == "linux-x64-cuda129"));
    }

    [Fact]
    public void LinuxRejectsCuda13Builds()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            NativeBuildPlanner.Create("linux-x64", "cuda13", clean: false));

        Assert.Contains("cuda129", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CudaAccelerationSelectsCuda129RuntimeVariant()
    {
        var plan = CreateAccelerationPlan("cuda", effectiveGpuLayers: 99);

        Assert.Equal("cuda129", MultimodalExecutionService.ResolveNativeVariant(plan));
    }

    [Fact]
    public void CpuAccelerationSelectsCpuRuntimeVariant()
    {
        var plan = CreateAccelerationPlan("cpu", effectiveGpuLayers: 0);

        Assert.Equal("cpu", MultimodalExecutionService.ResolveNativeVariant(plan));
    }

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
}
