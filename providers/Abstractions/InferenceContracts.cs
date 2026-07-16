namespace Tomur.Inference;

public sealed record ChatTurn(string Role, string Content);

public sealed record CompletionOptions(
    int MaxOutputTokens,
    int ContextSize,
    float Temperature,
    float TopP,
    int TopK,
    int PenaltyLastTokens,
    float RepeatPenalty,
    float FrequencyPenalty,
    float PresencePenalty,
    int Seed,
    IReadOnlyList<string> StopSequences)
{
    public static CompletionOptions Default { get; } = new(
        MaxOutputTokens: 256,
        ContextSize: 4096,
        Temperature: 0.7f,
        TopP: 0.9f,
        TopK: 40,
        PenaltyLastTokens: 128,
        RepeatPenalty: 1.05f,
        FrequencyPenalty: 0.0f,
        PresencePenalty: 0.0f,
        Seed: -1,
        StopSequences: []);
}

public sealed record CompletionResult(
    string Text,
    TokenUsage Usage,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);

public sealed record EmbeddingResult(
    IReadOnlyList<float> Vector,
    TokenUsage Usage,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);

public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public sealed record SessionSnapshot(
    bool Loaded,
    string? ModelId,
    string? ModelPath,
    string? Mode,
    DateTimeOffset? LoadedAt,
    long RequestCount,
    long PromptTokens,
    long CompletionTokens,
    IReadOnlyList<string> Diagnostics)
{
    public string? ProviderId { get; init; }

    public string? Architecture { get; init; }

    public string? Quantization { get; init; }

    public string? ExecutionBackend { get; init; }

    public string? ExecutionDetail { get; init; }

    public bool Busy { get; init; }

    public int? ContextSize { get; init; }

    public long? ResidentBytes { get; init; }

    public long? KvBytes { get; init; }

    public long? ScratchBytes { get; init; }

    public long? ExpertCacheBytes { get; init; }

    public long? ExpertCacheHits { get; init; }

    public long? ExpertCacheMisses { get; init; }

    public long? ExpertCacheEvictions { get; init; }

    public long? ExpertDiskReads { get; init; }

    public long? ExpertDiskBytes { get; init; }

    public long? LoadElapsedMilliseconds { get; init; }

    public double? LastFirstTokenMilliseconds { get; init; }

    public double? LastGenerationMilliseconds { get; init; }

    public double? LastOutputTokensPerSecond { get; init; }

    public double? LastDecodeTokensPerSecond { get; init; }

    public SessionErrorSnapshot? LastError { get; init; }
}

public sealed record SessionErrorSnapshot(
    string Code,
    string Message,
    DateTimeOffset OccurredAt);

public sealed class InferenceException : Exception
{
    public InferenceException(string code, string message, IReadOnlyList<string>? actions = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Actions = actions ?? [];
    }

    public string Code { get; }

    public IReadOnlyList<string> Actions { get; }
}
