namespace Tomur.Multimodal;

public sealed record NativeOperationResult(
    string Text,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);

public sealed record NativeImageResult(
    byte[] Bytes,
    string Format,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);

public sealed record NativeAudioResult(
    byte[] Bytes,
    string Format,
    string MediaType,
    int SampleRate,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);

public sealed record ImageInputBytes(
    byte[] Bytes,
    string? MediaType,
    string? Detail);

public sealed record ImageGenerationOptions(
    string Prompt,
    string? NegativePrompt,
    int Width,
    int Height,
    int Steps,
    float CfgScale,
    long Seed,
    string? SampleMethod,
    string? Scheduler);

public sealed record SpeechSynthesisOptions(
    string Text,
    string? Voice,
    string ResponseFormat,
    double Speed,
    string? Language);
