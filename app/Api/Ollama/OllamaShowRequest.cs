using System.Text.Json.Serialization;

namespace Tomur.Api.Ollama;

public sealed record OllamaShowRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("verbose")] bool? Verbose)
{
    [JsonIgnore]
    public string? RequestedModel => string.IsNullOrWhiteSpace(Model) ? Name : Model;
}
