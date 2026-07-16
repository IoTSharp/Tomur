using Tomur.Providers;
using Tomur.Providers.Glm;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeMemoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MemoryPlanBalancesResidentKvScratchAndMinimumExpertCache(bool quantizedExperts)
    {
        using var fixture = new OlmoeFixture(quantizedExperts);
        using var model = fixture.LoadModel(contextSize: 8);
        var plan = model.MemoryPlan;

        Assert.Equal(plan.ResidentBytes, model.ActualResidentBytes);
        Assert.Equal(
            (long)model.Configuration.LayerCount *
            plan.ContextSize *
            model.Configuration.KeyValueSize *
            2 *
            sizeof(float),
            plan.KvBytes);

        var tensorWorkspaceBytes = checked(
            ((long)plan.ActivationCapacity * sizeof(float)) +
            plan.QuantizationCapacity +
            ((long)plan.OutputCapacity * sizeof(float)));
        var attentionWorkspaceBytes = checked(
            ((long)plan.AttentionActivationCapacity * sizeof(float)) +
            ((long)plan.AttentionScoreCapacity * sizeof(float)));
        Assert.Equal(
            tensorWorkspaceBytes +
            attentionWorkspaceBytes +
            plan.ForwardWorkspaceBytes +
            plan.SamplingWorkspaceBytes +
            plan.MoeWorkspaceBytes,
            plan.ScratchBytes);
        Assert.Equal(
            plan.ResidentBytes + plan.KvBytes + plan.ScratchBytes,
            plan.RequiredBytes);

        var provider = (IModelReadinessProvider)new ManagedOlmoeProvider();
        var readiness = provider.InspectModel(fixture.Descriptor, new ModelSessionOptions(8));
        var minimumExpertCacheBytes = checked(
            model.ExpertLayout.SlotBudgetedBytes *
            model.ExpertLayout.MoeLayerCount *
            model.Configuration.ExpertsPerToken);
        Assert.Equal(minimumExpertCacheBytes, readiness.ExpertCacheBytes);
        Assert.Equal(plan.ResidentBytes, readiness.ResidentBytes);
        Assert.Equal(plan.KvBytes, readiness.KvBytes);
        Assert.Equal(plan.ScratchBytes, readiness.ScratchBytes);
        Assert.Equal(
            readiness.ResidentBytes +
            readiness.KvBytes +
            readiness.ScratchBytes +
            readiness.ExpertCacheBytes,
            readiness.RequiredBytes);
    }

    [Fact]
    public void SessionSnapshotUsesTheSameMemoryLedger()
    {
        using var fixture = new OlmoeFixture();
        var provider = new ManagedOlmoeProvider();
        var readiness = ((IModelReadinessProvider)provider).InspectModel(
            fixture.Descriptor,
            new ModelSessionOptions(8));
        using var session = provider.CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(8));

        var snapshot = session.GetSnapshot();

        Assert.Equal(readiness.ResidentBytes, snapshot.ResidentBytes);
        Assert.Equal(readiness.KvBytes, snapshot.KvBytes);
        Assert.Equal(readiness.ScratchBytes, snapshot.ScratchBytes);
        Assert.Equal(readiness.ExpertCacheBytes, snapshot.ExpertCacheBytes);
        Assert.Contains(
            snapshot.Diagnostics,
            value => value.StartsWith(
                $"load budget bytes: {readiness.RequiredBytes - readiness.ExpertCacheBytes}/",
                StringComparison.Ordinal));
    }
}
