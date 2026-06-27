namespace Tomur.Inference;

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
    IReadOnlyList<string> Diagnostics);

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
