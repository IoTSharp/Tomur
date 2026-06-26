using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiErrorResponse(
    [property: JsonPropertyName("error")] OpenAiError Error)
{
    public static OpenAiErrorResponse RuntimeUnavailable(RuntimeDiagnostic diagnostic)
    {
        return new OpenAiErrorResponse(
            new OpenAiError(
                diagnostic.Message,
                "runtime_unavailable",
                diagnostic.Code,
                "model",
                diagnostic));
    }

    public static OpenAiErrorResponse InvalidRequest(string message)
    {
        return new OpenAiErrorResponse(
            new OpenAiError(
                message,
                "invalid_request_error",
                "invalid_request",
                null,
                null));
    }
}

public sealed record OpenAiError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic);
