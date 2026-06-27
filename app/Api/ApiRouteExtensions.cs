using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Tomur.Api.OpenAI;
using Tomur.Api.Ollama;
using Tomur.Config;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Serialization;
using Tomur.Services;

namespace Tomur.Api;

public static class ApiRouteExtensions
{
    public static void MapApiRoutes(this WebApplication app)
    {
        app.MapHealthChecks("/health", HealthEndpoint.Options);

        app.MapGet("/api/version", static async (HttpContext context, VersionProvider versionProvider) =>
        {
            var response = versionProvider.GetVersionResponse();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.VersionResponse);
        });

        app.MapGet("/api/runtime/status", static async (HttpContext context, RuntimeDiagnosticsProvider diagnosticsProvider) =>
        {
            var response = diagnosticsProvider.GetRuntimeStatus();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.RuntimeStatusResponse);
        });

        app.MapGet("/api/runtime/native", static async (HttpContext context, RuntimeDiagnosticsProvider diagnosticsProvider) =>
        {
            var response = diagnosticsProvider.GetRuntimeStatus().NativeBundle;
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.NativeBundleProbeResult);
        });

        app.MapPost("/api/runtime/native/prepare", static async (
            HttpContext context,
            INativeBundlePreparer nativeBundlePreparer) =>
        {
            var response = nativeBundlePreparer.Prepare();
            var statusCode = response.Status == "error"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;

            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.NativeBundlePrepareResult,
                statusCode);
        });

        app.MapGet("/api/runtime/native/{componentId}/{libraryName}", static async (
            HttpContext context,
            INativeLibraryResolver libraryResolver,
            string componentId,
            string libraryName) =>
        {
            var response = libraryResolver.Resolve(componentId, libraryName);
            var statusCode = response.Exists ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.NativeLibraryResolution,
                statusCode);
        });

        app.MapPost("/api/runtime/native/{componentId}/{libraryName}/load", static async (
            HttpContext context,
            INativeLibraryLoader libraryLoader,
            string componentId,
            string libraryName) =>
        {
            var response = libraryLoader.Load(componentId, libraryName);
            var statusCode = response.Loaded
                ? StatusCodes.Status200OK
                : response.Resolution.Exists
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status404NotFound;

            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.NativeLibraryLoadResult,
                statusCode);
        });

        app.MapGet("/v1/models", static async (HttpContext context) =>
        {
            var response = new OpenAiModelListResponse(Array.Empty<OpenAiModelResponse>());
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.OpenAiModelListResponse);
        });

        app.MapPost("/v1/chat/completions", HandleOpenAiChatCompletionsAsync);
        app.MapPost("/api/chat", HandleOllamaChatAsync);

        app.MapGet("/", static async (HttpContext context) =>
        {
            var response = new RootResponse(
                Defaults.ProductName,
                "Tomur local API is running.",
                [
                    "/health",
                    "/api/version",
                    "/api/runtime/status",
                    "/api/runtime/native",
                    "POST /api/runtime/native/prepare",
                    "/api/runtime/native/{componentId}/{libraryName}",
                    "POST /api/runtime/native/{componentId}/{libraryName}/load",
                    "/v1/models",
                    "/v1/chat/completions",
                    "/api/chat"
                ]);

            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.RootResponse);
        });
    }

    private static async Task HandleOpenAiChatCompletionsAsync(HttpContext context, RuntimeDiagnosticsProvider diagnosticsProvider)
    {
        OpenAiChatCompletionRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                AppJsonSerializerContext.Default.OpenAiChatCompletionRequest,
                context.RequestAborted);
        }
        catch (JsonException exception)
        {
            var error = OpenAiErrorResponse.InvalidRequest($"Invalid JSON request body: {exception.Message}");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            var error = OpenAiErrorResponse.InvalidRequest("Request body is required.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            var error = OpenAiErrorResponse.InvalidRequest("The model field is required.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            var error = OpenAiErrorResponse.InvalidRequest("The messages field must contain at least one message.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        var runtime = diagnosticsProvider.GetRuntimeUnavailable(request.Model);
        var response = OpenAiErrorResponse.RuntimeUnavailable(runtime);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task HandleOllamaChatAsync(HttpContext context, RuntimeDiagnosticsProvider diagnosticsProvider)
    {
        OllamaChatRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                AppJsonSerializerContext.Default.OllamaChatRequest,
                context.RequestAborted);
        }
        catch (JsonException exception)
        {
            var error = OllamaErrorResponse.InvalidRequest($"Invalid JSON request body: {exception.Message}");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OllamaErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            var error = OllamaErrorResponse.InvalidRequest("Request body is required.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OllamaErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            var error = OllamaErrorResponse.InvalidRequest("The model field is required.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OllamaErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            var error = OllamaErrorResponse.InvalidRequest("The messages field must contain at least one message.");
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OllamaErrorResponse,
                StatusCodes.Status400BadRequest);
            return;
        }

        var runtime = diagnosticsProvider.GetRuntimeUnavailable(request.Model);
        var response = OllamaErrorResponse.RuntimeUnavailable(runtime);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OllamaErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }
}
