using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomur.Providers.M12.Tests;

public sealed class KvAndPrefetchTests
{
    [Fact]
    public void AdvancedConfigurationRequiresMatchingDsaAndMtpAssets()
    {
        using var dsaFixture = new FixtureDirectory();
        UpdateConfig(
            dsaFixture.Path,
            root =>
            {
                root["index_topk"] = 4;
                root["indexer_start_layer"] = 0;
                root["index_n_heads"] = 1;
                root["index_head_dim"] = 2;
            });
        var dsaError = Assert.Throws<InvalidDataException>(() => dsaFixture.ReadProbe());
        Assert.Contains("DSA", dsaError.Message, StringComparison.Ordinal);

        using var mtpFixture = new FixtureDirectory();
        UpdateConfig(
            mtpFixture.Path,
            root => root["num_nextn_predict_layers"] = 1);
        var mtpError = Assert.Throws<InvalidDataException>(() => mtpFixture.ReadProbe());
        Assert.Contains("MTP", mtpError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KvSnapshotRestoresOnlyAfterIdentityAndChecksumValidation()
    {
        using var fixture = new FixtureDirectory();
        var probe = fixture.ReadProbe();
        using var source = new KvCache(probe.Configuration, contextSize: 8);
        AppendEveryLayer(source, probe.Configuration, seed: 1);
        using var snapshot = new MemoryStream();
        source.Save(snapshot, "fixture@sha256:1234");

        snapshot.Position = 0;
        using var restored = new KvCache(probe.Configuration, contextSize: 8);
        var result = restored.Restore(snapshot, "fixture@sha256:1234");

        Assert.Equal(1, result.MinimumTokenCount);
        Assert.Equal(1, result.MaximumTokenCount);
        Assert.Equal(source.GetCompressed(0, 0).ToArray(), restored.GetCompressed(0, 0).ToArray());
        Assert.Equal(source.GetRopeKey(0, 0).ToArray(), restored.GetRopeKey(0, 0).ToArray());

        var damaged = snapshot.ToArray();
        damaged[damaged.Length / 2] ^= 0x5a;
        using var invalidTarget = new KvCache(probe.Configuration, contextSize: 8);
        Assert.Throws<InvalidDataException>(() => invalidTarget.Restore(
            new MemoryStream(damaged),
            "fixture@sha256:1234"));
        Assert.Equal(0, invalidTarget.GetValidTokenCount(0));
    }

    [Fact]
    public void IsolatedKvForkDoesNotShareMutableCacheState()
    {
        using var fixture = new FixtureDirectory();
        var probe = fixture.ReadProbe();
        using var original = new IsolatedKvContext(probe.Configuration, contextSize: 8);
        AppendEveryLayer(original, probe.Configuration, seed: 2);
        using var fork = original.Fork();

        AppendEveryLayer(fork, probe.Configuration, seed: 3);

        Assert.Equal(1, original.Position);
        Assert.Equal(2, fork.Position);
        Assert.Equal(1, original.Cache.GetValidTokenCount(0));
        Assert.Equal(2, fork.Cache.GetValidTokenCount(0));
    }

    [Fact]
    public void IsolatedKvForkCannotExceedSharedMemoryBudget()
    {
        using var fixture = new FixtureDirectory();
        var configuration = fixture.ReadProbe().Configuration;
        using var sizing = new KvCache(configuration, contextSize: 8);
        var budget = new KvContextBudget(sizing.ByteLength);
        using var context = new IsolatedKvContext(configuration, contextSize: 8, budget);

        Assert.Throws<InvalidOperationException>(() => context.Fork());
        Assert.Equal(sizing.ByteLength, budget.UsedBytes);
        context.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task RouterLookaheadPrefetchAndLiveRepinRemainBounded()
    {
        using var fixture = new FixtureDirectory();
        using var model = ManagedGlmModel.Load(fixture.ReadProbe(), 8, long.MaxValue);
        var options = new ExpertCacheOptions(
            checked(model.ExpertLayout.SlotBudgetedBytes * 2),
            WorkerCount: 1,
            QueueCapacity: 2,
            HotExpertCount: 1,
            RepinInterval: 100);
        using var cache = model.CreateExpertCache(options);
        var lookahead = new RouterLookaheadPrefetcher(model.Configuration, cache);

        for (var index = 0; index < 3; index++)
        {
            using var lease = await cache.AcquireLayerAsync(0, new[] { 0 });
            await lease.WaitReadyAsync();
        }

        using (var lease = await cache.AcquireLayerAsync(0, new[] { 1 }))
        {
            await lease.WaitReadyAsync();
        }

        cache.RepinHotExperts();
        lookahead.Observe(0, new[] { 0, 1 });
        await lookahead.PrefetchAsync(0);
        var snapshot = cache.GetSnapshot();

        Assert.Equal(new[] { 0 }, cache.GetPinnedExperts(0));
        Assert.Equal(1, snapshot.LiveRepins);
        Assert.Equal(2, lookahead.RequestedExperts);
        Assert.True(snapshot.Prefetches >= 1);
        Assert.True(snapshot.BudgetedBytes <= options.BudgetBytes);
    }

    private static void AppendEveryLayer(
        KvCache cache,
        GlmModelConfiguration configuration,
        int seed)
    {
        var random = new Random(seed);
        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            var compressed = Enumerable.Range(0, configuration.KeyValueLoraRank)
                .Select(_ => (float)random.NextDouble())
                .ToArray();
            var rope = Enumerable.Range(0, configuration.QueryKeyRopeHeadSize)
                .Select(_ => (float)random.NextDouble())
                .ToArray();
            cache.Append(layer, compressed, rope);
        }
    }

    private static void AppendEveryLayer(
        IsolatedKvContext context,
        GlmModelConfiguration configuration,
        int seed)
    {
        AppendEveryLayer(context.Cache, configuration, seed);
        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            context.GetSequence(layer).Advance();
        }
    }

    private static void UpdateConfig(string fixturePath, Action<JsonObject> update)
    {
        var path = System.IO.Path.Combine(fixturePath, "config.json");
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Fixture config root is missing.");
        update(root);
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FixtureDirectory : IDisposable
    {
        public FixtureDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tomur-m12-{Guid.NewGuid():N}");
            TinyFixtureBundle.Generate(Path);
        }

        public string Path { get; }

        public ModelProbe ReadProbe()
        {
            var manifestPath = System.IO.Path.Combine(Path, ModelProviderManifest.FileName);
            var info = new FileInfo(manifestPath);
            return ModelDirectoryProbe.Read(
                new LocalModelDescriptor(
                    "m12-fixture",
                    "Managed GLM M12 fixture",
                    ModelProviderManifest.FileName,
                    ModelProviderManifest.FileName,
                    manifestPath,
                    info.Length,
                    info.LastWriteTimeUtc,
                    "managed-model",
                    GlmModelConfiguration.DsaModelType,
                    "f32",
                    ["completion", "chat"]),
                ManagedGlmProvider.ProviderId);
        }

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
}
