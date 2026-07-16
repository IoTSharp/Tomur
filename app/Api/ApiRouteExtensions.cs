using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tomur.Agents;
using Tomur.Api.Anthropic;
using Tomur.Api.Models;
using Tomur.Api.Ollama;
using Tomur.Api.OpenAI;
using Tomur.Config;
using Tomur.Conversations;
using Tomur.Diagnostics;
using Tomur.Hardware;
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
        app.Use(SuppressRequestAbortExceptions);

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

        app.MapGet("/api/agents/runtime", static async (HttpContext context, AgentRuntimeService agentRuntime) =>
        {
            var response = agentRuntime.GetStatus();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentRuntimeStatus);
        });

        app.MapGet("/api/agents/tools", static async (HttpContext context, AgentRuntimeService agentRuntime) =>
        {
            var response = agentRuntime.GetToolMap();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentToolMapResponse);
        });

        app.MapGet("/api/agents/tool-bindings", static async (HttpContext context, ToolFactory toolFactory) =>
        {
            var response = toolFactory.GetBindingStatus();
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentFrameworkToolBindingResponse);
        });

        app.MapGet("/api/agents/events", static async (HttpContext context, AgentEventLog eventLog) =>
        {
            int? limit = null;
            if (context.Request.Query.TryGetValue("limit", out var limitValues) &&
                int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
            {
                limit = parsedLimit;
            }

            var response = eventLog.ReadRecent(limit);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentEventLogRecentResponse);
        });

        app.MapGet("/api/agents/telemetry", static async (
            HttpContext context,
            AgentTelemetry telemetry,
            AgentEventLog eventLog) =>
        {
            var response = telemetry.GetStatus(eventLog.LogPath);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentTelemetryStatus);
        });

        app.MapPost("/api/agents/chat", HandleAgentChatAsync);
        app.MapPost("/api/agents/workflows/read-only", HandleAgentReadOnlyWorkflowAsync);
        app.MapPost("/api/agents/tools/invoke", HandleAgentToolInvokeAsync);

        app.MapGet("/api/logs/recent", static async (HttpContext context, LogBroadcastService logs) =>
        {
            int? limit = null;
            if (context.Request.Query.TryGetValue("limit", out var limitValues) &&
                int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
            {
                limit = parsedLimit;
            }

            var minLevel = TryParseLogLevel(context.Request.Query["level"].FirstOrDefault());
            var category = NormalizeLogCategory(context.Request.Query["category"].FirstOrDefault());

            var response = logs.GetRecent(limit, minLevel, category);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.LogRecentResponse);
        });

        app.MapGet("/api/logs/stream", static async (HttpContext context, LogBroadcastService logs) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var minLevel = TryParseLogLevel(context.Request.Query["level"].FirstOrDefault());
            var category = NormalizeLogCategory(context.Request.Query["category"].FirstOrDefault());

            using var subscription = logs.Subscribe(backlogLimit: 200, minLevel, category);

            foreach (var entry in subscription.Backlog)
            {
                await WriteLogEventAsync(context, entry);
            }

            var reader = subscription.Reader;
            while (!context.RequestAborted.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    if (!await reader.WaitToReadAsync(heartbeat.Token))
                    {
                        break;
                    }

                    while (reader.TryRead(out var entry))
                    {
                        await WriteLogEventAsync(context, entry);
                    }
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        app.MapDelete("/api/logs", static async (HttpContext context, LogBroadcastService logs) =>
        {
            var cleared = logs.Clear();
            await JsonHttpResponse.WriteAsync(
                context,
                new LogClearResponse("ok", cleared),
                AppJsonSerializerContext.Default.LogClearResponse);
        });

        app.MapGet("/api/conversations", static async (HttpContext context, ConversationStore conversations) =>
        {
            int? limit = null;
            if (context.Request.Query.TryGetValue("limit", out var limitValues) &&
                int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
            {
                limit = parsedLimit;
            }

            var response = conversations.List(limit);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationListResponse);
        });

        app.MapPost("/api/conversations", HandleConversationCreateAsync);
        app.MapGet("/api/conversations/{conversationId}", HandleConversationGetAsync);
        app.MapDelete("/api/conversations/{conversationId}", HandleConversationDeleteAsync);
        app.MapPost("/api/conversations/{conversationId}/turns", HandleConversationTurnAsync);
        app.MapPost("/api/conversations/{conversationId}/voice-turns", HandleConversationVoiceTurnAsync);
        app.MapPost("/api/conversations/{conversationId}/messages", HandleConversationAppendMessageAsync);
        app.MapPost("/api/conversations/{conversationId}/artifacts", HandleConversationRegisterArtifactAsync);
        app.MapGet("/api/conversations/{conversationId}/artifacts/{artifactId}/content", HandleConversationArtifactContentAsync);
        app.MapPost("/api/conversations/{conversationId}/diagnostics", HandleConversationAppendDiagnosticAsync);

        app.MapGet("/api/models/catalog", static async (HttpContext context, DataPaths paths, HardwareAccelerationService accelerationService) =>
        {
            var response = CreateModelCatalogResponse(paths, accelerationService);
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
        app.MapPost("/v1/audio/transcriptions", HandleOpenAiAudioTranscriptionsAsync);
        app.MapPost("/v1/audio/speech", HandleOpenAiAudioSpeechAsync);
        app.MapPost("/v1/messages", HandleAnthropicMessagesAsync);
        app.MapPost("/v1/messages/count_tokens", HandleAnthropicCountTokensAsync);

        app.MapPost("/api/vision/analyze", HandleVisionAnalyzeAsync);
        app.MapPost("/api/ocr/analyze", HandleOcrAnalyzeAsync);

        app.MapGet("/api/tags", HandleOllamaTagsAsync);
        app.MapPost("/api/show", HandleOllamaShowAsync);
        app.MapPost("/api/generate", HandleOllamaGenerateAsync);
        app.MapPost("/api/chat", HandleOllamaChatAsync);

        app.MapGet("/api", (Func<HttpContext, Task>)(static async context =>
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
                    "/api/agents/runtime",
                    "/api/agents/tools",
                    "/api/agents/tool-bindings",
                    "/api/agents/events",
                    "/api/agents/telemetry",
                    "POST /api/agents/chat",
                    "POST /api/agents/workflows/read-only",
                    "POST /api/agents/tools/invoke",
                    "/api/conversations",
                    "POST /api/conversations",
                    "/api/conversations/{conversationId}",
                    "DELETE /api/conversations/{conversationId}",
                    "POST /api/conversations/{conversationId}/turns",
                    "POST /api/conversations/{conversationId}/voice-turns",
                    "POST /api/conversations/{conversationId}/messages",
                    "POST /api/conversations/{conversationId}/artifacts",
                    "/api/conversations/{conversationId}/artifacts/{artifactId}/content",
                    "POST /api/conversations/{conversationId}/diagnostics",
                    "/api/models/catalog",
                    "/api/models/installed",
                    "POST /api/runtime/native/prepare",
                    "/api/runtime/native/{componentId}/{libraryName}",
                    "POST /api/runtime/native/{componentId}/{libraryName}/load",
                    "/v1/models",
                    "POST /v1/chat/completions",
                    "POST /v1/completions",
                    "POST /v1/embeddings",
                    "/v1/images/generations",
                    "/v1/audio/transcriptions",
                    "/v1/audio/speech",
                    "/v1/messages",
                    "/v1/messages/count_tokens",
                    "/api/vision/analyze",
                    "/api/ocr/analyze",
                    "/api/tags",
                    "POST /api/show",
                    "POST /api/generate",
                    "POST /api/chat"
                ]);

            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.RootResponse);
        }));
    }

    private static async Task HandleOpenAiModelsAsync(HttpContext context, LocalModelCatalog modelCatalog)
    {
        if (IsAnthropicModelListRequest(context))
        {
            var anthropicResponse = CreateAnthropicModelListResponse(modelCatalog);
            await JsonHttpResponse.WriteAsync(
                context,
                anthropicResponse,
                AppJsonSerializerContext.Default.AnthropicModelListResponse);
            return;
        }

        var models = modelCatalog
            .ListModels()
            .Select(static model => new OpenAiModelResponse(
                model.Id,
                new DateTimeOffset(model.LastModifiedUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
                "local",
                model.Family,
                model.Format,
                model.QuantizationLevel,
                model.Capabilities))
            .ToArray();

        var response = new OpenAiModelListResponse(models);
        await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.OpenAiModelListResponse);
    }

    private static async Task HandleAnthropicMessagesAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        LocalInferenceService inferenceService)
    {
        var request = await ReadAnthropicRequestAsync(
            context,
            AppJsonSerializerContext.Default.AnthropicMessageRequest);
        if (request is null)
        {
            return;
        }

        var stream = request.Stream == true;
        var model = await RequireAnthropicModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream);
        if (model is null)
        {
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            await WriteAnthropicInvalidRequestAsync(
                context,
                "The messages field must contain at least one message.",
                stream);
            return;
        }

        var inputCharacters = EstimateAnthropicInputCharacters(request);
        if (!await RequireAnthropicInputWithinLimitAsync(context, diagnosticsProvider, request.Model, inputCharacters, stream))
        {
            return;
        }

        var messages = CreateAnthropicChatTurns(request);
        if (messages.Count == 0)
        {
            await WriteAnthropicInvalidRequestAsync(
                context,
                "At least one text message is required for the Claude Code compatibility endpoint.",
                stream);
            return;
        }

        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            request.Temperature,
            request.TopP,
            request.MaxTokens,
            stopSequences: request.StopSequences);
        var responseModel = string.IsNullOrWhiteSpace(request.Model)
            ? ToClaudeCodeModelAlias(model)
            : request.Model.Trim();
        var inputTokenEstimate = EstimateTokenCount(string.Join("\n", messages.Select(static message => message.Content)));

        try
        {
            if (stream)
            {
                await WriteAnthropicMessageStreamAsync(
                    context,
                    responseModel,
                    inputTokenEstimate,
                    onGenerate: emit => inferenceService.Chat(model, messages, options, context.RequestAborted, emit),
                    onInferenceError: exception => diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
                return;
            }

            var result = inferenceService.Chat(model, messages, options, context.RequestAborted);
            await WriteAnthropicMessageSuccessAsync(context, responseModel, result);
        }
        catch (InferenceException exception)
        {
            await WriteAnthropicRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, exception),
                stream);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteAnthropicRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)),
                stream);
        }
    }

    private static async Task HandleAnthropicCountTokensAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog)
    {
        var request = await ReadAnthropicRequestAsync(
            context,
            AppJsonSerializerContext.Default.AnthropicMessageRequest);
        if (request is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.Model) &&
            FindProtocolModel(modelCatalog, request.Model) is null)
        {
            await JsonHttpResponse.WriteAsync(
                context,
                AnthropicErrorResponse.ModelNotFound(diagnosticsProvider.GetModelNotDownloaded(request.Model)),
                AppJsonSerializerContext.Default.AnthropicErrorResponse,
                StatusCodes.Status404NotFound);
            return;
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            await WriteAnthropicInvalidRequestAsync(
                context,
                "The messages field must contain at least one message.");
            return;
        }

        var messages = CreateAnthropicChatTurns(request);
        var text = string.Join("\n", messages.Select(static message => message.Content));
        var response = new AnthropicTokenCountResponse(EstimateTokenCount(text));
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.AnthropicTokenCountResponse);
    }

    private static async Task HandleAgentChatAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        AgentRuntimeService agentRuntime,
        ToolInvoker toolInvoker,
        AgentEventLog eventLog,
        AgentTelemetry telemetry)
    {
        var request = await ReadAgentRequestAsync(
            context,
            AppJsonSerializerContext.Default.AgentChatRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = await agentRuntime.RunChatAsync(request, toolInvoker, context.RequestAborted);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.AgentChatResponse);
        }
        catch (InferenceException exception) when (IsInvalidRequestInferenceException(exception))
        {
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, exception);
            await eventLog.WriteErrorAsync(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                StatusCodes.Status400BadRequest);
        }
        catch (InferenceException exception)
        {
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, exception);
            await eventLog.WriteErrorAsync(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var runtimeException = CreateNativeRuntimeException(exception);
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, runtimeException);
            await eventLog.WriteErrorAsync(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "agent_chat",
                request.ToolMode,
                null,
                "Microsoft.Agents.AI.ChatClientAgent",
                request.Model,
                diagnostic,
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleAgentReadOnlyWorkflowAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        AgentRuntimeService agentRuntime,
        ToolInvoker toolInvoker,
        AgentEventLog eventLog,
        AgentTelemetry telemetry)
    {
        var request = await ReadAgentRequestAsync(
            context,
            AppJsonSerializerContext.Default.AgentReadOnlyWorkflowRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = await agentRuntime.RunReadOnlyWorkflowAsync(request, toolInvoker, context.RequestAborted);
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.AgentReadOnlyWorkflowResponse);
        }
        catch (InferenceException exception) when (IsInvalidRequestInferenceException(exception))
        {
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, exception);
            await eventLog.WriteErrorAsync(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                StatusCodes.Status400BadRequest);
        }
        catch (InferenceException exception)
        {
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, exception);
            await eventLog.WriteErrorAsync(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var runtimeException = CreateNativeRuntimeException(exception);
            var diagnostic = diagnosticsProvider.GetRuntimeFailure(request.Model, runtimeException);
            await eventLog.WriteErrorAsync(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "read_only_workflow",
                "workflow",
                null,
                "Microsoft.Agents.AI.Workflows",
                request.Model,
                diagnostic,
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleAgentToolInvokeAsync(
        HttpContext context,
        ToolInvoker toolInvoker,
        AgentEventLog eventLog,
        AgentTelemetry telemetry)
    {
        var request = await ReadAgentRequestAsync(
            context,
            AppJsonSerializerContext.Default.AgentToolInvokeRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = await toolInvoker.InvokeAsync(request, context.RequestAborted);
            var statusCode = response.Status switch
            {
                "blocked" => StatusCodes.Status409Conflict,
                "error" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status200OK
            };
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.AgentToolInvokeResponse,
                statusCode);
        }
        catch (InferenceException exception)
        {
            var diagnostic = new RuntimeDiagnostic(
                "error",
                exception.Code,
                exception.Message,
                request.Tool,
                exception.Actions);
            await eventLog.WriteErrorAsync(
                "tool_invocation",
                "manual",
                request.Tool,
                "Microsoft.Extensions.AI.AITool",
                null,
                diagnostic,
                context.RequestAborted);
            telemetry.RecordError(
                "tool_invocation",
                "manual",
                request.Tool,
                "Microsoft.Extensions.AI.AITool",
                null,
                diagnostic);
            await WriteAgentErrorAsync(
                context,
                "tool_invocation",
                "manual",
                request.Tool,
                "Microsoft.Extensions.AI.AITool",
                null,
                diagnostic,
                StatusCodes.Status400BadRequest);
        }
    }

    private static async Task HandleConversationCreateAsync(
        HttpContext context,
        ConversationStore conversations)
    {
        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationCreateRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var conversation = conversations.Create(request);
            var response = new ConversationCreateResponse("ok", conversation);
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.ConversationCreateResponse,
                StatusCodes.Status201Created);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationGetAsync(
        HttpContext context,
        ConversationStore conversations,
        string conversationId)
    {
        int? limit = null;
        if (context.Request.Query.TryGetValue("limit", out var limitValues) &&
            int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
        {
            limit = parsedLimit;
        }

        try
        {
            var response = conversations.Get(conversationId, limit);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationDetailResponse);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationDeleteAsync(
        HttpContext context,
        ConversationStore conversations,
        string conversationId)
    {
        try
        {
            var response = conversations.Delete(conversationId);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationDeleteResponse);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationAppendMessageAsync(
        HttpContext context,
        ConversationStore conversations,
        string conversationId)
    {
        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationAppendMessageRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = conversations.AppendMessage(conversationId, request);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationAppendMessageResponse);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationTurnAsync(
        HttpContext context,
        ConversationOrchestrationService orchestration,
        string conversationId)
    {
        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationTurnRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var result = await orchestration.RunTurnAsync(
                conversationId,
                request,
                context.RequestAborted);
            await JsonHttpResponse.WriteAsync(
                context,
                result.Response,
                AppJsonSerializerContext.Default.ConversationTurnResponse,
                result.StatusCode);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationVoiceTurnAsync(
        HttpContext context,
        ConversationOrchestrationService orchestration,
        string conversationId)
    {
        var parsed = await ReadConversationVoiceTurnAsync(context);
        if (parsed is null)
        {
            return;
        }

        if (parsed.AudioBytes.LongLength > CompatibilityProtocolLimits.MaxAudioBytes)
        {
            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    $"The audio input is too large. Limit: {CompatibilityProtocolLimits.MaxAudioBytes} bytes.",
                    ["Send a shorter PCM16 WAV recording."]));
            return;
        }

        try
        {
            var result = await orchestration.RunVoiceTurnAsync(
                conversationId,
                parsed.Request,
                parsed.AudioBytes,
                parsed.MediaType,
                parsed.FileName,
                context.RequestAborted);
            await JsonHttpResponse.WriteAsync(
                context,
                result.Response,
                AppJsonSerializerContext.Default.ConversationVoiceTurnResponse,
                result.StatusCode);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationRegisterArtifactAsync(
        HttpContext context,
        ConversationStore conversations,
        string conversationId)
    {
        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationRegisterArtifactRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = conversations.RegisterArtifact(conversationId, request);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationRegisterArtifactResponse);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationArtifactContentAsync(
        HttpContext context,
        ConversationStore conversations,
        DataPaths paths,
        string conversationId,
        string artifactId)
    {
        try
        {
            var artifact = conversations.GetArtifact(conversationId, artifactId);
            if (string.IsNullOrWhiteSpace(artifact.Path))
            {
                await WriteConversationErrorAsync(
                    context,
                    new ConversationStoreException(
                        "not_found",
                        "artifact_content_not_available",
                        "The requested artifact does not have local file content.",
                        ["Inspect the artifact metadata on GET /api/conversations/{conversationId}."]));
                return;
            }

            var artifactPath = Path.GetFullPath(artifact.Path);
            var dataRoot = Path.GetFullPath(paths.DataDirectory);
            if (!IsPathWithinRoot(artifactPath, dataRoot))
            {
                await WriteConversationErrorAsync(
                    context,
                    new ConversationStoreException(
                        "error",
                        "artifact_path_not_allowed",
                        "Conversation artifacts can be served only from the Tomur data directory.",
                        ["Register artifacts under the Tomur data directory before requesting content."]));
                return;
            }

            if (!File.Exists(artifactPath))
            {
                await WriteConversationErrorAsync(
                    context,
                    new ConversationStoreException(
                        "not_found",
                        "artifact_file_not_found",
                        "The artifact metadata exists, but the local file is missing.",
                        ["Regenerate the artifact or inspect the Tomur data directory."]));
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = string.IsNullOrWhiteSpace(artifact.MediaType)
                ? "application/octet-stream"
                : artifact.MediaType;
            await context.Response.SendFileAsync(artifactPath, context.RequestAborted);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static async Task HandleConversationAppendDiagnosticAsync(
        HttpContext context,
        ConversationStore conversations,
        string conversationId)
    {
        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationAppendDiagnosticRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            var response = conversations.AppendDiagnostic(conversationId, request);
            await JsonHttpResponse.WriteAsync(context, response, AppJsonSerializerContext.Default.ConversationAppendDiagnosticResponse);
        }
        catch (ConversationStoreException exception)
        {
            await WriteConversationErrorAsync(context, exception);
        }
    }

    private static ModelCatalogResponse CreateModelCatalogResponse(DataPaths paths, HardwareAccelerationService accelerationService)
    {
        var hardware = HardwareProfile.Detect();
        var acceleration = accelerationService.GetProfile();
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
                hardware.Recommendations,
                acceleration),
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
        LocalInferenceService inferenceService,
        MultimodalExecutionService multimodalExecution)
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
            await WriteOpenAiInvalidRequestAsync(
                context,
                "The messages field must contain at least one message.",
                request.Stream == true);
            return;
        }

        if (ContainsOpenAiImageContent(request.Messages))
        {
            if (!ModelHasCapability(model, "vision"))
            {
                var diagnostic = multimodalExecution.CreateCapabilityMismatchDiagnostic(
                    model.Id,
                    "vision",
                    "/v1/chat/completions");
                await WriteOpenAiDiagnosticAsync(context, diagnostic, StatusCodes.Status400BadRequest, request.Stream == true);
                return;
            }

            if (!TryCreateOpenAiVisionInput(request.Messages, out var prompt, out var images, out var imageError))
            {
                await WriteOpenAiInvalidRequestAsync(context, imageError, request.Stream == true);
                return;
            }

            var visionOptions = LocalInferenceService.MergeOptions(
                CompletionOptions.Default,
                request.Temperature,
                request.TopP,
                request.MaxTokens);

            try
            {
                var result = multimodalExecution.AnalyzeVision(model, prompt, images, visionOptions, context.RequestAborted);
                var completionResult = new CompletionResult(
                    result.Text,
                    EstimateVisionUsage(prompt, result.Text),
                    result.Elapsed,
                    result.Diagnostics);
                await WriteOpenAiChatCompletionSuccessAsync(context, model.Id, completionResult, request.Stream == true);
            }
            catch (InferenceException exception)
            {
                await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, exception), request.Stream == true);
            }
            catch (Exception exception) when (IsNativeRuntimeException(exception))
            {
                await WriteOpenAiRuntimeUnavailableAsync(context, diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)), request.Stream == true);
            }

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
            if (request.Stream == true)
            {
                await WriteOpenAiChatCompletionStreamAsync(
                    context,
                    model.Id,
                    onGenerate: emit => inferenceService.Chat(model, messages, options, context.RequestAborted, emit),
                    onInferenceError: exception => diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
                return;
            }

            var result = inferenceService.Chat(model, messages, options, context.RequestAborted);
            await WriteOpenAiChatCompletionSuccessAsync(context, model.Id, result, stream: false);
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
            if (request.Stream == true)
            {
                await WriteOpenAiCompletionStreamAsync(
                    context,
                    model.Id,
                    onGenerate: emit => inferenceService.Complete(model, prompt, options, context.RequestAborted, emit),
                    onInferenceError: exception => diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
                return;
            }

            var result = inferenceService.Complete(model, prompt, options, context.RequestAborted);
            await WriteOpenAiCompletionSuccessAsync(context, model.Id, result, stream: false);
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
        MultimodalExecutionService multimodalExecution,
        IsolatedImageGenerationService isolatedImageGeneration)
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

        if (!ModelHasCapability(model, "image"))
        {
            var diagnostic = multimodalExecution.CreateCapabilityMismatchDiagnostic(
                model.Id,
                "image",
                "/v1/images/generations");
            await WriteOpenAiDiagnosticAsync(context, diagnostic, StatusCodes.Status400BadRequest);
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

        if (request.Count is not null && request.Count.Value != 1)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The n field currently supports only 1.");
            return;
        }

        if (!TryParseImageSize(request.Size, out var width, out var height, out var sizeError))
        {
            await WriteOpenAiInvalidRequestAsync(context, sizeError);
            return;
        }

        var responseFormat = NormalizeImageResponseFormat(request.ResponseFormat);
        if (responseFormat is null)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The response_format field must be 'url' or 'b64_json'.");
            return;
        }

        var imageDefaults = ResolveImageGenerationDefaults(model);
        var options = new ImageGenerationOptions(
            request.Prompt.Trim(),
            request.NegativePrompt,
            width,
            height,
            Math.Clamp(request.Steps ?? imageDefaults.Steps, 1, 100),
            Math.Clamp(ToFloat(request.CfgScale, imageDefaults.CfgScale), 1.0f, 20.0f),
            request.Seed ?? -1,
            ToOptionalFloat(request.DistilledGuidance),
            ToOptionalFloat(request.FlowShift),
            request.SampleMethod ?? imageDefaults.SampleMethod,
            request.Scheduler);

        try
        {
            var result = await isolatedImageGeneration.GenerateImageAsync(model, options, context.RequestAborted);
            var imageBase64 = Convert.ToBase64String(result.Bytes);
            var mimeType = ResolveImageMimeType(result.Format);
            var data = responseFormat == "b64_json"
                ? new OpenAiImageGenerationData(null, imageBase64, request.Prompt.Trim())
                : new OpenAiImageGenerationData($"data:{mimeType};base64,{imageBase64}", null, request.Prompt.Trim());
            var response = new OpenAiImageGenerationResponse(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                [data]);
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.OpenAiImageGenerationResponse);
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

    private static async Task HandleOpenAiAudioTranscriptionsAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution)
    {
        if (!context.Request.HasFormContentType)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The request must use multipart/form-data.");
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var requestedModel = form["model"].FirstOrDefault();
        var model = await RequireOpenAiModelAsync(context, requestedModel, diagnosticsProvider, modelCatalog, stream: false);
        if (model is null)
        {
            return;
        }

        if (!ModelHasCapability(model, "audio"))
        {
            var diagnostic = multimodalExecution.CreateCapabilityMismatchDiagnostic(
                model.Id,
                "audio",
                "/v1/audio/transcriptions");
            await WriteOpenAiDiagnosticAsync(context, diagnostic, StatusCodes.Status400BadRequest);
            return;
        }

        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The file field is required and must contain audio bytes.");
            return;
        }

        if (file.Length > CompatibilityProtocolLimits.MaxAudioBytes)
        {
            await WriteOpenAiInvalidRequestAsync(context, $"The audio file is too large. Limit: {CompatibilityProtocolLimits.MaxAudioBytes} bytes.");
            return;
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(memory, context.RequestAborted);

        try
        {
            var result = multimodalExecution.TranscribeAudio(
                model,
                memory.ToArray(),
                form["language"].FirstOrDefault(),
                context.RequestAborted);
            var response = new OpenAiAudioTranscriptionResponse(result.Text);
            await JsonHttpResponse.WriteAsync(
                context,
                response,
                AppJsonSerializerContext.Default.OpenAiAudioTranscriptionResponse);
        }
        catch (InferenceException exception) when (IsInvalidRequestInferenceException(exception))
        {
            await WriteOpenAiInvalidRequestAsync(context, exception.Message);
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, exception),
                stream: false);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)),
                stream: false);
        }
    }

    private static async Task HandleOpenAiAudioSpeechAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution)
    {
        var request = await ReadOpenAiRequestAsync(
            context,
            AppJsonSerializerContext.Default.OpenAiAudioSpeechRequest);
        if (request is null)
        {
            return;
        }

        var model = await RequireOpenAiModelAsync(context, request.Model, diagnosticsProvider, modelCatalog, stream: false);
        if (model is null)
        {
            return;
        }

        if (!ModelHasCapability(model, "audio-output"))
        {
            var diagnostic = multimodalExecution.CreateCapabilityMismatchDiagnostic(
                model.Id,
                "audio-output",
                "/v1/audio/speech");
            await WriteOpenAiDiagnosticAsync(context, diagnostic, StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            await WriteOpenAiInvalidRequestAsync(context, "The input field is required.");
            return;
        }

        if (!await RequireOpenAiInputWithinLimitAsync(context, diagnosticsProvider, request.Model, request.Input.Length, stream: false))
        {
            return;
        }

        var responseFormat = NormalizeSpeechResponseFormat(request.ResponseFormat);
        if (responseFormat is null)
        {
            await WriteOpenAiInvalidRequestAsync(context, "The response_format field currently supports only 'wav'.");
            return;
        }

        var options = new SpeechSynthesisOptions(
            request.Input.Trim(),
            request.Voice,
            responseFormat,
            Math.Clamp(request.Speed ?? 1.0, 0.25, 4.0),
            request.Language);

        try
        {
            var result = multimodalExecution.SynthesizeSpeech(model, options, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = result.MediaType;
            context.Response.Headers.ContentLength = result.Bytes.Length;
            await context.Response.Body.WriteAsync(result.Bytes, context.RequestAborted);
        }
        catch (InferenceException exception) when (IsInvalidRequestInferenceException(exception))
        {
            await WriteOpenAiInvalidRequestAsync(context, exception.Message);
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, exception),
                stream: false);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiRuntimeUnavailableAsync(
                context,
                diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)),
                stream: false);
        }
    }

    private static async Task HandleVisionAnalyzeAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution)
    {
        var request = await ReadMultimodalRequestAsync(
            context,
            AppJsonSerializerContext.Default.VisionAnalysisRequest,
            "/api/vision/analyze",
            "vlm",
            multimodalExecution);
        if (request is null)
        {
            return;
        }

        var inputSummary = new MultimodalInputSummary(
            request.Prompt?.Length ?? 0,
            request.Images?.Count ?? 0,
            null,
            null,
            null);
        var model = await ResolveLocalEndpointModelAsync(
            context,
            diagnosticsProvider,
            modelCatalog,
            multimodalExecution,
            request.Model,
            requiredCapability: "vision",
            route: "/api/vision/analyze",
            backendId: "vlm",
            inputSummary);
        if (model is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    request.Model,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/vision/analyze", "The prompt field is required.", request.Model),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Images is null || request.Images.Count == 0)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    model.Id,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/vision/analyze", "At least one image is required.", model.Id),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Images.Count > CompatibilityProtocolLimits.MaxImageCount)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    model.Id,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/vision/analyze", $"Too many images. Limit: {CompatibilityProtocolLimits.MaxImageCount}.", model.Id),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryCreateImageInputs(request.Images, out var images, out var imageError))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    model.Id,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/vision/analyze", imageError, model.Id),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        var options = LocalInferenceService.MergeOptions(
            CompletionOptions.Default,
            request.Temperature,
            null,
            request.MaxTokens);

        try
        {
            var result = multimodalExecution.AnalyzeVision(model, request.Prompt, images, options, context.RequestAborted);
            await WriteMultimodalTextAsync(
                context,
                multimodalExecution.CreateTextResponse("/api/vision/analyze", "vlm", model.Id, result, inputSummary));
        }
        catch (InferenceException exception)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    model.Id,
                    diagnosticsProvider.GetRuntimeFailure(model.Id, exception),
                    inputSummary),
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/vision/analyze",
                    "vlm",
                    model.Id,
                    diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)),
                    inputSummary),
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task HandleOcrAnalyzeAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution)
    {
        var request = await ReadMultimodalRequestAsync(
            context,
            AppJsonSerializerContext.Default.OcrAnalysisRequest,
            "/api/ocr/analyze",
            "ocr",
            multimodalExecution);
        if (request is null)
        {
            return;
        }

        var inputSummary = new MultimodalInputSummary(
            request.Prompt?.Length ?? 0,
            request.Image is null ? 0 : 1,
            null,
            null,
            null);

        LocalModelDescriptor? model = null;
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            model = await ResolveLocalEndpointModelAsync(
                context,
                diagnosticsProvider,
                modelCatalog,
                multimodalExecution,
                request.Model,
                requiredCapability: "ocr",
                route: "/api/ocr/analyze",
                backendId: "ocr",
                inputSummary);
            if (model is null)
            {
                return;
            }
        }

        if (request.Image is null)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/ocr/analyze",
                    "ocr",
                    model?.Id ?? request.Model,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/ocr/analyze", "The image field is required.", model?.Id ?? request.Model),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        if (model is null)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/ocr/analyze",
                    "ocr",
                    request.Model,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/ocr/analyze", "The model field is required for executable OCR requests.", request.Model),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryCreateImageInput(request.Image, out var image, out var imageError))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/ocr/analyze",
                    "ocr",
                    model.Id,
                    MultimodalExecutionService.CreateInvalidRequestDiagnostic("/api/ocr/analyze", imageError, model.Id),
                    inputSummary),
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var result = multimodalExecution.AnalyzeOcr(
                model,
                image,
                request.Prompt,
                request.Language,
                CompletionOptions.Default,
                context.RequestAborted);
            await WriteMultimodalTextAsync(
                context,
                multimodalExecution.CreateTextResponse("/api/ocr/analyze", "ocr", model.Id, result, inputSummary));
        }
        catch (InferenceException exception)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/ocr/analyze",
                    "ocr",
                    model.Id,
                    diagnosticsProvider.GetRuntimeFailure(model.Id, exception),
                    inputSummary),
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    "/api/ocr/analyze",
                    "ocr",
                    model.Id,
                    diagnosticsProvider.GetRuntimeFailure(model.Id, CreateNativeRuntimeException(exception)),
                    inputSummary),
                StatusCodes.Status503ServiceUnavailable);
        }
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
            if (stream)
            {
                await WriteOllamaGenerateStreamAsync(
                    context,
                    model.Id,
                    onGenerate: emit => inferenceService.Complete(
                        model,
                        prompt,
                        options,
                        context.RequestAborted,
                        emit),
                    onInferenceError: exception => diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
                return;
            }

            var result = inferenceService.Complete(model, prompt, options, context.RequestAborted);
            await WriteOllamaGenerateSuccessAsync(context, model.Id, result, stream: false);
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
            if (stream)
            {
                await WriteOllamaChatStreamAsync(
                    context,
                    model.Id,
                    onGenerate: emit => inferenceService.Chat(
                        model,
                        messages,
                        options,
                        context.RequestAborted,
                        emit),
                    onInferenceError: exception => diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
                return;
            }

            var result = inferenceService.Chat(model, messages, options, context.RequestAborted);
            await WriteOllamaChatSuccessAsync(context, model.Id, result, stream: false);
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

    private static async Task<T?> ReadAnthropicRequestAsync<T>(
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

            await WriteAnthropicInvalidRequestAsync(context, "Request body is required.");
            return default;
        }
        catch (JsonException exception)
        {
            await WriteAnthropicInvalidRequestAsync(context, $"Invalid JSON request body: {exception.Message}");
            return default;
        }
    }

    private static async Task<T?> ReadAgentRequestAsync<T>(
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

            await WriteAgentDiagnosticAsync(
                context,
                new RuntimeDiagnostic(
                    "error",
                    "invalid_request",
                    "Request body is required.",
                    null,
                    ["Provide a JSON request body."]),
                StatusCodes.Status400BadRequest);
            return default;
        }
        catch (JsonException exception)
        {
            await WriteAgentDiagnosticAsync(
                context,
                new RuntimeDiagnostic(
                    "error",
                    "invalid_request",
                    $"Invalid JSON request body: {exception.Message}",
                    null,
                    ["Provide valid JSON."]),
                StatusCodes.Status400BadRequest);
            return default;
        }
    }

    private static async Task<T?> ReadConversationRequestAsync<T>(
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

            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    "Request body is required.",
                    ["Provide a JSON request body."]));
            return default;
        }
        catch (JsonException exception)
        {
            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    $"Invalid JSON request body: {exception.Message}",
                    ["Provide valid JSON."]));
            return default;
        }
    }

    private static async Task<ParsedVoiceTurnRequest?> ReadConversationVoiceTurnAsync(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            return await ReadConversationVoiceTurnFormAsync(context);
        }

        var request = await ReadConversationRequestAsync(
            context,
            AppJsonSerializerContext.Default.ConversationVoiceTurnRequest);
        if (request is null)
        {
            return null;
        }

        if (!TryCreateAudioInput(
                request.AudioDataUri,
                request.AudioBase64,
                request.AudioMediaType,
                request.AudioName,
                out var audioBytes,
                out var mediaType,
                out var error))
        {
            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    error,
                    ["Send audio_data_uri or audio_base64. PCM16 WAV is required by the current ASR adapter."]));
            return null;
        }

        return new ParsedVoiceTurnRequest(request, audioBytes, mediaType, request.AudioName);
    }

    private static async Task<ParsedVoiceTurnRequest?> ReadConversationVoiceTurnFormAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    "The file field is required and must contain audio bytes.",
                    ["Send multipart/form-data with a non-empty file field."]));
            return null;
        }

        if (file.Length > CompatibilityProtocolLimits.MaxAudioBytes)
        {
            await WriteConversationErrorAsync(
                context,
                new ConversationStoreException(
                    "error",
                    "invalid_request",
                    $"The audio file is too large. Limit: {CompatibilityProtocolLimits.MaxAudioBytes} bytes.",
                    ["Send a shorter PCM16 WAV recording."]));
            return null;
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(memory, context.RequestAborted);

        var request = new ConversationVoiceTurnRequest(
            null,
            null,
            FirstNonEmpty(ReadFormString(form, "audio_media_type"), file.ContentType),
            FirstNonEmpty(ReadFormString(form, "audio_name"), file.FileName),
            ReadFormString(form, "language"),
            ReadFormString(form, "asr_model"),
            ReadFormString(form, "model"),
            ReadFormString(form, "tts_model"),
            ReadFormBool(form, "speak"),
            ReadFormString(form, "voice"),
            ReadFormString(form, "response_format"),
            ReadFormDouble(form, "speed"),
            ReadFormString(form, "tool_mode"),
            null,
            ReadFormInt(form, "max_tool_rounds"),
            ReadFormString(form, "instructions"),
            ReadFormInt(form, "max_tokens"),
            ReadFormDouble(form, "temperature"),
            ReadFormDouble(form, "top_p"),
            ReadFormInt(form, "history_limit"),
            null);

        return new ParsedVoiceTurnRequest(
            request,
            memory.ToArray(),
            request.AudioMediaType,
            request.AudioName);
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

    private static async Task<T?> ReadMultimodalRequestAsync<T>(
        HttpContext context,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string route,
        string backendId,
        MultimodalExecutionService multimodalExecution)
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

            var diagnostic = MultimodalExecutionService.CreateInvalidRequestDiagnostic(route, "Request body is required.");
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(route, backendId, null, diagnostic, EmptyInputSummary()),
                StatusCodes.Status400BadRequest);
            return default;
        }
        catch (JsonException exception)
        {
            var diagnostic = MultimodalExecutionService.CreateInvalidRequestDiagnostic(
                route,
                $"Invalid JSON request body: {exception.Message}");
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(route, backendId, null, diagnostic, EmptyInputSummary()),
                StatusCodes.Status400BadRequest);
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
            await WriteOpenAiInvalidRequestAsync(context, "The model field is required.", stream);
            return null;
        }

        var resolvedModel = FindProtocolModel(modelCatalog, requestedModel);
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

    private static async Task<LocalModelDescriptor?> RequireAnthropicModelAsync(
        HttpContext context,
        string? requestedModel,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        bool stream)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            await WriteAnthropicInvalidRequestAsync(context, "The model field is required.", stream);
            return null;
        }

        var resolvedModel = FindProtocolModel(modelCatalog, requestedModel);
        if (resolvedModel is null)
        {
            var response = AnthropicErrorResponse.ModelNotFound(diagnosticsProvider.GetModelNotDownloaded(requestedModel));
            if (stream)
            {
                await WriteAnthropicStreamErrorAsync(context, response);
            }
            else
            {
                await JsonHttpResponse.WriteAsync(
                    context,
                    response,
                    AppJsonSerializerContext.Default.AnthropicErrorResponse,
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

    private static async Task<LocalModelDescriptor?> ResolveLocalEndpointModelAsync(
        HttpContext context,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution,
        string? requestedModel,
        string requiredCapability,
        string route,
        string backendId,
        MultimodalInputSummary inputSummary)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            var diagnostic = MultimodalExecutionService.CreateInvalidRequestDiagnostic(route, "The model field is required.");
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(route, backendId, null, diagnostic, inputSummary),
                StatusCodes.Status400BadRequest);
            return null;
        }

        var resolvedModel = modelCatalog.Find(requestedModel);
        if (resolvedModel is null)
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    route,
                    backendId,
                    requestedModel,
                    diagnosticsProvider.GetModelNotDownloaded(requestedModel),
                    inputSummary),
                StatusCodes.Status404NotFound);
            return null;
        }

        if (!ModelHasCapability(resolvedModel, requiredCapability))
        {
            await WriteMultimodalDiagnosticAsync(
                context,
                multimodalExecution.CreateDiagnosticResponse(
                    route,
                    backendId,
                    resolvedModel.Id,
                    multimodalExecution.CreateCapabilityMismatchDiagnostic(resolvedModel.Id, requiredCapability, route),
                    inputSummary),
                StatusCodes.Status400BadRequest);
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

    private static async Task<bool> RequireAnthropicInputWithinLimitAsync(
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

        var error = AnthropicErrorResponse.ContextLengthExceeded(
            diagnosticsProvider.GetContextLengthExceeded(model, characterCount));
        if (stream)
        {
            await WriteAnthropicStreamErrorAsync(context, error);
        }
        else
        {
            await JsonHttpResponse.WriteAsync(
                context,
                error,
                AppJsonSerializerContext.Default.AnthropicErrorResponse,
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

    private static async Task WriteAnthropicRuntimeUnavailableAsync(
        HttpContext context,
        RuntimeDiagnostic diagnostic,
        bool stream)
    {
        var response = AnthropicErrorResponse.RuntimeUnavailable(diagnostic);
        if (stream)
        {
            await WriteAnthropicStreamErrorAsync(context, response);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.AnthropicErrorResponse,
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

    private static async Task WriteAgentDiagnosticAsync(
        HttpContext context,
        RuntimeDiagnostic diagnostic,
        int statusCode)
    {
        await JsonHttpResponse.WriteAsync(
            context,
            diagnostic,
            AppJsonSerializerContext.Default.RuntimeDiagnostic,
            statusCode);
    }

    private static async Task WriteAgentErrorAsync(
        HttpContext context,
        string eventName,
        string? mode,
        string? tool,
        string? runtime,
        string? model,
        RuntimeDiagnostic diagnostic,
        int statusCode)
    {
        var response = new AgentErrorResponse(
            "error",
            eventName,
            string.IsNullOrWhiteSpace(mode) ? null : mode,
            string.IsNullOrWhiteSpace(tool) ? null : tool,
            string.IsNullOrWhiteSpace(runtime) ? null : runtime,
            string.IsNullOrWhiteSpace(model) ? null : model,
            diagnostic);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.AgentErrorResponse,
            statusCode);
    }

    private static async Task WriteConversationErrorAsync(
        HttpContext context,
        ConversationStoreException exception)
    {
        var diagnostic = new RuntimeDiagnostic(
            exception.Status,
            exception.Code,
            exception.Message,
            null,
            exception.Actions);
        var statusCode = exception.Code is "conversation_not_found" or "artifact_not_found" or "artifact_content_not_available" or "artifact_file_not_found"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest;

        await JsonHttpResponse.WriteAsync(
            context,
            diagnostic,
            AppJsonSerializerContext.Default.RuntimeDiagnostic,
            statusCode);
    }

    private static async Task WriteOpenAiDiagnosticAsync(
        HttpContext context,
        RuntimeDiagnostic diagnostic,
        int statusCode,
        bool stream = false)
    {
        var response = statusCode >= 500
            ? OpenAiErrorResponse.RuntimeUnavailable(diagnostic)
            : OpenAiErrorResponse.InvalidRequest(diagnostic, "model");
        if (stream)
        {
            await StreamingErrorResponse.WriteOpenAiAsync(context, response);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            statusCode);
    }

    private static async Task WriteMultimodalDiagnosticAsync(
        HttpContext context,
        MultimodalOperationResponse response,
        int statusCode)
    {
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.MultimodalOperationResponse,
            statusCode);
    }

    private static async Task WriteMultimodalTextAsync(
        HttpContext context,
        MultimodalTextResponse response)
    {
        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.MultimodalTextResponse);
    }

    internal static async Task WriteOpenAiChatCompletionSuccessAsync(
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

    internal static async Task WriteAnthropicMessageSuccessAsync(
        HttpContext context,
        string model,
        CompletionResult result)
    {
        var response = CreateAnthropicMessageResponse(
            $"msg_{Guid.NewGuid():N}",
            model,
            [new AnthropicContentBlock("text", result.Text)],
            "end_turn",
            result.Usage);

        await JsonHttpResponse.WriteAsync(
            context,
            response,
            AppJsonSerializerContext.Default.AnthropicMessageResponse);
    }

    internal static async Task WriteAnthropicMessageStreamAsync(
        HttpContext context,
        string model,
        int inputTokens,
        Func<Action<string>, CompletionResult> onGenerate,
        Func<InferenceException, RuntimeDiagnostic> onInferenceError)
    {
        var id = $"msg_{Guid.NewGuid():N}";
        var streamStarted = false;
        var contentStarted = false;
        var wroteText = false;
        CompletionResult result;

        try
        {
            result = onGenerate(chunk =>
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                EnsureStreamStarted();
                EnsureContentStarted();
                wroteText = true;
                WriteAnthropicEventAsync(
                    context,
                    "content_block_delta",
                    new AnthropicContentBlockDeltaEvent(
                        "content_block_delta",
                        0,
                        new AnthropicTextDelta("text_delta", chunk)),
                    AppJsonSerializerContext.Default.AnthropicContentBlockDeltaEvent).GetAwaiter().GetResult();
            });
        }
        catch (InferenceException exception)
        {
            await WriteAnthropicStreamErrorAsync(
                context,
                AnthropicErrorResponse.RuntimeUnavailable(onInferenceError(exception)));
            return;
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteAnthropicStreamErrorAsync(
                context,
                AnthropicErrorResponse.RuntimeUnavailable(onInferenceError(CreateNativeRuntimeException(exception))));
            return;
        }

        EnsureStreamStarted();
        EnsureContentStarted();
        if (!wroteText)
        {
            await WriteAnthropicEventAsync(
                context,
                "content_block_delta",
                new AnthropicContentBlockDeltaEvent(
                    "content_block_delta",
                    0,
                    new AnthropicTextDelta("text_delta", result.Text)),
                AppJsonSerializerContext.Default.AnthropicContentBlockDeltaEvent);
        }

        await WriteAnthropicEventAsync(
            context,
            "content_block_stop",
            new AnthropicContentBlockStopEvent("content_block_stop", 0),
            AppJsonSerializerContext.Default.AnthropicContentBlockStopEvent);
        await WriteAnthropicEventAsync(
            context,
            "message_delta",
            new AnthropicMessageDeltaEvent(
                "message_delta",
                new AnthropicMessageDelta("end_turn", null),
                new AnthropicDeltaUsage(result.Usage.CompletionTokens)),
            AppJsonSerializerContext.Default.AnthropicMessageDeltaEvent);
        await WriteAnthropicEventAsync(
            context,
            "message_stop",
            new AnthropicMessageStopEvent("message_stop"),
            AppJsonSerializerContext.Default.AnthropicMessageStopEvent);

        void EnsureStreamStarted()
        {
            if (streamStarted)
            {
                return;
            }

            StartAnthropicStream(context);
            WriteAnthropicEventAsync(
                context,
                "message_start",
                new AnthropicMessageStartEvent(
                    "message_start",
                    CreateAnthropicMessageResponse(
                        id,
                        model,
                        [],
                        null,
                        new TokenUsage(inputTokens, 0, inputTokens))),
                AppJsonSerializerContext.Default.AnthropicMessageStartEvent).GetAwaiter().GetResult();
            streamStarted = true;
        }

        void EnsureContentStarted()
        {
            if (contentStarted)
            {
                return;
            }

            WriteAnthropicEventAsync(
                context,
                "content_block_start",
                new AnthropicContentBlockStartEvent(
                    "content_block_start",
                    0,
                    new AnthropicContentBlock("text", string.Empty)),
                AppJsonSerializerContext.Default.AnthropicContentBlockStartEvent).GetAwaiter().GetResult();
            contentStarted = true;
        }
    }

    internal static async Task WriteOpenAiChatCompletionStreamAsync(
        HttpContext context,
        string model,
        Func<Action<string>, CompletionResult> onGenerate,
        Func<InferenceException, RuntimeDiagnostic> onInferenceError)
    {
        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streamStarted = false;

        var wroteRole = false;
        CompletionResult result;
        try
        {
            result = onGenerate(chunk =>
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                var role = wroteRole ? null : "assistant";
                wroteRole = true;
                EnsureStreamStarted();
                WriteOpenAiChatChunkAsync(
                    context,
                    new OpenAiChatCompletionChunk(
                        id,
                        "chat.completion.chunk",
                        created,
                        model,
                        [new OpenAiChatCompletionChunkChoice(0, new OpenAiChatCompletionDelta(role, chunk), null)],
                        null)).GetAwaiter().GetResult();
            });
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiStreamErrorAsync(
                context,
                OpenAiErrorResponse.RuntimeUnavailable(onInferenceError(exception)));
            return;
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiStreamErrorAsync(
                context,
                OpenAiErrorResponse.RuntimeUnavailable(onInferenceError(CreateNativeRuntimeException(exception))));
            return;
        }

        EnsureStreamStarted();
        if (!wroteRole)
        {
            await WriteOpenAiChatChunkAsync(
                context,
                new OpenAiChatCompletionChunk(
                    id,
                    "chat.completion.chunk",
                    created,
                    model,
                    [new OpenAiChatCompletionChunkChoice(0, new OpenAiChatCompletionDelta("assistant", null), null)],
                    null));
        }

        await WriteOpenAiChatChunkAsync(
            context,
            new OpenAiChatCompletionChunk(
                id,
                "chat.completion.chunk",
                created,
                model,
                [new OpenAiChatCompletionChunkChoice(0, new OpenAiChatCompletionDelta(null, null), "stop")],
                ToOpenAiUsage(result.Usage)));

        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        void EnsureStreamStarted()
        {
            if (streamStarted)
            {
                return;
            }

            StartOpenAiStream(context);
            streamStarted = true;
        }
    }

    private static async Task WriteOpenAiCompletionStreamAsync(
        HttpContext context,
        string model,
        Func<Action<string>, CompletionResult> onGenerate,
        Func<InferenceException, RuntimeDiagnostic> onInferenceError)
    {
        var id = $"cmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var streamStarted = false;

        var wroteText = false;
        CompletionResult result;
        try
        {
            result = onGenerate(chunk =>
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                wroteText = true;
                EnsureStreamStarted();
                WriteOpenAiCompletionChunkAsync(
                    context,
                    new OpenAiCompletionChunk(
                        id,
                        "text_completion",
                        created,
                        model,
                        [new OpenAiCompletionChunkChoice(chunk, 0, null)],
                        null)).GetAwaiter().GetResult();
            });
        }
        catch (InferenceException exception)
        {
            await WriteOpenAiStreamErrorAsync(
                context,
                OpenAiErrorResponse.RuntimeUnavailable(onInferenceError(exception)));
            return;
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOpenAiStreamErrorAsync(
                context,
                OpenAiErrorResponse.RuntimeUnavailable(onInferenceError(CreateNativeRuntimeException(exception))));
            return;
        }

        EnsureStreamStarted();
        if (!wroteText)
        {
            await WriteOpenAiCompletionChunkAsync(
                context,
                new OpenAiCompletionChunk(
                    id,
                    "text_completion",
                    created,
                    model,
                    [new OpenAiCompletionChunkChoice(string.Empty, 0, null)],
                    null));
        }

        await WriteOpenAiCompletionChunkAsync(
            context,
            new OpenAiCompletionChunk(
                id,
                "text_completion",
                created,
                model,
                [new OpenAiCompletionChunkChoice(string.Empty, 0, "stop")],
                ToOpenAiUsage(result.Usage)));

        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        void EnsureStreamStarted()
        {
            if (streamStarted)
            {
                return;
            }

            StartOpenAiStream(context);
            streamStarted = true;
        }
    }

    private static void StartOpenAiStream(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteLogEventAsync(HttpContext context, LogStreamEntry entry)
    {
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            entry,
            AppJsonSerializerContext.Default.LogStreamEntry,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static Microsoft.Extensions.Logging.LogLevel? TryParseLogLevel(string? value)
        => Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(value, ignoreCase: true, out var level)
            ? level
            : null;

    private static string? NormalizeLogCategory(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void StartAnthropicStream(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteAnthropicEventAsync<T>(
        HttpContext context,
        string eventName,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        await context.Response.WriteAsync($"event: {eventName}\n", context.RequestAborted);
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            jsonTypeInfo,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteOpenAiStreamErrorAsync(HttpContext context, OpenAiErrorResponse error)
    {
        if (!context.Response.HasStarted)
        {
            await StreamingErrorResponse.WriteOpenAiAsync(context, error);
            return;
        }

        await context.Response.WriteAsync("event: error\n", context.RequestAborted);
        await context.Response.WriteAsync("data: ", context.RequestAborted);
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n\n", context.RequestAborted);
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteAnthropicStreamErrorAsync(HttpContext context, AnthropicErrorResponse error)
    {
        if (!context.Response.HasStarted)
        {
            StartAnthropicStream(context);
        }

        await WriteAnthropicEventAsync(
            context,
            "error",
            error,
            AppJsonSerializerContext.Default.AnthropicErrorResponse);
    }

    internal static async Task WriteOllamaGenerateStreamAsync(
        HttpContext context,
        string model,
        Func<Action<string>, CompletionResult> onGenerate,
        Func<InferenceException, RuntimeDiagnostic> onInferenceError)
    {
        StartOllamaStream(context);
        var wroteText = false;
        CompletionResult result;
        try
        {
            result = onGenerate(chunk =>
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                wroteText = true;
                WriteOllamaGenerateChunkAsync(
                    context,
                    new OllamaGenerateResponse(
                        model,
                        DateTimeOffset.UtcNow,
                        chunk,
                        false,
                        null,
                        0,
                        0,
                        0,
                        0)).GetAwaiter().GetResult();
            });
        }
        catch (InferenceException exception)
        {
            await WriteOllamaStreamErrorAsync(
                context,
                OllamaErrorResponse.RuntimeUnavailable(onInferenceError(exception)));
            return;
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOllamaStreamErrorAsync(
                context,
                OllamaErrorResponse.RuntimeUnavailable(onInferenceError(CreateNativeRuntimeException(exception))));
            return;
        }

        await WriteOllamaGenerateChunkAsync(
            context,
            new OllamaGenerateResponse(
                model,
                DateTimeOffset.UtcNow,
                wroteText ? string.Empty : result.Text,
                true,
                null,
                ToNanoseconds(result.Elapsed),
                0,
                result.Usage.PromptTokens,
                result.Usage.CompletionTokens));
    }

    internal static async Task WriteOllamaChatStreamAsync(
        HttpContext context,
        string model,
        Func<Action<string>, CompletionResult> onGenerate,
        Func<InferenceException, RuntimeDiagnostic> onInferenceError)
    {
        StartOllamaStream(context);
        var wroteText = false;
        CompletionResult result;
        try
        {
            result = onGenerate(chunk =>
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                wroteText = true;
                WriteOllamaChatChunkAsync(
                    context,
                    new OllamaChatResponse(
                        model,
                        DateTimeOffset.UtcNow,
                        new OllamaChatMessage("assistant", chunk),
                        false,
                        0,
                        0,
                        0,
                        0)).GetAwaiter().GetResult();
            });
        }
        catch (InferenceException exception)
        {
            await WriteOllamaStreamErrorAsync(
                context,
                OllamaErrorResponse.RuntimeUnavailable(onInferenceError(exception)));
            return;
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            await WriteOllamaStreamErrorAsync(
                context,
                OllamaErrorResponse.RuntimeUnavailable(onInferenceError(CreateNativeRuntimeException(exception))));
            return;
        }

        await WriteOllamaChatChunkAsync(
            context,
            new OllamaChatResponse(
                model,
                DateTimeOffset.UtcNow,
                new OllamaChatMessage("assistant", wroteText ? string.Empty : result.Text),
                true,
                ToNanoseconds(result.Elapsed),
                0,
                result.Usage.PromptTokens,
                result.Usage.CompletionTokens));
    }

    private static void StartOllamaStream(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteOllamaGenerateChunkAsync(
        HttpContext context,
        OllamaGenerateResponse response)
    {
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            AppJsonSerializerContext.Default.OllamaGenerateResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteOllamaChatChunkAsync(
        HttpContext context,
        OllamaChatResponse response)
    {
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            response,
            AppJsonSerializerContext.Default.OllamaChatResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteOllamaStreamErrorAsync(HttpContext context, OllamaErrorResponse error)
    {
        if (!context.Response.HasStarted)
        {
            await StreamingErrorResponse.WriteOllamaAsync(context, error);
            return;
        }

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            error,
            AppJsonSerializerContext.Default.OllamaErrorResponse,
            context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
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

    internal static async Task WriteOllamaChatSuccessAsync(
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
        await context.Response.Body.FlushAsync(context.RequestAborted);
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
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteOpenAiInvalidRequestAsync(HttpContext context, string message, bool stream = false)
    {
        var error = OpenAiErrorResponse.InvalidRequest(message);
        if (stream)
        {
            await StreamingErrorResponse.WriteOpenAiAsync(context, error);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            error,
            AppJsonSerializerContext.Default.OpenAiErrorResponse,
            StatusCodes.Status400BadRequest);
    }

    private static async Task WriteAnthropicInvalidRequestAsync(HttpContext context, string message, bool stream = false)
    {
        var error = AnthropicErrorResponse.InvalidRequest(message);
        if (stream)
        {
            await WriteAnthropicStreamErrorAsync(context, error);
            return;
        }

        await JsonHttpResponse.WriteAsync(
            context,
            error,
            AppJsonSerializerContext.Default.AnthropicErrorResponse,
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

    private static async Task SuppressRequestAbortExceptions(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client has already gone away, so there is no response left to write.
        }
    }

    private static bool IsInvalidRequestInferenceException(InferenceException exception)
        => exception.Code is
            "invalid_audio" or
            "invalid_request" or
            "tool_not_found" or
            "tool_round_limit_exceeded" or
            "unsupported_audio_format";

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

    private static AnthropicMessageResponse CreateAnthropicMessageResponse(
        string id,
        string model,
        IReadOnlyList<AnthropicContentBlock> content,
        string? stopReason,
        TokenUsage usage)
    {
        return new AnthropicMessageResponse(
            id,
            "message",
            "assistant",
            content,
            model,
            stopReason,
            null,
            new AnthropicUsage(usage.PromptTokens, usage.CompletionTokens));
    }

    private static TokenUsage EstimateVisionUsage(string prompt, string output)
    {
        var promptTokens = EstimateTokenCount(prompt);
        var completionTokens = EstimateTokenCount(output);
        return new TokenUsage(promptTokens, completionTokens, promptTokens + completionTokens);
    }

    private static int EstimateTokenCount(string value)
        => Math.Max(1, (value.Length + 3) / 4);

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

    private static bool IsAnthropicModelListRequest(HttpContext context)
    {
        if (context.Request.Query.ContainsKey("limit") ||
            context.Request.Headers.ContainsKey("anthropic-version") ||
            context.Request.Headers.ContainsKey("anthropic-beta"))
        {
            return true;
        }

        var userAgent = context.Request.Headers["User-Agent"].ToString();
        return userAgent.Contains("claude-code", StringComparison.OrdinalIgnoreCase);
    }

    private static AnthropicModelListResponse CreateAnthropicModelListResponse(LocalModelCatalog modelCatalog)
    {
        var models = modelCatalog
            .ListModels()
            .Where(IsTextGenerationModel)
            .Select(static model => new AnthropicModelResponse(
                ToClaudeCodeModelAlias(model),
                $"{model.Name} (Tomur local)",
                new DateTimeOffset(model.LastModifiedUtc, TimeSpan.Zero),
                "local",
                model.Capabilities))
            .ToArray();

        return new AnthropicModelListResponse(
            models,
            false,
            models.FirstOrDefault()?.Id,
            models.LastOrDefault()?.Id);
    }

    private static LocalModelDescriptor? FindProtocolModel(LocalModelCatalog modelCatalog, string? requestedModel)
    {
        var direct = modelCatalog.Find(requestedModel);
        if (direct is not null)
        {
            return direct;
        }

        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return null;
        }

        var requested = requestedModel.Trim();
        return modelCatalog
            .ListModels()
            .FirstOrDefault(model =>
                IsTextGenerationModel(model) &&
                string.Equals(ToClaudeCodeModelAlias(model), requested, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToClaudeCodeModelAlias(LocalModelDescriptor model)
    {
        var slug = CreateProtocolSlug(model.Id);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(model.Id));
        var suffix = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"claude-tomur-{slug}-{suffix}";
    }

    private static string CreateProtocolSlug(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(normalized.Length);
        var previousDash = false;
        foreach (var character in normalized)
        {
            if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
            {
                builder.Append(character);
                previousDash = false;
                continue;
            }

            if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "local-model" : slug;
    }

    private static bool IsTextGenerationModel(LocalModelDescriptor model)
        => model.Capabilities.Any(static capability =>
            string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ChatTurn> CreateAnthropicChatTurns(AnthropicMessageRequest request)
    {
        var messages = new List<ChatTurn>();
        var system = ExtractAnthropicTextContent(request.System);
        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatTurn("system", system));
        }

        if (request.Messages is null)
        {
            return messages;
        }

        foreach (var message in request.Messages)
        {
            var text = ExtractAnthropicTextContent(message.Content);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            messages.Add(new ChatTurn(NormalizeAnthropicRole(message.Role), text));
        }

        return messages;
    }

    private static string NormalizeAnthropicRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user"
        };

    private static int EstimateAnthropicInputCharacters(AnthropicMessageRequest request)
    {
        var count = EstimateJsonElementCharacters(request.System);
        if (request.Messages is null)
        {
            return count;
        }

        return count + request.Messages.Sum(static message => EstimateJsonElementCharacters(message.Content));
    }

    private static string ExtractAnthropicTextContent(JsonElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        return ExtractAnthropicTextContent(element.Value);
    }

    private static string ExtractAnthropicTextContent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                var part = ExtractAnthropicTextContent(item);
                if (!string.IsNullOrWhiteSpace(part))
                {
                    parts.Add(part);
                }
            }

            return string.Join("\n", parts);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return value.GetRawText();
        }

        var type = TryGetString(value, "type");
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetString(value, "text") ?? string.Empty;
        }

        if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            var toolUseId = TryGetString(value, "tool_use_id");
            var content = value.TryGetProperty("content", out var contentElement)
                ? ExtractAnthropicTextContent(contentElement)
                : value.GetRawText();
            return string.IsNullOrWhiteSpace(toolUseId)
                ? $"Tool result:\n{content}"
                : $"Tool result for {toolUseId}:\n{content}";
        }

        if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
        {
            var name = TryGetString(value, "name") ?? "tool";
            var input = value.TryGetProperty("input", out var inputElement)
                ? inputElement.GetRawText()
                : "{}";
            return $"Tool request {name}:\n{input}";
        }

        if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
        {
            return "[Image content was provided, but this Claude Code compatibility endpoint currently maps requests to local text chat.]";
        }

        if (value.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (value.TryGetProperty("content", out var nestedContent))
        {
            return ExtractAnthropicTextContent(nestedContent);
        }

        return value.GetRawText();
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

    private static bool ContainsOpenAiImageContent(IReadOnlyList<OpenAiChatMessage> messages)
        => messages.Any(static message => ContainsImageContent(message.Content));

    private static bool TryCreateOpenAiVisionInput(
        IReadOnlyList<OpenAiChatMessage> messages,
        out string prompt,
        out IReadOnlyList<ImageInputBytes> images,
        out string error)
    {
        prompt = string.Join(
            "\n",
            messages.Select(static message => ExtractOpenAiTextContent(message.Content))
                .Where(static item => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = "Describe the image.";
        }

        var collected = new List<ImageInputBytes>();
        foreach (var message in messages)
        {
            if (!TryCollectOpenAiImages(message.Content, collected, out error))
            {
                images = [];
                return false;
            }
        }

        if (collected.Count == 0)
        {
            images = [];
            error = "At least one image content item is required.";
            return false;
        }

        if (collected.Count > CompatibilityProtocolLimits.MaxImageCount)
        {
            images = [];
            error = $"Too many images. Limit: {CompatibilityProtocolLimits.MaxImageCount}.";
            return false;
        }

        images = collected;
        error = string.Empty;
        return true;
    }

    private static bool TryCollectOpenAiImages(JsonElement? element, List<ImageInputBytes> images, out string error)
    {
        error = string.Empty;
        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var item in element.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryReadOpenAiImageInput(item, out var imageInput))
            {
                continue;
            }

            if (!TryCreateImageInput(imageInput, out var image, out error))
            {
                return false;
            }

            images.Add(image);
        }

        return true;
    }

    private static bool TryReadOpenAiImageInput(JsonElement item, out MultimodalImageInput input)
    {
        input = new MultimodalImageInput(null, null, null, null);
        if (item.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            !IsImageContentType(type.GetString()))
        {
            return false;
        }

        string? imageUrl = null;
        string? dataUri = null;
        string? mediaType = null;
        string? detail = null;

        if (item.TryGetProperty("image_url", out var imageUrlElement))
        {
            if (imageUrlElement.ValueKind == JsonValueKind.String)
            {
                imageUrl = imageUrlElement.GetString();
            }
            else if (imageUrlElement.ValueKind == JsonValueKind.Object)
            {
                imageUrl = TryGetString(imageUrlElement, "url");
                detail = TryGetString(imageUrlElement, "detail");
            }
        }

        if (item.TryGetProperty("input_image", out var inputImageElement))
        {
            if (inputImageElement.ValueKind == JsonValueKind.String)
            {
                imageUrl ??= inputImageElement.GetString();
            }
            else if (inputImageElement.ValueKind == JsonValueKind.Object)
            {
                imageUrl ??= TryGetString(inputImageElement, "image_url") ?? TryGetString(inputImageElement, "url");
            }

            dataUri ??= inputImageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(inputImageElement, "data_uri")
                : null;
            mediaType ??= inputImageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(inputImageElement, "media_type")
                : null;
        }

        if (item.TryGetProperty("image", out var imageElement))
        {
            imageUrl ??= imageElement.ValueKind == JsonValueKind.String
                ? imageElement.GetString()
                : TryGetString(imageElement, "url") ?? TryGetString(imageElement, "image_url");
            dataUri ??= imageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(imageElement, "data_uri") ?? TryGetString(imageElement, "data")
                : null;
            mediaType ??= imageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(imageElement, "media_type")
                : null;
        }

        dataUri ??= TryGetString(item, "data_uri");
        mediaType ??= TryGetString(item, "media_type");
        detail ??= TryGetString(item, "detail");

        input = new MultimodalImageInput(imageUrl, dataUri, mediaType, detail);
        return !string.IsNullOrWhiteSpace(imageUrl) || !string.IsNullOrWhiteSpace(dataUri);
    }

    private static bool TryCreateImageInputs(
        IReadOnlyList<MultimodalImageInput> imageInputs,
        out IReadOnlyList<ImageInputBytes> images,
        out string error)
    {
        var collected = new List<ImageInputBytes>(imageInputs.Count);
        foreach (var input in imageInputs)
        {
            if (!TryCreateImageInput(input, out var image, out error))
            {
                images = [];
                return false;
            }

            collected.Add(image);
        }

        images = collected;
        error = string.Empty;
        return true;
    }

    private static bool TryCreateImageInput(
        MultimodalImageInput input,
        out ImageInputBytes image,
        out string error)
    {
        var source = FirstNonEmpty(input.DataUri, input.ImageUrl);
        if (string.IsNullOrWhiteSpace(source))
        {
            image = new ImageInputBytes([], null, null);
            error = "Image input must include data_uri or image_url.";
            return false;
        }

        if (TryDecodeDataUri(source, input.MediaType, out image, input.Detail, out error))
        {
            return true;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            image = new ImageInputBytes([], null, null);
            error = "Remote image URLs are not fetched by local R8 endpoints yet; send a data URI instead.";
            return false;
        }

        if (TryDecodeBase64(source, input.MediaType, input.Detail, out image, out var decodeError))
        {
            error = string.Empty;
            return true;
        }

        image = new ImageInputBytes([], null, null);
        error = string.IsNullOrWhiteSpace(decodeError)
            ? "Image input must be a data URI or raw base64 payload."
            : decodeError;
        return false;
    }

    private static bool TryCreateAudioInput(
        string? dataUri,
        string? base64,
        string? mediaType,
        string? name,
        out byte[] audioBytes,
        out string? resolvedMediaType,
        out string error)
    {
        audioBytes = [];
        resolvedMediaType = string.IsNullOrWhiteSpace(mediaType) ? ResolveMediaTypeFromName(name) : mediaType.Trim();
        error = string.Empty;

        var source = FirstNonEmpty(dataUri, base64);
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "Voice turns require audio_data_uri or audio_base64.";
            return false;
        }

        if (TryDecodeAudioDataUri(source, resolvedMediaType, out audioBytes, out var dataUriMediaType, out error))
        {
            resolvedMediaType = dataUriMediaType ?? resolvedMediaType ?? "audio/wav";
            return true;
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryDecodeAudioBase64(source, out audioBytes, out error))
        {
            return false;
        }

        resolvedMediaType ??= "audio/wav";
        return true;
    }

    private static bool TryDecodeAudioDataUri(
        string source,
        string? fallbackMediaType,
        out byte[] audioBytes,
        out string? mediaType,
        out string error)
    {
        audioBytes = [];
        mediaType = fallbackMediaType;
        error = string.Empty;
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            error = "Audio data URI is missing the payload separator.";
            return false;
        }

        var metadata = source[5..comma];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            error = "Audio data URI must use base64 encoding.";
            return false;
        }

        mediaType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static item => item.Contains('/', StringComparison.Ordinal))
            ?? fallbackMediaType;
        return TryDecodeAudioBase64(source[(comma + 1)..], out audioBytes, out error);
    }

    private static bool TryDecodeAudioBase64(
        string source,
        out byte[] audioBytes,
        out string error)
    {
        try
        {
            audioBytes = Convert.FromBase64String(source.Trim());
            if (audioBytes.Length == 0)
            {
                error = "Audio payload is empty.";
                return false;
            }

            if (audioBytes.LongLength > CompatibilityProtocolLimits.MaxAudioBytes)
            {
                audioBytes = [];
                error = $"Audio payload is too large. Limit: {CompatibilityProtocolLimits.MaxAudioBytes} bytes.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (FormatException)
        {
            audioBytes = [];
            error = "Audio payload is not valid base64.";
            return false;
        }
    }

    private static string? ResolveMediaTypeFromName(string? name)
    {
        return Path.GetExtension(name)?.ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            _ => null
        };
    }

    private static string? ReadFormString(IFormCollection form, string key)
    {
        var value = form[key].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ReadFormInt(IFormCollection form, string key)
        => int.TryParse(ReadFormString(form, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ReadFormDouble(IFormCollection form, string key)
        => double.TryParse(ReadFormString(form, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? ReadFormBool(IFormCollection form, string key)
    {
        var value = ReadFormString(form, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value is "1" or "yes" or "on";
    }

    private static bool TryDecodeDataUri(
        string source,
        string? fallbackMediaType,
        out ImageInputBytes image,
        string? detail,
        out string error)
    {
        image = new ImageInputBytes([], null, null);
        error = string.Empty;
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            error = "Data URI image input is missing the payload separator.";
            return false;
        }

        var metadata = source[5..comma];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            error = "Data URI image input must use base64 encoding.";
            return false;
        }

        var mediaType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static item => item.Contains('/', StringComparison.Ordinal))
            ?? fallbackMediaType;
        if (!TryDecodeBase64(source[(comma + 1)..], mediaType, detail, out image, out var decodeError))
        {
            error = decodeError;
            return false;
        }

        return true;
    }

    private static bool TryDecodeBase64(
        string source,
        string? mediaType,
        string? detail,
        out ImageInputBytes image,
        out string error)
    {
        try
        {
            image = new ImageInputBytes(Convert.FromBase64String(source.Trim()), mediaType, detail);
            if (image.Bytes.Length == 0)
            {
                error = "Image payload is empty.";
                return false;
            }

            if (image.Bytes.Length > CompatibilityProtocolLimits.MaxImageBytes)
            {
                image = new ImageInputBytes([], null, null);
                error = $"Image payload is too large. Limit: {CompatibilityProtocolLimits.MaxImageBytes} bytes.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (FormatException)
        {
            image = new ImageInputBytes([], null, null);
            error = "Image payload is not valid base64.";
            return false;
        }
    }

    private static bool TryParseImageSize(
        string? value,
        out int width,
        out int height,
        out string error)
    {
        var size = string.IsNullOrWhiteSpace(value) ? "1024x1024" : value.Trim();
        var parts = size.Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out width) ||
            !int.TryParse(parts[1], out height))
        {
            width = 0;
            height = 0;
            error = "The size field must use WIDTHxHEIGHT format, for example 1024x1024.";
            return false;
        }

        if (width < 256 || height < 256 || width > 2048 || height > 2048 || width % 64 != 0 || height % 64 != 0)
        {
            error = "The size field must be between 256x256 and 2048x2048, with width and height divisible by 64.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string? NormalizeImageResponseFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "url";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "url" or "b64_json" ? normalized : null;
    }

    private static ImageGenerationDefaults ResolveImageGenerationDefaults(LocalModelDescriptor model)
    {
        if (model.PackageId?.Contains("flux2-klein", StringComparison.OrdinalIgnoreCase) == true ||
            model.Id.Contains("flux2-klein", StringComparison.OrdinalIgnoreCase) ||
            model.Name.Contains("flux.2 klein", StringComparison.OrdinalIgnoreCase) ||
            model.FileName.Contains("flux-2-klein", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageGenerationDefaults(4, 1.0f, "euler");
        }

        return new ImageGenerationDefaults(20, 7.0f, null);
    }

    private static string? NormalizeSpeechResponseFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "wav";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "wav" ? normalized : null;
    }

    private static float ToFloat(double? value, float fallback)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return fallback;
        }

        return (float)value.Value;
    }

    private static float? ToOptionalFloat(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return (float)value.Value;
    }

    private static string ResolveImageMimeType(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool ContainsImageContent(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in element.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                IsImageContentType(type.GetString()))
            {
                return true;
            }

            if (item.TryGetProperty("image_url", out _) ||
                item.TryGetProperty("image", out _) ||
                item.TryGetProperty("input_image", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsImageContentType(string? value)
        => string.Equals(value, "image_url", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "input_image", StringComparison.OrdinalIgnoreCase);

    private static bool ModelHasCapability(LocalModelDescriptor model, string capability)
        => model.Capabilities.Any(item => string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));

    private static MultimodalInputSummary EmptyInputSummary()
        => new(0, 0, null, null, null);

    private sealed record ImageGenerationDefaults(
        int Steps,
        float CfgScale,
        string? SampleMethod);

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

    private sealed record ParsedVoiceTurnRequest(
        ConversationVoiceTurnRequest Request,
        byte[] AudioBytes,
        string? MediaType,
        string? FileName);
}
