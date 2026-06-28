using System.Text.Json.Serialization;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiImageGenerationRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("n")] int? Count,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("response_format")] string? ResponseFormat,
    [property: JsonPropertyName("negative_prompt")] string? NegativePrompt,
    [property: JsonPropertyName("steps")] int? Steps,
    [property: JsonPropertyName("cfg_scale")] double? CfgScale,
    [property: JsonPropertyName("distilled_guidance")] double? DistilledGuidance,
    [property: JsonPropertyName("flow_shift")] double? FlowShift,
    [property: JsonPropertyName("seed")] long? Seed,
    [property: JsonPropertyName("sample_method")] string? SampleMethod,
    [property: JsonPropertyName("scheduler")] string? Scheduler);

public sealed record OpenAiImageGenerationResponse(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiImageGenerationData> Data);

public sealed record OpenAiImageGenerationData(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("b64_json")] string? B64Json,
    [property: JsonPropertyName("revised_prompt")] string RevisedPrompt);
