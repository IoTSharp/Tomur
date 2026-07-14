using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M7.Tests;

public sealed class MlaAttentionTests
{
    [Fact]
    public void PrefillMatchesAttentionOracleAndStoresOnlyCompressedKv()
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
        using var cache = model.CreateKvCache();
        using var workspace = model.CreateAttentionWorkspace();
        var sequence = model.CreateSequenceState(0);
        var trace = new AttentionTrace();
        var tokenIds = oracle.Tokenization
            .Single(item => item.Text == "hello Tomur")
            .TokenIds
            .ToArray();
        var normalizedInputs = CreateNormalizedInputs(model, tokenIds);
        var outputs = new float[normalizedInputs.Length];

        model.RunAttentionPrefill(
            0,
            normalizedInputs,
            tokenIds.Length,
            cache,
            sequence,
            workspace,
            outputs,
            lastTokenTrace: trace);

        AssertClose(oracle.GetCheckpoint("attention.q_latent").Values, trace.QueryLatent, oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("attention.q_normalized").Values,
            trace.NormalizedQueryLatent,
            oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("attention.kv_latent").Values,
            trace.KeyValueLatent,
            oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("attention.kv_normalized").Values,
            trace.NormalizedKeyValueLatent,
            oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("attention.query").Values, trace.Query, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("attention.key").Values, trace.Key, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("attention.value").Values, trace.Value, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("attention.scores").Values, trace.Scores, oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("attention.probabilities").Values,
            trace.Probabilities,
            oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("attention.output").Values, trace.Output, oracle.Tolerances);
        AssertClose(
            oracle.GetCheckpoint("attention.output").Values,
            outputs.AsSpan(outputs.Length - oracle.ModelConfiguration.HiddenSize).ToArray(),
            oracle.Tolerances);

        Assert.Equal(tokenIds.Length, sequence.Position);
        Assert.Equal(tokenIds.Length, sequence.ValidTokenCount);
        Assert.Equal(0, sequence.CacheStart);
        Assert.Equal(tokenIds.Length, cache.GetValidTokenCount(0));
        Assert.Equal(model.MemoryPlan.KvBytes, cache.ByteLength);
        Assert.Equal(256L, cache.ByteLength);
        AssertClose(
            trace.NormalizedKeyValueLatent,
            cache.GetCompressed(0, tokenIds.Length - 1).ToArray(),
            oracle.Tolerances);
        AssertClose(
            trace.Key.AsSpan(2, 2).ToArray(),
            cache.GetRopeKey(0, tokenIds.Length - 1).ToArray(),
            oracle.Tolerances);

