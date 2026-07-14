using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M8.Tests;

public sealed class MoeStreamingTests
{
    [Fact]
    public async Task ColdAndHotMoeExecutionMatchRouterAndMoeOracle()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(
            probe,
            oracle.ModelConfiguration.ContextSize,
            long.MaxValue);
        using var workspace = model.CreateMoeWorkspace();
        using var cache = CreateTopKCache(model);
        var input = oracle.GetCheckpoint("router.input").Values.ToArray();
        var output = new float[model.Configuration.HiddenSize];
        var trace = new MoeTrace();

        await model.RunMoeTokenAsync(0, input, cache, workspace, output, trace: trace);

        Assert.Equal(oracle.Router.ExpertIds.ToArray(), trace.ExpertIds);
        AssertClose(oracle.GetCheckpoint("router.scores").Values, trace.Scores, oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("router.adjusted_scores").Values,
            trace.AdjustedScores,
            oracle.Tolerances);
        AssertClose(oracle.Router.Weights, trace.RoutingWeights, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("router.output").Values, trace.RoutedOutput, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("moe.output").Values, output, oracle.Tolerances);
        AssertClose(output, trace.Output, oracle.Tolerances);
        Assert.Equal(model.MemoryPlan.MoeWorkspaceBytes, workspace.BudgetedBytes);

        var cold = cache.GetSnapshot();
        Assert.Equal(model.ExpertLayout.SlotBudgetedBytes * model.Configuration.ExpertsPerToken, cold.BudgetedBytes);
        Assert.Equal((long)model.Configuration.ExpertsPerToken, cold.Misses);
        Assert.Equal((long)model.Configuration.ExpertsPerToken, cold.DiskReads);
        Assert.Equal(0L, cold.Hits);
        Assert.True(cold.DiskBytes > 0);
        Assert.Equal(model.Configuration.ExpertsPerToken, cold.UsageHistogram.Count);

        output.AsSpan().Fill(float.NaN);
        await model.RunMoeTokenAsync(0, input, cache, workspace, output);

        var hot = cache.GetSnapshot();
        AssertClose(oracle.GetCheckpoint("moe.output").Values, output, oracle.Tolerances);
        Assert.Equal(cold.Misses, hot.Misses);
        Assert.Equal(cold.DiskReads, hot.DiskReads);
        Assert.Equal((long)model.Configuration.ExpertsPerToken, hot.Hits);
        Assert.All(hot.UsageHistogram.Values, count => Assert.Equal(2L, count));
    }

    [Fact]
    public async Task LayerCacheUnionsDuplicatesAndDoesNotReuseLeasedSlots()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(probe, 16, long.MaxValue);
        using var cache = CreateTopKCache(model);

        using var first = await cache.AcquireLayerAsync(0, new[] { 0, 0, 1 });
        await first.WaitReadyAsync();
        Assert.Equal(2, first.Count);
        var waiting = cache.AcquireLayerAsync(0, new[] { 2 }).AsTask();
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        using var second = await waiting;
        await second.WaitReadyAsync();

        var afterEviction = cache.GetSnapshot();
        Assert.Equal(3L, afterEviction.Misses);
        Assert.Equal(1L, afterEviction.Evictions);
        Assert.Equal(3L, afterEviction.DiskReads);
        Assert.Equal(1L, afterEviction.UsageHistogram[new ExpertKey(0, 0, ExpertWeightFormat.Float32)]);
        Assert.Equal(1L, afterEviction.UsageHistogram[new ExpertKey(0, 1, ExpertWeightFormat.Float32)]);
        Assert.Equal(1L, afterEviction.UsageHistogram[new ExpertKey(0, 2, ExpertWeightFormat.Float32)]);
    }

    [Fact]
    public async Task ExpertBudgetAndCanceledAcquisitionFailBeforeReading()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(probe, 16, long.MaxValue);
        var minimum = checked(
            model.ExpertLayout.SlotBudgetedBytes * model.Configuration.ExpertsPerToken);

        var budgetException = Assert.Throws<ExpertCacheBudgetExceededException>(() =>
            model.CreateExpertCache(new ExpertCacheOptions(minimum - 1)));
        Assert.Equal(minimum, budgetException.MinimumBytes);

        using var cache = model.CreateExpertCache(new ExpertCacheOptions(
            minimum,
            WorkerCount: 1,
            QueueCapacity: 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.AcquireLayerAsync(0, new[] { 0 }, cancellation.Token));
        var snapshot = cache.GetSnapshot();
        Assert.Equal(0L, snapshot.Misses);
        Assert.Equal(0L, snapshot.DiskReads);
    }

    [Fact]
    public void RouterCanSkipTopKNormalizationAndStillApplyScaling()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var configPath = Path.Combine(fixturePath, TinyFixtureFiles.Configuration);
        var configuration = File.ReadAllText(configPath)
            .Replace("\"norm_topk_prob\": true", "\"norm_topk_prob\": false", StringComparison.Ordinal)
            .Replace("\"routed_scaling_factor\": 1.0", "\"routed_scaling_factor\": 2.0", StringComparison.Ordinal);
        File.WriteAllText(configPath, configuration);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(probe, 16, long.MaxValue);
        using var workspace = model.CreateMoeWorkspace();

        MoeRouter.Route(model, 0, oracle.GetCheckpoint("router.input").Values.ToArray(), workspace);

        for (var route = 0; route < workspace.ExpertsPerToken; route++)
        {
            var expertId = workspace.SelectedExpertIds[route];
            Assert.Equal(workspace.Scores[expertId] * 2.0f, workspace.SelectedWeights[route]);
        }
    }

    [Fact]
    public async Task CancellationLeavesDestinationAndTraceUnchanged()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(probe, 16, long.MaxValue);
        using var workspace = model.CreateMoeWorkspace();
        using var cache = CreateTopKCache(model);
        var destination = Enumerable.Repeat(123.0f, model.Configuration.HiddenSize).ToArray();
        var trace = new MoeTrace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await model.RunMoeTokenAsync(
                0,
                oracle.GetCheckpoint("router.input").Values.ToArray(),
                cache,
                workspace,
                destination,
                cancellation.Token,
                trace));

        Assert.All(destination, value => Assert.Equal(123.0f, value));
        Assert.Empty(trace.Output);
        Assert.Equal(0L, cache.GetSnapshot().DiskReads);
    }

    private static ExpertCache CreateTopKCache(ManagedGlmModel model)
        => model.CreateExpertCache(new ExpertCacheOptions(
            checked(model.ExpertLayout.SlotBudgetedBytes * model.Configuration.ExpertsPerToken),
            WorkerCount: 1,
            QueueCapacity: model.Configuration.ExpertsPerToken));

    private static ModelProbe ReadProbe(string fixturePath)
    {
        var manifestPath = Path.Combine(fixturePath, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "m8-fixture",
            "Managed GLM M8 fixture",
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

    private static void AssertClose(
        IReadOnlyList<float> expected,
        IReadOnlyList<float> actual,
        TinyOracleTolerance tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var error = Math.Abs(expected[index] - actual[index]);
            var limit = tolerance.Absolute + (tolerance.Relative * Math.Abs(expected[index]));
            Assert.True(
                error <= limit,
                $"Value {index} differs: expected {expected[index]}, actual {actual[index]}, limit {limit}.");
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m8-{Guid.NewGuid():N}");
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
