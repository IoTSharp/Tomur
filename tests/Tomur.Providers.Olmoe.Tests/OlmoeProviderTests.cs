using Tomur.Inference;
using Tomur.Providers.Glm;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeProviderTests
{
    [Fact]
    public void ProbeAcceptsSignedInt8RowwiseQsExperts()
    {
        using var fixture = new OlmoeFixture(quantizedExperts: true);
        var probe = OlmoeModelDirectoryProbe.Read(
            fixture.Descriptor,
            ManagedOlmoeProvider.ProviderId);

        Assert.Equal(2, probe.Configuration.AttentionHeadCount);
        Assert.Equal(2, probe.Configuration.RoutedExpertCount);
        Assert.Equal("rowwise-qs", probe.Manifest.QuantizationLayout);
        Assert.Contains("model.layers.0.mlp.experts.0.gate_proj.weight.qs", probe.Tensors.Items.Select(x => x.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForwardRunsForQuantizedAndFloatingExperts(bool quantizedExperts)
    {
        using var fixture = new OlmoeFixture(quantizedExperts);
        using var model = fixture.LoadModel();
        using var cache = CreateMinimumCache(model);
        using var forward = new OlmoeForwardContext(model, cache, contextLimit: 8);

        var logits = await forward.ForwardAsync(new[] { 4 }, CancellationToken.None);

        Assert.Equal(8, logits.Length);
        Assert.All(logits.ToArray(), value => Assert.True(float.IsFinite(value)));
        Assert.Equal(1, forward.Position);
        Assert.Equal(1L, cache.GetSnapshot().DiskReads);
    }

    [Fact]
    public void ProviderChatUsesOlmoeTemplateAndTracksUsage()
    {
        using var fixture = new OlmoeFixture();
        var provider = new ManagedOlmoeProvider();
        using var session = provider.CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(8));
        var options = CompletionOptions.Default with
        {
            ContextSize = 8,
            MaxOutputTokens = 1,
            Temperature = 0,
            TopK = 8,
            TopP = 1,
            StopSequences = []
        };

        var result = ((IChatGenerationSession)session).GenerateChat(
            [new ChatTurn("user", "hello")],
            options,
            CancellationToken.None);

        Assert.Equal(1, result.Usage.CompletionTokens);
        Assert.True(result.Usage.PromptTokens > 1);
        Assert.Equal("hello", result.Text);
        var snapshot = session.GetSnapshot();
        Assert.Equal("managed-olmoe-generation", snapshot.Mode);
        Assert.Equal(1L, snapshot.RequestCount);
        Assert.Contains(snapshot.Diagnostics, value => value == "provider: managed-olmoe");
    }

    [Fact]
    public void PromptTemplateUsesConfiguredBosEosAndAssistantGenerationPrompt()
    {
        using var fixture = new OlmoeFixture();
        var probe = OlmoeModelDirectoryProbe.Read(fixture.Descriptor, ManagedOlmoeProvider.ProviderId);
        var prompt = new OlmoePromptTemplate(probe.Configuration, probe.Tokenizer).BuildChat(
            [new ChatTurn("system", "Tomur"), new ChatTurn("user", "hello")]);

        Assert.Equal(probe.Configuration.EosTokenId, prompt.TokenIds[0]);
        Assert.Equal([probe.Configuration.EosTokenId], prompt.StopTokenIds);
        Assert.True(prompt.TokenIds.Count >= 4);
    }

    [Fact]
    public void SplitHalfRopeMatchesReferenceFormula()
    {
        float[] values = [1, 2, 3, 4];
        OlmoeAttention.ApplySplitHalfRope(values, position: 1, theta: 10000);

        Assert.Equal((float)(Math.Cos(1) - (3 * Math.Sin(1))), values[0], 5);
        Assert.Equal((float)((3 * Math.Cos(1)) + Math.Sin(1)), values[2], 5);
        Assert.Equal((float)((2 * Math.Cos(0.01)) - (4 * Math.Sin(0.01))), values[1], 5);
        Assert.Equal((float)((4 * Math.Cos(0.01)) + (2 * Math.Sin(0.01))), values[3], 5);
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
}
