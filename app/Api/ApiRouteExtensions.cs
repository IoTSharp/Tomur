using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Tomur.Api.Ollama;
using Tomur.Api.OpenAI;
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

        app.MapGet("/v1/models", HandleOpenAiModelsAsync);
        app.MapPost("/v1/chat/completions", HandleOpenAiChatCompletionsAsync);
        app.MapPost("/v1/completions", HandleOpenAiCompletionsAsync);
        app.MapPost("/v1/embeddings", HandleOpenAiEmbeddingsAsync);
        app.MapPost("/v1/images/generations", HandleOpenAiImageGenerationsAsync);

        app.MapGet("/api/tags", HandleOllamaTagsAsync);
        app.MapPost("/api/show", HandleOllamaShowAsync);
        app.MapPost("/api/generate", HandleOllamaGenerateAsync);
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
                    "POST /v1/chat/completions",
                    "POST /v1/completions",
                    "POST /v1/embeddings",
                    "POST /v1/images/generations",
                    "/api/tags",
                    "POST /api/show",
                    "POST /api/generate",
                    "POST /api/chat"
                ]);

            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.RootResponse);
        });
    }

    private static async Task HandleOpenAiModelsAsync(HttpContext context, LocalModelCatalog modelCatalog)
    {
        var models = modelCatalog
            .ListModels()
            .Select(static model => new OpenAiModelResponse(
                model.Id,
                new DateTimeOffset(model.LastModifiedUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
                "local"))
            .ToArray();

        var response = new OpenAiModelListResponse(models);
        await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.OpenAiModelListResponse);
    }

    private static async Task HandleOpenAiChatCompletionsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOpenAiRequestAsync(
            context,
            AppJsonSerializerContext.Default.OpenAiChatCompletionRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOpenAiModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, request.Stream == true);
        if (model is null)
        {
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The messages field must contain at least one message.");
            return;
        }

        var inputCharacters = request.Messages.Sum(static message => EstimateJsonElementCharacters(message.Content));
        if (!await RequireOpenAiInputWithinLimitAsync(context, diagnosticsProvider, request.Model, inputCharacters, request.Stream == true))
        {
            return;
        }

        await WriteOpenAiRuntimeUnavailableAsync(
            context,
            diagnosticsProvider.GetRuntimeUnavailable(model.Id),
            request.Stream == true);
    }

    private static async Task HandleOpenAiCompletionsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOpenAiRequestAsync(
            context,
            AppJsonSerializerContext.Default.OpenAiCompletionRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOpenAiModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, request.Stream == true);
        if (model is null)
        {
            return;
        }

        if (request.Prompt is null)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The prompt field is required.");
            return;
        }

        var inputCharacters = EstimateJsonElementCharacters(request.Prompt);
        if (!await RequireOpenAiInputWithinLimitAsync(context, diagnosticsProvider, request.Model, inputCharacters, request.Stream == true))
        {
            return;
        }

        await WriteOpenAiRuntimeUnavailableAsync(
            context,
            diagnosticsProvider.GetRuntimeUnavailable(model.Id),
            request.Stream == true);
    }

    private static async Task HandleOpenAiEmbeddingsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOpenAiRequestAsync(
            context,
            AppJsonSerializerContext.Default.OpenAiEmbeddingRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOpenAiModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream: false);
        if (model is null)
        {
            return;
        }

        if (request.Input is null)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The input field is required.");
            return;
        }

        var inputCharacters = EstimateJsonElementCharacters(request.Input);
        if (!await RequireOpenAiInputWithinLimitAsync(context, diagnosticsProvider, request.Model, inputCharacters, stream: false))
        {
            return;
        }

        var response = OpenAiErrorResponse.RuntimeUnavailable(diagnosticsProvider.GetRuntimeUnavailable(model.Id));
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task HandleOpenAiImageGenerationsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOpenAiRequestAsync(
            context,
            AppJsonSerializerContext.Default.OpenAiImageGenerationRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOpenAiModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream: false);
        if (model is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            await WriteOpenAiInvalidRequestAsync(context, "The prompt field is required.");
            return;
        }

        if (!await RequireOpenAiInputWithinLimitAsync(context, diagnosticsProvider, request.Model, request.Prompt.Length, stream: false))
        {
            return;
        }

        var response = OpenAiErrorResponse.RuntimeUnavailable(diagnosticsProvider.GetRuntimeUnavailable(model.Id));
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task HandleOllamaTagsAsync(HttpContext context, LocalModelCatalog modelCatalog)
    {
        var models = modelCatalog
            .ListModels()
            .Select(static model => new OllamaModelResponse(
                model.Id,
                model.Id,
                new DateTimeOffset(model.LastModifiedUtc, TimeSpan.Zero),
                model.SizeBytes,
                CreateDigest(model),
                new OllamaModelDetails(
                    model.Format,
                    model.Family,
                    "unknown",
                    model.QuantizationLevel)))
            .ToArray();

        var response = new OllamaModelListResponse(models);
        await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.OllamaModelListResponse);
    }

    private static async Task HandleOllamaShowAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOllamaRequestAsync(
            context,
            AppJsonSerializerContext.Default.OllamaShowRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOllamaModelAsync(context, request.RequestedModel, diagnosticsProvider, modelCatalog, stream: false);
        if (model is null)
        {
            return;
        }

        var response = new OllamaShowResponse(
            string.Empty,
            $"FROM {model.RelativePath}",
            string.Empty,
            string.Empty,
            new OllamaModelDetails(model.Format, model.Family, "unknown", model.QuantizationLevel),
            new OllamaModelInfo(model.Family, model.Format, model.Name),
            model.Capabilities);

        await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.OllamaShowResponse);
    }

    private static async Task HandleOllamaGenerateAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOllamaRequestAsync(
            context,
            AppJsonSerializerContext.Default.OllamaGenerateRequest);
        if (request is null)
        {
            return;
        }

        var stream = request.Stream != false;
        var model = await RequireOllamaModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream);
        if (model is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            await WriteOllamaInvalidRequestAsync(context, "The prompt field is required.");
            return;
        }

        if (!await RequireOllamaInputWithinLimitAsync(context, diagnosticsProvider, request.Model, request.Prompt.Length, stream))
        {
            return;
        }

        await WriteOllamaRuntimeUnavailableAsync(
            context,
            diagnosticsProvider.GetRuntimeUnavailable(model.Id),
            stream);
    }

    private static async Task HandleOllamaChatAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadOllamaRequestAsync(
            context,
            AppJsonSerializerContext.Default.OllamaChatRequest);
        if (request is null)
        {
            return;
        }

        var stream = request.Stream != false;
        var model = await RequireOllamaModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream);
        if (model is null)
        {
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            await WriteOllamaInvalidRequestAsync(context, "The messages field must contain at least one message.");
            return;
        }

        var inputCharacters = request.Messages.Sum(static message => message.Content?.Length ?? 0);
        if (!await RequireOllamaInputWithinLimitAsync(context, diagnosticsProvider, request.Model, inputCharacters, stream))
        {
            return;
        }

        await WriteOllamaRuntimeUnavailableAsync(
            context,
            diagnosticsProvider.GetRuntimeUnavailable(model.Id),
            stream);
    }

    private static async Task<T?> ReadOpenAiRequestAsync<T>(
        HttpContext context,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                jsonTypeInfo,
                context.RequestAborted);

            if (request is not null)
            {
                return request;
            }

            await WriteOpenAiInvalidRequestAsync(context, "Request body is required.");
            return default;
        }
        catch (JsonException exception)
        {
            await WriteOpenAiInvalidRequestAsync(context, $"Invalid JSON request body: {exception.Message}");
            return default;
        }
    }

    private static async Task<T?> ReadOllamaRequestAsync<T>(
        HttpContext context,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                jsonTypeInfo,
                context.RequestAborted);

            if (request is not null)
            {
                return request;
            }

            await WriteOllamaInvalidRequestAsync(context, "Request body is required.");
            return default;
        }
        catch (JsonException exception)
        {
            await WriteOllamaInvalidRequestAsync(context, $"Invalid JSON request body: {exception.Message}");
            return default;
        }
    }

    private static async Task<LocalModelDescriptor?> RequireOpenAiModelAsync(
        HttpContext context,
        string? requestedModel,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        bool stream)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            await WriteOpenAiInvalidRequestAsync(context, "The model field is required.");
            return null;
        }

        var resolvedModel = modelCatalog.Find(requestedModel);
        if (resolvedModel is null)
        {
            var response = OpenAiErrorResponse.ModelNotDownloaded(diagnosticsProvider.GetModelNotDownloaded(requestedModel));
            if (stream)
            {
                await StreamingErrorResponse.WriteOpenAiAsync(context, response);
            }
            else
            {
                await JsonHttpResponse.WriteAsync(
                    context,
                    response,
                    AppJsonSerializerContext.Default.OpenAiErrorResponse,
                    StatusCodes.Status404NotFound);
            }

            return null;
        }

        return resolvedModel;
    }

    private static async Task<LocalModelDescriptor?> RequireOllamaModelAsync(
        HttpContext context,
        string? requestedModel,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        bool stream)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            await WriteOllamaInvalidRequestAsync(context, "The model field is required.");
            return null;
        }

        var resolvedModel = modelCatalog.Find(requestedModel);
        if (resolvedModel is null)
        {
            var response = OllamaErrorResponse.ModelNotDownloaded(diagnosticsProvider.GetModelNotDownloaded(requestedModel));
            if (stream)
            {
                await StreamingErrorResponse.WriteOllamaAsync(context, response);
            }
            else
            {
                await JsonHttpResponse.WriteAsync(
                    context,
                    response,
                    AppJsonSerializerContext.Default.OllamaErrorResponse,
                    StatusCodes.Status404NotFound);
            }

            return null;
        }

        return resolvedModel;
    }

    private static async Task<bool> RequireOpenAiInputWithinLimitAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        string? model,
        int characterCount,
        bool stream)
    {
        if (characterCount <= CompatibilityProtocolLimits.MaxInputCharacters)
        {
            return true;
        }

        var error = OpenAiErrorResponse.ContextLengthExceeded(
            diagnosticsProvider.GetContextLengthExceeded(model, characterCount));
        if (stream)
        {
            await StreamingErrorResponse.WriteOpenAiAsync(context, error);
        }
        else
        {
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status400BadRequest);
        }

        return false;
    }

    private static async Task<bool> RequireOllamaInputWithinLimitAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        string? model,
        int characterCount,
        bool stream)
    {
        if (characterCount <= CompatibilityProtocolLimits.MaxInputCharacters)
        {
            return true;
        }

        var error = OllamaErrorResponse.ContextLengthExceeded(
            diagnosticsProvider.GetContextLengthExceeded(model, characterCount));
        if (stream)
        {
            await StreamingErrorResponse.WriteOllamaAsync(context, error);
        }
        else
        {
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.OllamaErrorResponse,
                StatusCodes.Status400BadRequest);
        }

        return false;
    }

    private static async Task WriteOpenAiRuntimeUnavailableAsync(
        HttpContext context,
        RuntimeDiagnostic diagnostic,
        bool stream)
    {
        var response = OpenAiErrorResponse.RuntimeUnavailable(diagnostic);
        if (stream)
        {
            await StreamingErrorResponse.WriteOpenAiAsync(context, response);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task WriteOllamaRuntimeUnavailableAsync(
        HttpContext context,
        RuntimeDiagnostic diagnostic,
        bool stream)
    {
        var response = OllamaErrorResponse.RuntimeUnavailable(diagnostic);
        if (stream)
        {
            await StreamingErrorResponse.WriteOllamaAsync(context, response);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OllamaErrorResponse,
            StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task WriteOpenAiInvalidRequestAsync(HttpContext context, string message)
    {
        var error = OpenAiErrorResponse.InvalidRequest(message);
        await JsonHttpResponse.WriteAsync(
            context,
            error,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status400BadRequest);
    }

    private static async Task WriteOllamaInvalidRequestAsync(HttpContext context, string message)
    {
        var error = OllamaErrorResponse.InvalidRequest(message);
        await JsonHttpResponse.WriteAsync(
            context,
            error,
            AppJsonSerializerContext.Default.OllamaErrorResponse,
            StatusCodes.Status400BadRequest);
    }

    private static int EstimateJsonElementCharacters(JsonElement? element)
    {
        if (element is null)
        {
            return 0;
        }

        var value = element.Value;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Length ?? 0
            : value.GetRawText().Length;
    }

    private static string CreateDigest(LocalModelDescriptor model)
    {
        var input = $"{model.RelativePath}|{model.SizeBytes}|{model.LastModifiedUtc:O}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
