using System.Text.Json;
using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Providers;

/// <summary>
/// Stable host-side contract for a typed decision provider. This contract is
/// intentionally separate from text generation and does not expose provider
/// implementation types.
/// </summary>
public static class DecisionProviderContract
{
    public const int Version = 1;
    public const string ProviderId = "managed-decision";
    public const string Capability = "decision";

    public static Version? AssemblyVersion
        => typeof(IDecisionProvider).Assembly.GetName().Version;
}

/// <summary>Stable capability names used by model catalog and provider probes.</summary>
public static class DecisionCapabilities
{
    public const string Decision = DecisionProviderContract.Capability;
    public const string Choice = "choice";
    public const string Score = "score";
    public const string Boolean = "boolean";
}

/// <summary>Stable error codes shared by the host contract and later API mapping.</summary>
public static class DecisionErrorCodes
{
    public const string InvalidJson = "decision_invalid_json";
    public const string InvalidRequest = "decision_invalid_request";
    public const string InputLimitExceeded = "decision_input_limit_exceeded";
    public const string ModelNotInstalled = "decision_model_not_installed";
    public const string ArchitectureUnsupported = "decision_architecture_unsupported";
    public const string BackendUnavailable = "decision_backend_unavailable";
    public const string SessionBusy = "decision_session_busy";
    public const string TokenBudgetExceeded = "decision_token_budget_exceeded";
    public const string QuestionLimitExceeded = "decision_question_limit_exceeded";
    public const string Cancelled = "decision_cancelled";
    public const string DeadlineExceeded = "decision_deadline_exceeded";
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionQuestionType>))]
public enum DecisionQuestionType
{
    [JsonStringEnumMemberName("choice")]
    Choice,

    [JsonStringEnumMemberName("score")]
    Score,

    [JsonStringEnumMemberName("boolean")]
    Boolean,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionAnswerStatus>))]
public enum DecisionAnswerStatus
{
    [JsonStringEnumMemberName("answered")]
    Answered,

    [JsonStringEnumMemberName("abstained")]
    Abstained,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionCalibrationStatus>))]
public enum DecisionCalibrationStatus
{
    [JsonStringEnumMemberName("uncalibrated")]
    Uncalibrated,

    [JsonStringEnumMemberName("calibrated")]
    Calibrated,

    [JsonStringEnumMemberName("out_of_scope")]
    OutOfScope,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionBackend>))]
public enum DecisionBackend
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("cpu")]
    Cpu,

    [JsonStringEnumMemberName("cuda")]
    Cuda,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionReadiness>))]
public enum DecisionReadiness
{
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("degraded")]
    Degraded,

    [JsonStringEnumMemberName("ready")]
    Ready,
}

[JsonConverter(typeof(JsonStringEnumConverter<DecisionSessionState>))]
public enum DecisionSessionState
{
    [JsonStringEnumMemberName("unloaded")]
    Unloaded,

    [JsonStringEnumMemberName("loading")]
    Loading,

    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("busy")]
    Busy,

    [JsonStringEnumMemberName("faulted")]
    Faulted,

    [JsonStringEnumMemberName("disposed")]
    Disposed,
}

/// <summary>
/// Input limits checked before model memory is allocated. Limits are part of
/// the host contract so HTTP and in-process callers share the same boundary.
/// </summary>
public sealed record DecisionRequestLimits
{
    public static DecisionRequestLimits Default { get; } = new();

    [JsonPropertyName("max_request_bytes")]
    public int MaxRequestBytes { get; init; } = 1 * 1024 * 1024;

    [JsonPropertyName("max_questions")]
    public int MaxQuestions { get; init; } = 32;

    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; init; } = 32;

    [JsonPropertyName("max_score_criteria")]
    public int MaxScoreCriteria { get; init; } = 10;

    [JsonPropertyName("max_tokens_per_question")]
    public int MaxTokensPerQuestion { get; init; } = 1024;

    [JsonPropertyName("max_json_depth")]
    public int MaxJsonDepth { get; init; } = 32;

    [JsonPropertyName("max_state_bytes")]
    public int MaxStateBytes { get; init; } = 256 * 1024;

    [JsonPropertyName("max_instruction_bytes")]
    public int MaxInstructionBytes { get; init; } = 64 * 1024;

    [JsonPropertyName("max_identifier_length")]
    public int MaxIdentifierLength { get; init; } = 128;

    public void Validate()
    {
        if (MaxRequestBytes <= 0 ||
            MaxQuestions <= 0 ||
            MaxCandidates < 2 ||
            MaxScoreCriteria < 2 ||
            MaxTokensPerQuestion <= 0 ||
            MaxJsonDepth <= 0 ||
            MaxStateBytes <= 0 ||
            MaxInstructionBytes <= 0 ||
            MaxIdentifierLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DecisionRequestLimits),
                "Decision request limits must be positive and candidates/score criteria must be at least two.");
        }
    }
}

/// <summary>
/// Per-session compute budget. A provider must reject work that exceeds this
/// budget before starting an unbounded encode or score operation.
/// </summary>
public sealed record DecisionResourceBudget
{
    public static DecisionResourceBudget Default { get; } = new();

    [JsonPropertyName("max_questions")]
    public int MaxQuestions { get; init; } = 32;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 4096;

    [JsonPropertyName("deadline")]
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxQuestions <= 0 ||
            MaxTokens <= 0 ||
            Deadline <= TimeSpan.Zero ||
            Deadline > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DecisionResourceBudget),
                "Decision resource budget must be positive and the deadline cannot exceed five minutes.");
        }
    }
}

public sealed record DecisionSessionOptions
{
    [JsonPropertyName("limits")]
    public DecisionRequestLimits Limits { get; init; } = DecisionRequestLimits.Default;

