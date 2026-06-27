using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaShowResponse(
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("modelfile")] string Modelfile,
    [property: JsonPropertyName("parameters")] string Parameters,
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("details")] OllamaModelDetails Details,
    [property: JsonPropertyName("model_info")] OllamaModelInfo ModelInfo,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record OllamaModelInfo(
    [property: JsonPropertyName("general.architecture")] string Architecture,
    [property: JsonPropertyName("general.file_type")] string FileType,
    [property: JsonPropertyName("general.name")] string Name);
