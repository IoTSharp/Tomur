using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiAudioSpeechRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input")] string? Input,
    [property: JsonPropertyName("voice")] string? Voice,
    [property: JsonPropertyName("response_format")] string? ResponseFormat,
    [property: JsonPropertyName("speed")] double? Speed,
    [property: JsonPropertyName("language")] string? Language);

public sealed record OpenAiAudioTranscriptionResponse(
    [property: JsonPropertyName("text")] string Text);
