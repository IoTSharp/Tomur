using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M11.Tests;

public sealed class ExpertCacheOptimizationTests
{
    [Fact]
    public async Task PrefetchLoadsWithoutChangingUsageAndNextAcquireIsHot()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        using var model = ManagedGlmModel.Load(ReadProbe(fixturePath), 16, long.MaxValue);
        using var cache = model.CreateExpertCache(CreateOptions(model, hotExpertCount: 0));

        await cache.PrefetchLayerAsync(0, new[] { 0 });

        var prefetched = cache.GetSnapshot();
        Assert.Equal(1L, prefetched.Prefetches);
        Assert.Equal(1L, prefetched.DiskReads);
        Assert.Empty(prefetched.UsageHistogram);

        using var lease = await cache.AcquireLayerAsync(0, new[] { 0 });
        await lease.WaitReadyAsync();
        var acquired = cache.GetSnapshot();
        Assert.Equal(1L, acquired.Hits);
        Assert.Equal(1L, acquired.UsageHistogram[new ExpertKey(0, 0, ExpertWeightFormat.Float32)]);
    }

    [Fact]
    public async Task UsageHistogramProtectsHotExpertDuringEviction()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        using var model = ManagedGlmModel.Load(ReadProbe(fixturePath), 16, long.MaxValue);
        using var cache = model.CreateExpertCache(CreateOptions(model, hotExpertCount: 1));

        await AcquireAndRelease(cache, 0);
        await AcquireAndRelease(cache, 1);
        await AcquireAndRelease(cache, 0);
        await AcquireAndRelease(cache, 2);
        var beforeHotCheck = cache.GetSnapshot();

        await AcquireAndRelease(cache, 0);

        var afterHotCheck = cache.GetSnapshot();
        Assert.Equal(1, afterHotCheck.HotExpertCount);
        Assert.Equal(beforeHotCheck.DiskReads, afterHotCheck.DiskReads);
        Assert.Equal(beforeHotCheck.Hits + 1, afterHotCheck.Hits);
    }

    [Fact]
    public void AutomaticCapacityUsesBudgetAndLeavesRoomForHotExpert()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        using var model = ManagedGlmModel.Load(ReadProbe(fixturePath), 16, long.MaxValue);

        var options = ExpertCacheOptions.CreateAutomatic(model.ExpertLayout, model.MemoryPlan);
        var capacity = checked((int)(options.BudgetBytes /
            (model.ExpertLayout.SlotBudgetedBytes * model.ExpertLayout.MoeLayerCount)));

        Assert.InRange(
            capacity,
            model.Configuration.ExpertsPerToken,
            model.Configuration.RoutedExpertCount);
        Assert.Equal(
            Math.Max(0, capacity - model.Configuration.ExpertsPerToken),
            options.HotExpertCount);
    }

    private static ExpertCacheOptions CreateOptions(ManagedGlmModel model, int hotExpertCount)
        => new(
            checked(model.ExpertLayout.SlotBudgetedBytes * 2),
            WorkerCount: 1,
            QueueCapacity: 2,
            HotExpertCount: hotExpertCount);

    private static async Task AcquireAndRelease(ExpertCache cache, int expertId)
    {
        using var lease = await cache.AcquireLayerAsync(0, new[] { expertId });
        await lease.WaitReadyAsync();
    }

    private static ModelProbe ReadProbe(string fixturePath)
    {
        var manifestPath = Path.Combine(fixturePath, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "m11-fixture",
            "Managed GLM M11 fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);
        return ModelDirectoryProbe.Read(descriptor, ManagedGlmProvider.ProviderId);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
