using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaGenerateResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("context")] IReadOnlyList<int>? Context,
    [property: JsonPropertyName("total_duration")] long TotalDuration,
    [property: JsonPropertyName("load_duration")] long LoadDuration,
    [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount,
    [property: JsonPropertyName("eval_count")] int EvalCount)
{
    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; init; }
}

public sealed record OllamaChatResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("message")] OllamaChatMessage Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("total_duration")] long TotalDuration,
    [property: JsonPropertyName("load_duration")] long LoadDuration,
    [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount,
    [property: JsonPropertyName("eval_count")] int EvalCount)
{
    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; init; }
}
