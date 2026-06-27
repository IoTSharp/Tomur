using System.Text.Json.Serialization;
using Tomur.Runtime;

namespace Tomur.Api.OpenAI;

public sealed record OpenAiErrorResponse(
    [property: JsonPropertyName("error")] OpenAiError Error)
{
    public static OpenAiErrorResponse RuntimeUnavailable(RuntimeDiagnostic diagnostic)
    {
        return FromDiagnostic(diagnostic, "runtime_unavailable", "model");
    }

    public static OpenAiErrorResponse ModelNotDownloaded(RuntimeDiagnostic diagnostic)
    {
        return FromDiagnostic(diagnostic, "invalid_request_error", "model");
    }

    public static OpenAiErrorResponse ContextLengthExceeded(RuntimeDiagnostic diagnostic)
    {
        return FromDiagnostic(diagnostic, "invalid_request_error", "messages");
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

    private static OpenAiErrorResponse FromDiagnostic(RuntimeDiagnostic diagnostic, string type, string? param)
    {
        return new OpenAiErrorResponse(
            new OpenAiError(
                diagnostic.Message,
                type,
                diagnostic.Code,
                param,
                diagnostic));
    }
}

public sealed record OpenAiError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("diagnostic")] RuntimeDiagnostic? Diagnostic);
