using System.Text.Json;
using Tomur.Providers;

namespace Tomur.Providers.M1.Tests;

public sealed class DecisionContractsTests
{
    [Fact]
    public void ProviderIdentityAndCapabilitiesRemainStable()
    {
        Assert.Equal(1, DecisionProviderContract.Version);
        Assert.Equal("managed-decision", DecisionProviderContract.ProviderId);
        Assert.Equal("decision", DecisionProviderContract.Capability);
        Assert.Equal(DecisionProviderContract.Capability, DecisionCapabilities.Decision);
        Assert.Equal("choice", DecisionCapabilities.Choice);
        Assert.Equal("score", DecisionCapabilities.Score);
        Assert.Equal("boolean", DecisionCapabilities.Boolean);
    }

    [Fact]
    public void DefaultLimitsAndBudgetValidateAtTheirContractValues()
    {
        var limits = DecisionRequestLimits.Default;
        limits.Validate();

        Assert.Equal(1 * 1024 * 1024, limits.MaxRequestBytes);
        Assert.Equal(32, limits.MaxQuestions);
        Assert.Equal(32, limits.MaxCandidates);
        Assert.Equal(10, limits.MaxScoreCriteria);
        Assert.Equal(1024, limits.MaxTokensPerQuestion);
        Assert.Equal(32, limits.MaxJsonDepth);
        Assert.Equal(256 * 1024, limits.MaxStateBytes);
        Assert.Equal(64 * 1024, limits.MaxInstructionBytes);
        Assert.Equal(128, limits.MaxIdentifierLength);

        var budget = DecisionResourceBudget.Default;
        budget.Validate();

        Assert.Equal(32, budget.MaxQuestions);
        Assert.Equal(4096, budget.MaxTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), budget.Deadline);

        new DecisionSessionOptions().Validate();
    }

    [Fact]
    public void InvalidLimitsAndBudgetAreRejectedBeforeProviderExecution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionRequestLimits { MaxCandidates = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionResourceBudget { Deadline = TimeSpan.FromMinutes(6) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionResourceBudget { MaxTokens = 0 }.Validate());
    }

    [Fact]
    public void CoreErrorCodesRemainStable()
    {
        Assert.Equal("decision_model_not_installed", DecisionErrorCodes.ModelNotInstalled);
        Assert.Equal("decision_backend_unavailable", DecisionErrorCodes.BackendUnavailable);
        Assert.Equal("decision_token_budget_exceeded", DecisionErrorCodes.TokenBudgetExceeded);
        Assert.Equal("decision_cancelled", DecisionErrorCodes.Cancelled);
    }

    [Fact]
    public void EnumValuesUseStableJsonStrings()
    {
        Assert.Equal("\"choice\"", JsonSerializer.Serialize(DecisionQuestionType.Choice));
        Assert.Equal("\"answered\"", JsonSerializer.Serialize(DecisionAnswerStatus.Answered));
        Assert.Equal("\"out_of_scope\"", JsonSerializer.Serialize(DecisionCalibrationStatus.OutOfScope));
        Assert.Equal("\"cuda\"", JsonSerializer.Serialize(DecisionBackend.Cuda));
        Assert.Equal("\"ready\"", JsonSerializer.Serialize(DecisionReadiness.Ready));
        Assert.Equal("\"busy\"", JsonSerializer.Serialize(DecisionSessionState.Busy));
    }

    [Fact]
    public void DecisionQuestionPolymorphismUsesTypedJsonDiscriminator()
    {
        using var instructions = JsonDocument.Parse("{\"prompt\":\"choose one\"}");
        using var safeCriteria = JsonDocument.Parse("{\"description\":\"safe\"}");
        var question = new DecisionChoiceQuestion
        {
            Instructions = instructions.RootElement.Clone(),
            Criteria = new Dictionary<string, JsonElement>
            {
                ["safe"] = safeCriteria.RootElement.Clone(),
            },
        };

        var json = JsonSerializer.Serialize<DecisionQuestion>(question);
        Assert.Contains("\"type\":\"choice\"", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<DecisionQuestion>(json);
        var typedQuestion = Assert.IsType<DecisionChoiceQuestion>(roundTrip);
        Assert.Equal("choose one", typedQuestion.Instructions.GetProperty("prompt").GetString());
        Assert.Contains("safe", typedQuestion.Criteria.Keys);
    }
}