    [JsonPropertyName("budget")]
    public DecisionResourceBudget Budget { get; init; } = DecisionResourceBudget.Default;

    public void Validate()
    {
        (Limits ?? throw new ArgumentNullException(nameof(Limits))).Validate();
        (Budget ?? throw new ArgumentNullException(nameof(Budget))).Validate();
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DecisionChoiceQuestion), "choice")]
[JsonDerivedType(typeof(DecisionScoreQuestion), "score")]
[JsonDerivedType(typeof(DecisionBooleanQuestion), "boolean")]
public abstract record DecisionQuestion
{
    [JsonPropertyName("instructions")]
    public required JsonElement Instructions { get; init; }
}

public sealed record DecisionChoiceQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyDictionary<string, JsonElement> Criteria { get; init; }
}

public sealed record DecisionScoreQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    public required IReadOnlyList<JsonElement> Criteria { get; init; }
}

public sealed record DecisionBooleanQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    public DecisionBooleanCriteria? Criteria { get; init; }
}

public sealed record DecisionBooleanCriteria
{
    [JsonPropertyName("true")]
    public required JsonElement WhenTrue { get; init; }

    [JsonPropertyName("false")]
    public required JsonElement WhenFalse { get; init; }
}

public sealed record DecisionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }

    [JsonPropertyName("questions")]
    public required IReadOnlyDictionary<string, DecisionQuestion> Questions { get; init; }
}

public sealed record DecisionCalibrationInfo
{
    [JsonPropertyName("status")]
    public required DecisionCalibrationStatus Status { get; init; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DecisionChoiceAnswer), "choice")]
[JsonDerivedType(typeof(DecisionScoreAnswer), "score")]
[JsonDerivedType(typeof(DecisionBooleanAnswer), "boolean")]
public abstract record DecisionAnswer
{
    [JsonPropertyName("status")]
    public required DecisionAnswerStatus Status { get; init; }

    [JsonPropertyName("abstention_reason")]
    public string? AbstentionReason { get; init; }

    [JsonPropertyName("calibration")]
    public required DecisionCalibrationInfo Calibration { get; init; }
}

public sealed record DecisionChoiceAnswer : DecisionAnswer
{
    [JsonPropertyName("choice")]
    public required string Choice { get; init; }

    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    [JsonPropertyName("concentration")]
    public required double Concentration { get; init; }
}

public sealed record DecisionScoreAnswer : DecisionAnswer
{
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("legend")]
    public required IReadOnlyDictionary<string, JsonElement> Legend { get; init; }

    [JsonPropertyName("probabilities")]
    public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

    [JsonPropertyName("concentration")]
    public required double Concentration { get; init; }
}

public sealed record DecisionBooleanAnswer : DecisionAnswer
{
    [JsonPropertyName("probability_true")]
    public required double ProbabilityTrue { get; init; }
}

public sealed record DecisionUsage
{
    [JsonPropertyName("question_count")]
    public required int QuestionCount { get; init; }

    [JsonPropertyName("token_count")]
    public required int TokenCount { get; init; }

    [JsonPropertyName("micro_batch_count")]
    public required int MicroBatchCount { get; init; }
}

public sealed record DecisionResult
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("model_revision")]
    public required string ModelRevision { get; init; }

    [JsonPropertyName("backend")]
    public required DecisionBackend Backend { get; init; }

    [JsonPropertyName("answers")]
    public required IReadOnlyDictionary<string, DecisionAnswer> Answers { get; init; }

    [JsonPropertyName("usage")]
    public DecisionUsage? Usage { get; init; }
}

/// <summary>Structured diagnostic for an unavailable or rejected decision call.</summary>
public sealed record DecisionError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<string> Actions { get; init; } = [];

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}

public sealed record DecisionStatus
{
    [JsonPropertyName("provider_id")]
    public required string ProviderId { get; init; }

    [JsonPropertyName("contract_version")]
    public required int ContractVersion { get; init; }

    [JsonPropertyName("readiness")]
    public required DecisionReadiness Readiness { get; init; }

    [JsonPropertyName("session_state")]
    public required DecisionSessionState SessionState { get; init; }

    [JsonPropertyName("provider_built_in")]
    public bool ProviderBuiltIn { get; init; }

    [JsonPropertyName("driver_available")]
    public bool DriverAvailable { get; init; }

    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [JsonPropertyName("model_revision")]
    public string? ModelRevision { get; init; }

    [JsonPropertyName("backend")]
    public DecisionBackend Backend { get; init; } = DecisionBackend.Unknown;

    [JsonPropertyName("assets_verified")]
    public bool AssetsVerified { get; init; }

    [JsonPropertyName("schema_valid")]
    public bool SchemaValid { get; init; }

    [JsonPropertyName("calibration_status")]
    public DecisionCalibrationStatus CalibrationStatus { get; init; } = DecisionCalibrationStatus.Uncalibrated;

    [JsonPropertyName("multilingual_quality_verified")]
    public bool MultilingualQualityVerified { get; init; }

    [JsonPropertyName("aot_smoke_verified")]
    public bool AotSmokeVerified { get; init; }

    [JsonPropertyName("latency_verified")]
    public bool LatencyVerified { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<DecisionError> Diagnostics { get; init; } = [];
}

public interface IDecisionProvider
{
    string Id { get; }

    bool CanHandle(LocalModelDescriptor model);

    IDecisionSession CreateSession(LocalModelDescriptor model, DecisionSessionOptions options);
}

public interface IDecisionSession : IDisposable
{
    string ProviderId { get; }

    DecisionSessionState State { get; }

    DecisionStatus GetStatus();

    DecisionResult Evaluate(
        DecisionRequest request,
        CancellationToken cancellationToken = default);
}
