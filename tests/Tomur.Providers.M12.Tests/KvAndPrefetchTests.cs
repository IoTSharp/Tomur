using System.Buffers.Binary;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomur.Providers.M12.Tests;

public sealed class KvAndPrefetchTests
{
    /// <summary>
    /// 验证 shared indexer 层不要求重复权重，且基础推理可忽略缺失的独立 MTP head。
    /// </summary>
    [Fact]
    public void AdvancedProbeAcceptsSharedIndexerLayersWithoutIndependentMtpHead()
    {
        using var fixture = new FixtureDirectory();
        var configuration = GlmModelConfiguration.Read(
            System.IO.Path.Combine(fixture.Path, "config.json"),
            GlmModelConfiguration.DsaModelType) with
        {
            LayerCount = 3,
            DsaTopK = 4,
            DsaStartLayer = 0,
            DsaIndexHeadCount = 1,
            DsaIndexHeadSize = 2,
            DsaIndexerTypes = ["full", "shared", "shared"],
            MtpLayerCount = 1
        };
        var tensorPath = System.IO.Path.Combine(fixture.Path, "advanced.safetensors");
        WriteSafeTensorHeaders(
            tensorPath,
            ("model.layers.0.self_attn.indexer.k_norm.weight", new long[] { 2 }),
            ("model.layers.3.eh_proj.weight", new long[] { 4, 4 }));

        var probe = AdvancedFeatureProbe.Inspect(
            configuration,
            SafeTensorCatalog.Read([tensorPath]));

        Assert.True(probe.DsaConfigured);
        Assert.Equal(1, probe.DsaTensorCount);
        Assert.True(probe.MtpConfigured);
        Assert.Equal(1, probe.MtpTensorCount);
        Assert.Null(probe.MtpHeadTensorName);
    }

    /// <summary>
    /// 验证无 indexer 权重的 DSA 模型仅能在 top-k 覆盖上下文时加载。
    /// </summary>
    [Fact]
    public void DsaWithoutIndexerWeightsIsLimitedToDenseEquivalentContext()
    {
        using var fixture = new FixtureDirectory();
        UpdateConfig(
            fixture.Path,
            root =>
            {
                root["index_topk"] = 4;
                root["indexer_start_layer"] = 0;
                root["index_n_heads"] = 1;
                root["index_head_dim"] = 2;
                root["indexer_types"] = new JsonArray("full");
            });
        var probe = fixture.ReadProbe();

        Assert.True(probe.AdvancedFeatures.DsaConfigured);
        Assert.Equal(0, probe.AdvancedFeatures.DsaTensorCount);
        using var model = ManagedGlmModel.Load(probe, contextSize: 4, long.MaxValue);
        var exception = Assert.Throws<ContextLengthExceededException>(() =>
            ManagedGlmModel.Load(probe, contextSize: 5, long.MaxValue));
        Assert.Equal(4, exception.ContextLimit);

        var descriptor = fixture.CreateDescriptor();
        var provider = new ManagedGlmProvider();
        var readiness = provider.InspectModel(descriptor, new ModelSessionOptions(8));
        Assert.Equal(4, readiness.ContextSize);
        using var session = provider.CreateSession(descriptor, new ModelSessionOptions(8));
        Assert.Equal(4, session.GetSnapshot().ContextSize);
    }

    /// <summary>
    /// 验证声明 MTP 层时仍必须提供对应的 MTP 张量资产。
    /// </summary>
    [Fact]
    public void AdvancedConfigurationRequiresMtpAssets()
    {
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

    /// <summary>
    /// 写入只包含测试所需 F32 张量的最小 safetensors 文件。
    /// </summary>
    private static void WriteSafeTensorHeaders(
        string path,
        params (string Name, long[] Shape)[] tensors)
    {
        using var headerStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(headerStream))
        {
            writer.WriteStartObject();
            long offset = 0;
            foreach (var tensor in tensors)
            {
                var elements = tensor.Shape.Aggregate(1L, static (total, value) => checked(total * value));
                var byteLength = checked(elements * sizeof(float));
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", "F32");
                writer.WriteStartArray("shape");
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("data_offsets");
                writer.WriteNumberValue(offset);
                writer.WriteNumberValue(checked(offset + byteLength));
                writer.WriteEndArray();
                writer.WriteEndObject();
                offset = checked(offset + byteLength);
            }

            writer.WriteEndObject();
        }

        var unpaddedHeader = headerStream.ToArray();
        var paddedLength = checked((unpaddedHeader.Length + 7) & ~7);
        var header = new byte[paddedLength];
        unpaddedHeader.CopyTo(header, 0);
        header.AsSpan(unpaddedHeader.Length).Fill((byte)' ');

        using var stream = File.Create(path);
        Span<byte> headerLength = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(headerLength, (ulong)header.Length);
        stream.Write(headerLength);
        stream.Write(header);
        stream.SetLength(checked(sizeof(ulong) + header.Length +
            tensors.Sum(static tensor => tensor.Shape.Aggregate(
                (long)sizeof(float),
                static (total, value) => checked(total * value)))));
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

        /// <summary>
        /// 按测试目录创建托管模型描述符。
        /// </summary>
        public LocalModelDescriptor CreateDescriptor()
        {
            var manifestPath = System.IO.Path.Combine(Path, ModelProviderManifest.FileName);
            var info = new FileInfo(manifestPath);
            return new LocalModelDescriptor(
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
                ["completion", "chat"]);
        }

        /// <summary>
        /// 读取测试目录并完成模型探测。
        /// </summary>
        public ModelProbe ReadProbe()
        {
            return ModelDirectoryProbe.Read(
                CreateDescriptor(),
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
