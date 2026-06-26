using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Api.Ollama;

public sealed record OllamaErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic)
{
    public static OllamaErrorResponse RuntimeUnavailable(RuntimeDiagnostic diagnostic)
    {
        return new OllamaErrorResponse(diagnostic.Message, diagnostic.Code, diagnostic);
    }

    public static OllamaErrorResponse InvalidRequest(string message)
    {
        return new OllamaErrorResponse(message, "invalid_request", null);
    }
}
