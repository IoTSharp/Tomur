using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Tomur.Api.Models;
using Tomur.Api.Ollama;
using Tomur.Api.OpenAI;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Models;
using Tomur.Multimodal;
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

        app.MapPost("/api/runtime/session/unload", static async (
            HttpContext context,
            LocalInferenceService inferenceService,
            RuntimeDiagnosticsProvider diagnosticsProvider) =>
        {
            inferenceService.Unload();
            var response = diagnosticsProvider.GetRuntimeStatus();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.RuntimeStatusResponse);
        });

        app.MapGet("/api/runtime/native", static async (HttpContext context, RuntimeDiagnosticsProvider diagnosticsProvider) =>
        {
            var response = diagnosticsProvider.GetRuntimeStatus().NativeBundle;
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.NativeBundleProbeResult);
        });

        app.MapGet("/api/runtime/multimodal", static async (HttpContext context, MultimodalRuntimeService multimodalRuntime) =>
        {
            var response = multimodalRuntime.GetStatus();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.MultimodalRuntimeStatus);
        });

        app.MapGet("/api/models/catalog", static async (HttpContext context, DataPaths paths) =>
        {
            var response = CreateModelCatalogResponse(paths);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ModelCatalogResponse);
        });

        app.MapGet("/api/models/installed", static async (HttpContext context, DataPaths paths, LocalModelCatalog localModelCatalog) =>
        {
            var response = CreateInstalledModelsResponse(paths, localModelCatalog);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.InstalledModelsResponse);
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
                    "POST /api/runtime/session/unload",
                    "/api/runtime/native",
                    "/api/runtime/multimodal",
                    "/api/models/catalog",
                    "/api/models/installed",
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

    private static ModelCatalogResponse CreateModelCatalogResponse(DataPaths paths)
    {
        var hardware = HardwareProfile.Detect();
        var catalog = new ModelCatalog();
        var manifest = new InstallManifestStore(paths).Read();
        var packages = catalog.GetAll()
            .Select(package =>
            {
                var installed = manifest.Packages.FirstOrDefault(item => string.Equals(item.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                return new ModelCatalogPackageResponse(
                    package.Id,
                    package.ModelKey,
                    package.DisplayName,
                    package.Description,
                    package.Segment,
                    package.Task,
                    package.Runtime,
                    package.Family,
                    package.Format,
                    package.Quantization,
                    package.License,
                    package.SizeBytes,
                    package.ParameterCount,
                    package.PrimaryFileName,
                    package.Recommended,
                    package.Optional,
                    package.Research,
                    installed is not null,
                    installed?.Status ?? "not-installed",
                    package.MinimumMemoryBytes,
                    package.HardwareTier,
                    package.LicenseNotice,
                    package.Tags,
                    package.Assets.Select(static asset => new ModelCatalogAssetResponse(
                        asset.RepositoryId,
                        asset.RelativePath,
                        asset.TargetRelativePath,
                        asset.ExpectedSha256,
                        asset.SourceKind.ToString())).ToArray(),
                    package.BundleAssets.Select(static asset => new ModelCatalogBundleAssetResponse(
                        asset.AssetKey,
                        asset.Role,
                        asset.IsRequired,
                        asset.RelativePath,
                        asset.FileName,
                        asset.Format,
                        asset.Quantization,
                        asset.License,
                        asset.SizeBytes,
                        asset.ExpectedSha256,
                        asset.Description)).ToArray());
            })
            .ToArray();

        return new ModelCatalogResponse(
            new ModelHardwareProfileResponse(
                hardware.OSDescription,
                hardware.ProcessArchitecture,
                hardware.ProcessorCount,
                hardware.TotalMemoryBytes,
                hardware.Tier,
                hardware.Recommendations),
            packages);
    }

    private static InstalledModelsResponse CreateInstalledModelsResponse(DataPaths paths, LocalModelCatalog localModelCatalog)
    {
        var manifest = new InstallManifestStore(paths).Read();
        return new InstalledModelsResponse(
            paths.ModelsDirectory,
            manifest.Packages.Select(static package => new InstalledModelPackageResponse(
                package.Id,
                package.ModelKey,
                package.DisplayName,
                package.Segment,
                package.Directory,
                package.PrimaryPath,
                package.Status,
                package.License,
                package.LicenseNotice,
                package.InstalledAtUtc,
                package.UpdatedAtUtc,
                package.Assets.Select(static asset => new InstalledModelAssetResponse(
                    asset.Path,
                    asset.SourceRepositoryId,
                    asset.SourceRelativePath,
                    asset.ExpectedSha256,
                    asset.ActualSha256,
                    asset.Sha256Verified,
                    asset.SizeBytes)).ToArray())).ToArray(),
            localModelCatalog.ListModels().Select(static model => new VisibleModelResponse(
                model.Id,
                model.Name,
                model.PackageId,
                model.RelativePath,
                model.SizeBytes,
                model.Format,
                model.Family,
                model.QuantizationLevel,
                model.Capabilities,
                model.IsVerified)).ToArray());
    }

    private static async Task HandleOpenAiChatCompletionsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
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

        var messages = request.Messages
            .Select(static message => new ChatTurn(message.Role ?? "user", ExtractOpenAiTextContent(message.Content)))
            .ToArray();
        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            request.Temperature,
            request.TopP,
            request.MaxTokens);

        try
        {
            var result = inferenceService.Chat(model, messages, options, context.RequestAborted);
            await WriteOpenAiChatCompletionSuccessAsync(context, model.Id, result, request.Stream == true);
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, exception), request.Stream == true);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)), request.Stream == true);
        }
    }

    private static async Task HandleOpenAiCompletionsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
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

        var prompt = ExtractOpenAiTextContent(request.Prompt);
        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            request.Temperature,
            request.TopP,
            request.MaxTokens);

        try
        {
            var result = inferenceService.Complete(model, prompt, options, context.RequestAborted);
            await WriteOpenAiCompletionSuccessAsync(context, model.Id, result, request.Stream == true);
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, exception), request.Stream == true);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)), request.Stream == true);
        }
    }

    private static async Task HandleOpenAiEmbeddingsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
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

        var inputs = ExtractEmbeddingInputs(request.Input);
        if (inputs.Count == 0)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The input field must contain at least one string.");
            return;
        }

        try
        {
            var data = new List<OpenAiEmbeddingData>(inputs.Count);
            var promptTokens = 0;
            for (var index = 0; index < inputs.Count; index++)
            {
                var result = inferenceService.Embed(model, inputs[index], CompletionOptions.Default, context.RequestAborted);
                promptTokens += result.Usage.PromptTokens;
                data.Add(new OpenAiEmbeddingData("embedding", result.Vector, index));
            }

            var response = new OpenAiEmbeddingResponse(
                "list",
                data,
                model.Id,
                new OpenAiUsage(promptTokens, 0, promptTokens));

            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.OpenAiEmbeddingResponse);
        }
        catch (InferenceException exception)
        {
            var response = OpenAiErrorResponse.RuntimeUnavailable(diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var response = OpenAiErrorResponse.RuntimeUnavailable(diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)));
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.OpenAiErrorResponse,
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleOpenAiImageGenerationsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalRuntimeService multimodalRuntime)
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

        var response = OpenAiErrorResponse.RuntimeUnavailable(
            diagnosticsProvider.GetMultimodalRuntimeUnavailable(model.Id, multimodalRuntime.GetBackendStatus("image-generation")));
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
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
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

        var prompt = BuildOllamaGeneratePrompt(request);
        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            null,
            null,
            null,
            request.Options);

        try
        {
            var result = inferenceService.Complete(model, prompt, options, context.RequestAborted);
            await WriteOllamaGenerateSuccessAsync(context, model.Id, result, stream);
        }
        catch (InferenceException exception)
        {
            await WriteOllamaRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, exception), stream);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOllamaRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)), stream);
        }
    }

    private static async Task HandleOllamaChatAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
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

        var messages = request.Messages
            .Select(static message => new ChatTurn(message.Role ?? "user", message.Content ?? string.Empty))
            .ToArray();
        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            null,
            null,
            null,
            request.Options);

        try
        {
            var result = inferenceService.Chat(model, messages, options, context.RequestAborted);
            await WriteOllamaChatSuccessAsync(context, model.Id, result, stream);
        }
        catch (InferenceException exception)
        {
            await WriteOllamaRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, exception), stream);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOllamaRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)), stream);
        }
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

    private static async Task WriteOpenAiChatCompletionSuccessAsync(
        HttpContext context,
        string model,
        CompletionResult result,
        bool stream)
    {
        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = ToOpenAiUsage(result.Usage);

        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";

            await WriteOpenAiChatChunkAsync(
                context,
                new OpenAiChatCompletionChunk(
                    id,
                    "chat.completion.chunk",
                    created,
                    model,
                    [new OpenAiChatCompletionChunkChoice(0, new OpenAiChatCompletionDelta("assistant", result.Text), null)],
                    null));

            await WriteOpenAiChatChunkAsync(
                context,
                new OpenAiChatCompletionChunk(
                    id,
                    "chat.completion.chunk",
                    created,
                    model,
                    [new OpenAiChatCompletionChunkChoice(0, new OpenAiChatCompletionDelta(null, null), "stop")],
                    usage));

            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            return;
        }

        var response = new OpenAiChatCompletionResponse(
            id,
            "chat.completion",
            created,
            model,
            [new OpenAiChatCompletionChoice(0, new OpenAiChatCompletionMessage("assistant", result.Text), "stop")],
            usage);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiChatCompletionResponse);
    }

    private static async Task WriteOpenAiCompletionSuccessAsync(
        HttpContext context,
        string model,
        CompletionResult result,
        bool stream)
    {
        var id = $"cmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = ToOpenAiUsage(result.Usage);

        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";

            await WriteOpenAiCompletionChunkAsync(
                context,
                new OpenAiCompletionChunk(
                    id,
                    "text_completion",
                    created,
                    model,
                    [new OpenAiCompletionChunkChoice(result.Text, 0, null)],
                    null));

            await WriteOpenAiCompletionChunkAsync(
                context,
                new OpenAiCompletionChunk(
                    id,
                    "text_completion",
                    created,
                    model,
                    [new OpenAiCompletionChunkChoice(string.Empty, 0, "stop")],
                    usage));

            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            return;
        }

        var response = new OpenAiCompletionResponse(
            id,
            "text_completion",
            created,
            model,
            [new OpenAiCompletionChoice(result.Text, 0, "stop")],
            usage);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiCompletionResponse);
    }

    private static async Task WriteOllamaGenerateSuccessAsync(
        HttpContext context,
        string model,
        CompletionResult result,
        bool stream)
    {
        var response = new OllamaGenerateResponse(
            model,
            DateTimeOffset.UtcNow,
            result.Text,
            true,
            null,
            ToNanoseconds(result.Elapsed),
            0,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens);

        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await System.Text.Json.JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                AppJsonSerializerContext.Default.OllamaGenerateResponse,
                context.RequestAborted);
            await context.Response.WriteAsync("\n", context.RequestAborted);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OllamaGenerateResponse);
    }

    private static async Task WriteOllamaChatSuccessAsync(
        HttpContext context,
        string model,
        CompletionResult result,
        bool stream)
    {
        var response = new OllamaChatResponse(
            model,
            DateTimeOffset.UtcNow,
            new OllamaChatMessage("assistant", result.Text),
            true,
            ToNanoseconds(result.Elapsed),
            0,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens);

        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await System.Text.Json.JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                AppJsonSerializerContext.Default.OllamaChatResponse,
                context.RequestAborted);
            await context.Response.WriteAsync("\n", context.RequestAborted);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OllamaChatResponse);
    }

    private static async Task WriteOpenAiChatChunkAsync(HttpContext context, OpenAiChatCompletionChunk chunk)
    {
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            chunk,
            AppJsonSerializerContext.Default.OpenAiChatCompletionChunk,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
    }

    private static async Task WriteOpenAiCompletionChunkAsync(HttpContext context, OpenAiCompletionChunk chunk)
    {
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            chunk,
            AppJsonSerializerContext.Default.OpenAiCompletionChunk,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
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

    private static bool IsNativeRuntimeException(Exception exception)
        => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static InferenceException CreateNativeRuntimeException(Exception exception)
    {
        return new InferenceException(
            "native_runtime_unavailable",
            $"The llama.cpp native runtime could not be used: {exception.Message}",
            [
                "Run tomur native prepare to extract or repair the managed runtime bundle.",
                "Run tomur doctor to inspect native runtime status."
            ],
            exception);
    }

    private static OpenAiUsage ToOpenAiUsage(TokenUsage usage)
        => new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);

    private static long ToNanoseconds(TimeSpan elapsed)
        => elapsed.Ticks * 100L;

    private static string BuildOllamaGeneratePrompt(OllamaGenerateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.System))
        {
            return request.Prompt ?? string.Empty;
        }

        return $"[SYSTEM]\n{request.System.Trim()}\n\n[USER]\n{request.Prompt?.Trim() ?? string.Empty}";
    }

    private static string ExtractOpenAiTextContent(JsonElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var value = element.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? string.Empty);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                    continue;
                }

                if (item.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    parts.Add(content.GetString() ?? string.Empty);
                }
            }

            return string.Join("\n", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
        }

        return value.GetRawText();
    }

    private static IReadOnlyList<string> ExtractEmbeddingInputs(JsonElement? input)
    {
        if (input is null)
        {
            return [];
        }

        var value = input.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return [value.GetString() ?? string.Empty];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [value.GetRawText()];
        }

        var inputs = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                inputs.Add(item.GetString() ?? string.Empty);
            }
        }

        return inputs;
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