        var fullKeyValueBytes = checked(
            (long)model.Configuration.LayerCount *
            model.MemoryPlan.ContextSize *
            model.Configuration.AttentionHeadCount *
            (model.Configuration.QueryKeyNopeHeadSize +
             model.Configuration.QueryKeyRopeHeadSize +
             model.Configuration.ValueHeadSize) *
            sizeof(float));
        Assert.True(cache.ByteLength < fullKeyValueBytes);
        Assert.Equal(
            checked(
                ((long)model.MemoryPlan.AttentionActivationCapacity +
                 model.MemoryPlan.AttentionScoreCapacity) *
                sizeof(float)),
            workspace.BudgetedBytes);
    }

    [Fact]
    public void DecodeAfterPrefillMatchesSinglePrefill()
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
        var tokenIds = oracle.Tokenization
            .Single(item => item.Text == "hello Tomur")
            .TokenIds
            .ToArray();
        var normalizedInputs = CreateNormalizedInputs(model, tokenIds);

        using var splitCache = model.CreateKvCache();
        using var splitWorkspace = model.CreateAttentionWorkspace();
        var splitSequence = model.CreateSequenceState(0);
        var prefixOutputs = new float[2 * model.Configuration.HiddenSize];
        model.RunAttentionPrefill(
            0,
            normalizedInputs.AsSpan(0, prefixOutputs.Length),
            2,
            splitCache,
            splitSequence,
            splitWorkspace,
            prefixOutputs);
        var decodedOutput = new float[model.Configuration.HiddenSize];
        model.RunAttentionToken(
            0,
            normalizedInputs.AsSpan(prefixOutputs.Length, model.Configuration.HiddenSize),
            splitCache,
            splitSequence,
            splitWorkspace,
            decodedOutput);

        using var fullCache = model.CreateKvCache();
        using var fullWorkspace = model.CreateAttentionWorkspace();
        var fullSequence = model.CreateSequenceState(0);
        var fullOutputs = new float[normalizedInputs.Length];
        model.RunAttentionPrefill(
            0,
            normalizedInputs,
            tokenIds.Length,
            fullCache,
            fullSequence,
            fullWorkspace,
            fullOutputs);

        AssertClose(
            fullOutputs.AsSpan(fullOutputs.Length - model.Configuration.HiddenSize).ToArray(),
            decodedOutput,
            oracle.Tolerances);
        Assert.Equal(fullSequence.Position, splitSequence.Position);
        Assert.Equal(fullSequence.ValidTokenCount, splitSequence.ValidTokenCount);
        for (var tokenIndex = 0; tokenIndex < tokenIds.Length; tokenIndex++)
        {
            AssertClose(
                fullCache.GetCompressed(0, tokenIndex).ToArray(),
                splitCache.GetCompressed(0, tokenIndex).ToArray(),
                oracle.Tolerances);
            AssertClose(
                fullCache.GetRopeKey(0, tokenIndex).ToArray(),
                splitCache.GetRopeKey(0, tokenIndex).ToArray(),
                oracle.Tolerances);
        }

        using var absorbedCache = model.CreateKvCache();
        using var absorbedWorkspace = model.CreateAttentionWorkspace();
        var absorbedSequence = model.CreateSequenceState(0);
        var absorbedOutputs = new float[normalizedInputs.Length];
        model.RunAttentionPrefill(
            0,
            normalizedInputs,
            tokenIds.Length,
            absorbedCache,
            absorbedSequence,
            absorbedWorkspace,
            absorbedOutputs,
            mode: MlaAttentionMode.Absorbed);
        AssertClose(fullOutputs, absorbedOutputs, oracle.Tolerances);
        Assert.Equal(fullSequence.Position, absorbedSequence.Position);
        Assert.Equal(fullSequence.ValidTokenCount, absorbedSequence.ValidTokenCount);
        for (var tokenIndex = 0; tokenIndex < tokenIds.Length; tokenIndex++)
        {
            AssertClose(
                fullCache.GetCompressed(0, tokenIndex).ToArray(),
                absorbedCache.GetCompressed(0, tokenIndex).ToArray(),
                oracle.Tolerances);
            AssertClose(
                fullCache.GetRopeKey(0, tokenIndex).ToArray(),
                absorbedCache.GetRopeKey(0, tokenIndex).ToArray(),
                oracle.Tolerances);
        }
    }

    [Fact]
    public void PartialRopeRotatesConsecutivePairsWithPerPairFrequency()
    {
        float[] values = [1.0f, 2.0f, 3.0f, 4.0f];
        const int position = 2;
        const float theta = 10000.0f;
        var expected = new float[values.Length];
        RotatePair(values, expected, 0, position);
        RotatePair(values, expected, 2, position * 0.01);

        RotaryEmbedding.ApplyInterleaved(values, position, theta);

        AssertClose(expected, values, new TinyOracleTolerance(1e-6f, 1e-6f));
    }

    [Fact]
    public void ContextOverflowFailsBeforePrefillMutatesCacheOrOutput()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        var modelLimitException = Assert.Throws<ContextLengthExceededException>(() =>
            ManagedGlmModel.Load(probe, contextSize: 17, availableMemoryBytes: long.MaxValue));
        Assert.Equal(16, modelLimitException.ContextLimit);
        using var model = ManagedGlmModel.Load(probe, contextSize: 2, availableMemoryBytes: long.MaxValue);
        using var cache = model.CreateKvCache();
        using var workspace = model.CreateAttentionWorkspace();
        var sequence = model.CreateSequenceState(0);
        int[] tokenIds = [1, 4, 6];
        var normalizedInputs = CreateNormalizedInputs(model, tokenIds);
        var outputs = Enumerable.Repeat(123.0f, normalizedInputs.Length).ToArray();

        var exception = Assert.Throws<ContextLengthExceededException>(() =>
            model.RunAttentionPrefill(
                0,
                normalizedInputs,
                tokenIds.Length,
                cache,
                sequence,
                workspace,
                outputs));

        Assert.Equal(2, exception.ContextLimit);
        Assert.Equal(0, sequence.Position);
        Assert.Equal(0, sequence.ValidTokenCount);
        Assert.Equal(0, cache.GetValidTokenCount(0));
        Assert.All(outputs, value => Assert.Equal(123.0f, value));
    }

    [Fact]
    public void FailureAndCancellationRollbackNewCachePositions()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var probe = ReadProbe(fixturePath);
        using var model = ManagedGlmModel.Load(probe, contextSize: 16, availableMemoryBytes: long.MaxValue);
        using var cache = model.CreateKvCache();
        using var workspace = model.CreateAttentionWorkspace();
        var sequence = model.CreateSequenceState(0);
        int[] tokenIds = [1, 4];
        var normalizedInputs = CreateNormalizedInputs(model, tokenIds);
        normalizedInputs[model.Configuration.HiddenSize] = float.NaN;
        var outputs = new float[normalizedInputs.Length];

        Assert.Throws<InvalidDataException>(() =>
            model.RunAttentionPrefill(
                0,
                normalizedInputs,
                tokenIds.Length,
                cache,
                sequence,
                workspace,
                outputs));
        Assert.Equal(0, sequence.Position);
        Assert.Equal(0, sequence.ValidTokenCount);
        Assert.Equal(0, cache.GetValidTokenCount(0));

        var validInput = CreateNormalizedInputs(model, [1]);
        var validOutput = new float[model.Configuration.HiddenSize];
        model.RunAttentionToken(
            0,
            validInput,
            cache,
            sequence,
            workspace,
            validOutput);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            model.RunAttentionToken(
                0,
                validInput,
                cache,
                sequence,
                workspace,
                validOutput,
                cancellation.Token));
        Assert.Equal(1, sequence.Position);
        Assert.Equal(1, sequence.ValidTokenCount);
        Assert.Equal(1, cache.GetValidTokenCount(0));
    }

    [Fact]
    public void OddRopeDimensionIsRejectedDuringConfigurationProbe()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var configPath = Path.Combine(fixturePath, TinyFixtureFiles.Configuration);
        var configuration = File.ReadAllText(configPath);
        File.WriteAllText(
            configPath,
            configuration.Replace(
                "\"qk_rope_head_dim\": 2",
                "\"qk_rope_head_dim\": 3",
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidDataException>(() => ReadProbe(fixturePath));

        Assert.Contains("must be even", exception.Message, StringComparison.Ordinal);
    }

    private static float[] CreateNormalizedInputs(ManagedGlmModel model, IReadOnlyList<int> tokenIds)
    {
        var hiddenSize = model.Configuration.HiddenSize;
        var embeddings = new float[checked(tokenIds.Count * hiddenSize)];
        model.GatherEmbeddings(tokenIds.ToArray(), embeddings);
        var normalized = new float[embeddings.Length];
        for (var tokenIndex = 0; tokenIndex < tokenIds.Count; tokenIndex++)
        {
            var offset = checked(tokenIndex * hiddenSize);
            model.NormalizeLayerInput(
                0,
                embeddings.AsSpan(offset, hiddenSize),
                normalized.AsSpan(offset, hiddenSize));
        }

        return normalized;
    }

    private static void RotatePair(
        IReadOnlyList<float> source,
        IList<float> destination,
        int offset,
        double angle)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        destination[offset] = (float)((source[offset] * cosine) - (source[offset + 1] * sine));
        destination[offset + 1] = (float)((source[offset] * sine) + (source[offset + 1] * cosine));
    }

    private static ModelProbe ReadProbe(string fixturePath)
    {
        var manifestPath = Path.Combine(fixturePath, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "m7-fixture",
            "Managed GLM M7 fixture",
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
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m7-{Guid.NewGuid():N}");
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
