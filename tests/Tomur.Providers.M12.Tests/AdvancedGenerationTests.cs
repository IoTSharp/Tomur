using Tomur.Providers.Glm;

namespace Tomur.Providers.M12.Tests;

public sealed class AdvancedGenerationTests
{
    [Fact]
    public void DsaRetainsDenseSoftmaxWhenTopKIncludesEveryCausalKey()
    {
        float[] dense = [1.5f, -0.25f, 0.5f, 2.0f];
        var sparse = dense.ToArray();

        ScalarKernels.SoftmaxInPlace(dense);
        DsaCausalSelector.SoftmaxSelectedInPlace(sparse, sparse.Length);

        Assert.Equal(dense, sparse);
    }

    [Fact]
    public void DsaSelectionIsStableAndMasksUnselectedKeys()
    {
        float[] scores = [0.5f, 2.0f, 2.0f, -1.0f];
        Span<int> selected = stackalloc int[2];

        var count = DsaCausalSelector.SelectTopK(scores, 2, selected);
        DsaCausalSelector.SoftmaxSelectedInPlace(scores, 2);

        Assert.Equal(2, count);
        Assert.Equal(new[] { 1, 2 }, selected.ToArray());
        Assert.Equal(0f, scores[0]);
        Assert.Equal(0.5f, scores[1]);
        Assert.Equal(0.5f, scores[2]);
        Assert.Equal(0f, scores[3]);
    }

    [Fact]
    public void SpeculativeVerificationAcceptsDraftAndUsesResidualOnRejection()
    {
        float[] target = [0.6f, 0.3f, 0.1f];
        float[] draft = [0.2f, 0.7f, 0.1f];

        var accepted = SpeculativeDecoder.VerifySingleDraft(
            0,
            target,
            draft,
            acceptanceSample: 0.99,
            rejectionSample: 0.5);
        var rejected = SpeculativeDecoder.VerifySingleDraft(
            1,
            target,
            draft,
            acceptanceSample: 0.9,
            rejectionSample: 0.5);

        Assert.True(accepted.DraftAccepted);
        Assert.Equal(0, accepted.TokenId);
        Assert.False(rejected.DraftAccepted);
        Assert.Equal(0, rejected.TokenId);
        Assert.InRange(rejected.AcceptanceProbability, 0.4285f, 0.4286f);
    }

    [Fact]
    public void ForcedSpanGrammarOnlyConstrainsDeclaredPositions()
    {
        var grammar = new ForcedSpanGrammar(
            [new ForcedTokenSpan(1, [3, 4])],
            vocabularySize: 6);
        float[] unconstrained = [1, 2, 3, 4, 5, 6];
        var first = unconstrained.ToArray();
        var second = unconstrained.ToArray();

        Assert.False(grammar.Apply(0, first));
        Assert.True(grammar.Apply(1, second));
        Assert.Equal(unconstrained, first);
        Assert.Equal(4, second[3]);
        Assert.All(
            second.Where((_, index) => index != 3),
            value => Assert.Equal(float.MinValue, value));
    }
}
