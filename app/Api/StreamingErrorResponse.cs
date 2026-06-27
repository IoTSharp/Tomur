using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tomur.Api.Ollama;
using Tomur.Api.OpenAI;
using Tomur.Serialization;

namespace Tomur.Api;

internal static class StreamingErrorResponse
{
    public static async Task WriteOpenAiAsync(HttpContext context, OpenAiErrorResponse error)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";

        await context.Response.WriteAsync("event: error\n", context.RequestAborted);
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
    }

    public static async Task WriteOllamaAsync(HttpContext context, OllamaErrorResponse error)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            AppJsonSerializerContext.Default.OllamaErrorResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);
    }
}
