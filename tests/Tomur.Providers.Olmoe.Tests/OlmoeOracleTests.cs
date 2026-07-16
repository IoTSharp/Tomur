using Tomur.Inference;
using Tomur.Providers.Glm;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeOracleTests
{
    [Fact]
    public void FixtureGenerationIsByteDeterministic()
    {
        using var first = new OlmoeFixture();
        using var second = new OlmoeFixture();

        Assert.Equal(File.ReadAllBytes(first.TensorPath), File.ReadAllBytes(second.TensorPath));
        Assert.Equal(
            File.ReadAllText(Path.Combine(first.Path, "config.json")),
            File.ReadAllText(Path.Combine(second.Path, "config.json")));
        Assert.Equal(first.LogicalWeights.Keys.Order(), second.LogicalWeights.Keys.Order());
    }

    [Fact]
    public void EmbeddingAttentionRouterAndMoeMatchScalarOracle()
    {
        using var fixture = new OlmoeFixture();
        var oracle = OlmoeTinyReference.Create(fixture);
        var expected = oracle.TeacherForcing[0];
        using var model = fixture.LoadModel(contextSize: 16);

        var embedding = new float[model.Configuration.HiddenSize];
        model.GatherEmbedding(expected.InputTokenId, embedding);
        AssertClose(expected.Embedding, embedding, oracle);

        using var kv = new OlmoeKvCache(model.Configuration, contextSize: 16);
        using var attentionWorkspace = new OlmoeAttentionWorkspace(model.MemoryPlan);
        var sequence = new SequenceState(layer: 0, layerCount: 1, contextLimit: 16);
        var attention = new float[model.Configuration.HiddenSize];
        OlmoeAttention.RunToken(
            model,
            0,
            expected.AttentionInput.ToArray(),
            kv,
            sequence,
            attentionWorkspace,
            attention);
        AssertClose(expected.AttentionOutput, attention, oracle);

        using var cache = CreateMinimumCache(model);
        using var moeWorkspace = model.CreateMoeWorkspace();
        var moe = new float[model.Configuration.HiddenSize];
        var trace = new OlmoeMoeTrace();
        OlmoeMoeExecutor.RunTokenAsync(
                model,
                0,
                expected.RouterInput.ToArray(),
                cache,
                moeWorkspace,
                moe,
                CancellationToken.None,
                trace)
            .AsTask().GetAwaiter().GetResult();

        Assert.Equal(expected.SelectedExpertIds, trace.SelectedExpertIds);
        AssertClose(expected.RouterWeights, trace.SelectedWeights, oracle);
        AssertClose(expected.MoeOutput, trace.Output, oracle);
        AssertClose(expected.MoeOutput, moe, oracle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncrementalTeacherForcingMatchesEveryOracleLogit(bool quantizedExperts)
    {
        using var fixture = new OlmoeFixture(quantizedExperts);
        var oracle = OlmoeTinyReference.Create(fixture);
        using var model = fixture.LoadModel(contextSize: 16);
        using var cache = CreateMinimumCache(model);
        using var forward = new OlmoeForwardContext(model, cache, contextLimit: 16);

        foreach (var expected in oracle.TeacherForcing)
        {
            var logits = await forward.ForwardAsync(
                new[] { expected.InputTokenId },
                CancellationToken.None);

            AssertClose(expected.Logits, logits.ToArray(), oracle);
            Assert.Equal(expected.Position + 1, forward.Position);
        }
    }

    [Fact]
    public async Task GreedyDecodeMatchesOracle()
    {
        using var fixture = new OlmoeFixture();
        var oracle = OlmoeTinyReference.Create(fixture);
        using var model = fixture.LoadModel(contextSize: 16);
        using var cache = CreateMinimumCache(model);
        var generator = new OlmoeTextGenerator(model, cache);
        var options = CompletionOptions.Default with
        {
            ContextSize = 16,
            MaxOutputTokens = oracle.GreedyTokenIds.Count,
            Temperature = 0,
            TopK = 8,
            TopP = 1,
            PenaltyLastTokens = 0,
            RepeatPenalty = 1,
            FrequencyPenalty = 0,
            PresencePenalty = 0,
            StopSequences = []
        };

        var result = await generator.GenerateAsync(
            new OlmoePrompt(oracle.PromptTokenIds, []),
            options,
            CancellationToken.None);

        Assert.Equal(oracle.GreedyTokenIds, result.GeneratedTokenIds);
        Assert.Equal("length", result.StopReason);
        Assert.Equal(oracle.PromptTokenIds.Count, result.PromptTokenCount);
    }

    private static ExpertCache CreateMinimumCache(ManagedOlmoeModel model)
    {
        var minimum = checked(
            model.ExpertLayout.SlotBudgetedBytes *
            model.ExpertLayout.MoeLayerCount *
            model.Configuration.ExpertsPerToken);
        return model.CreateExpertCache(new ExpertCacheOptions(
            minimum,
            WorkerCount: 1,
            QueueCapacity: model.Configuration.ExpertsPerToken));
    }

    private static void AssertClose(
        IReadOnlyList<float> expected,
        IReadOnlyList<float> actual,
        OlmoeTinyOracle oracle)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var error = Math.Abs(expected[index] - actual[index]);
            var limit = oracle.AbsoluteTolerance +
                (oracle.RelativeTolerance * Math.Abs(expected[index]));
            Assert.True(
                error <= limit,
                $"Value {index} differs: expected {expected[index]}, actual {actual[index]}, limit {limit}.");
        }
    }
}
