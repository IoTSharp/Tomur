using System.Globalization;
using Tomur.Config;
using Tomur.Agents;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Runtime;

namespace Tomur.Conversations;

public sealed class ConversationOrchestrationService
{
    private const string RuntimeName = "Microsoft.Agents.AI.ChatClientAgent";
    private const int HttpOk = 200;
    private const int HttpBadRequest = 400;
    private const int HttpNotFound = 404;
    private const int HttpServiceUnavailable = 503;
    private readonly ConversationStore conversations;
    private readonly AgentRuntimeService agentRuntime;
    private readonly ToolInvoker toolInvoker;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;
    private readonly AgentEventLog eventLog;
    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalExecutionService multimodalExecution;
    private readonly DataPaths paths;

    public ConversationOrchestrationService(
        ConversationStore conversations,
        AgentRuntimeService agentRuntime,
        ToolInvoker toolInvoker,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        AgentEventLog eventLog,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution,
        DataPaths paths)
    {
        this.conversations = conversations;
        this.agentRuntime = agentRuntime;
        this.toolInvoker = toolInvoker;
        this.diagnosticsProvider = diagnosticsProvider;
        this.eventLog = eventLog;
        this.modelCatalog = modelCatalog;
        this.multimodalExecution = multimodalExecution;
        this.paths = paths;
    }

    public async Task<ConversationTurnResult> RunTurnAsync(
        string conversationId,
        ConversationTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(request);

        var content = NormalizeContent(request.Content);
        if (content is null)
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Conversation turns require text content in the current R10 boundary.",
                [
                    "Send text content for this turn.",
                    "Use POST /api/conversations/{conversationId}/messages to persist attachment-only messages until multimodal turn orchestration is wired."
                ]);
        }

        var userAppend = conversations.AppendMessage(
            conversationId,
            new ConversationAppendMessageRequest(
                "user",
                content,
                NormalizeModality(request.Modality),
                "ok",
                request.Model,
                request.Attachments,
                null,
                null,
                request.Metadata));
        var messages = new List<ConversationMessageRecord>
        {
            userAppend.Message
        };
        var diagnostics = new List<ConversationDiagnosticRecord>();
        var model = request.Model ?? userAppend.Message.Model ?? userAppend.Conversation.Model;
        var toolMode = ResolveToolModeForAudit(request.ToolMode, request.Tools);

        try
        {
            var recentMessages = conversations.ListRecentMessages(conversationId, request.HistoryLimit);
            var agentRequest = new AgentChatRequest(
                model,
                null,
                BuildAgentMessages(recentMessages),
                null,
                request.ToolMode,
                request.Tools,
                request.MaxToolRounds,
                request.Instructions,
                request.MaxTokens,
                request.Temperature,
                request.TopP);
            var agentResponse = await agentRuntime.RunChatAsync(
                    agentRequest,
                    toolInvoker,
                    cancellationToken)
                .ConfigureAwait(false);

            ConversationAppendMessageResponse? toolAppend = null;
            if (agentResponse.ToolCalls.Count > 0)
            {
                toolAppend = conversations.AppendMessage(
                    conversationId,
                    new ConversationAppendMessageRequest(
                        "tool",
                        BuildToolMessageContent(agentResponse.ToolCalls),
                        "tool",
                        agentResponse.ToolCalls.Any(IsBlockedToolCall) ? "partial" : "ok",
                        agentResponse.Model,
                        null,
                        agentResponse.ToolCalls.Select(ToConversationToolCall).ToArray(),
                        null,
                        null));
                messages.Add(toolAppend.Message);
            }

            ConversationAppendMessageResponse? assistantAppend = null;
            if (!string.IsNullOrWhiteSpace(agentResponse.Text))
            {
                assistantAppend = conversations.AppendMessage(
                    conversationId,
                    new ConversationAppendMessageRequest(
                        "assistant",
                        agentResponse.Text,
                        "text",
                        agentResponse.Status,
                        agentResponse.Model,
                        null,
                        null,
                        null,
                        null));
                messages.Add(assistantAppend.Message);
            }

            if (agentResponse.ToolCalls.Any(IsBlockedToolCall))
            {
                var diagnosticAppend = conversations.AppendDiagnostic(
                    conversationId,
                    new ConversationAppendDiagnosticRequest(
                        "warning",
                        "tool_call_blocked",
                        "One or more requested tools were blocked by the current local orchestration boundary.",
                        agentResponse.Model,
                        RuntimeName,
                        ["Inspect GET /api/agents/tools and use dedicated endpoints for side-effect tools until automatic tool-calling is enabled."],
                        null));
                diagnostics.Add(diagnosticAppend.Diagnostic);
            }

            var conversation = assistantAppend?.Conversation
                ?? toolAppend?.Conversation
                ?? userAppend.Conversation;
            var response = new ConversationTurnResponse(
                agentResponse.Status,
                conversation,
                messages,
                userAppend.Message,
                toolAppend?.Message,
                assistantAppend?.Message,
                diagnostics,
                agentResponse);

            return new ConversationTurnResult(response, HttpOk);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InferenceException exception)
        {
            return await PersistFailureAsync(
                    conversationId,
                    userAppend,
                    model,
                    toolMode,
                    diagnosticsProvider.GetRuntimeFailure(model, exception),
                    ResolveInferenceStatusCode(exception),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var runtimeException = new InferenceException(
                "native_runtime_unavailable",
                $"The llama.cpp native runtime could not be used: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Run tomur doctor to inspect native runtime status."
                ],
                exception);
            return await PersistFailureAsync(
                    conversationId,
                    userAppend,
                    model,
                    toolMode,
                    diagnosticsProvider.GetRuntimeFailure(model, runtimeException),
                    HttpServiceUnavailable,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<ConversationVoiceTurnResult> RunVoiceTurnAsync(
        string conversationId,
        ConversationVoiceTurnRequest request,
        byte[] audioBytes,
        string? audioMediaType,
        string? audioName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audioBytes);

        if (audioBytes.Length == 0)
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Voice turns require non-empty audio input.",
                ["Send a PCM16 WAV file in multipart field 'file', or provide audio_base64/audio_data_uri in JSON."]);
        }

        var diagnostics = new List<ConversationDiagnosticRecord>();
        var inputArtifactResult = await TryRegisterAudioArtifactAsync(
                conversationId,
                audioBytes,
                NormalizeOptional(audioMediaType) ?? NormalizeOptional(request.AudioMediaType) ?? "audio/wav",
                NormalizeOptional(audioName) ?? NormalizeOptional(request.AudioName) ?? "voice-input.wav",
                "voice-input",
                "conversation.voice-turn.input",
                cancellationToken)
            .ConfigureAwait(false);

        if (inputArtifactResult.Error is not null)
        {
            return inputArtifactResult.Error;
        }

        var inputArtifact = inputArtifactResult.Artifact!;
        var conversation = inputArtifactResult.Conversation!;
        if (!TryResolveModelByCapability(
                request.AsrModel,
                "audio",
                "/api/conversations/{conversationId}/voice-turns",
                "asr",
                out var asrModel,
                out var asrDiagnostic,
                out var asrStatusCode))
        {
            return PersistVoiceFailure(
                conversationId,
                conversation,
                null,
                inputArtifact,
                null,
                null,
                null,
                asrDiagnostic!,
                diagnostics,
                asrStatusCode);
        }

        NativeOperationResult transcriptResult;
        try
        {
            transcriptResult = multimodalExecution.TranscribeAudio(
                asrModel!,
                audioBytes,
                request.Language,
                cancellationToken);
        }
        catch (InferenceException exception)
        {
            return PersistVoiceFailure(
                conversationId,
                conversation,
                null,
                inputArtifact,
                null,
                null,
                null,
                diagnosticsProvider.GetRuntimeFailure(asrModel!.Id, exception),
                diagnostics,
                ResolveInferenceStatusCode(exception));
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var runtimeException = new InferenceException(
                "native_runtime_unavailable",
                $"The ASR native runtime could not be used: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Use /api/runtime/multimodal to inspect backend readiness."
                ],
                exception);
            return PersistVoiceFailure(
                conversationId,
                conversation,
                null,
                inputArtifact,
                null,
                null,
                null,
                diagnosticsProvider.GetRuntimeFailure(asrModel!.Id, runtimeException),
                diagnostics,
                HttpServiceUnavailable);
        }

        var transcript = transcriptResult.Text.Trim();
        diagnostics.Add(AppendRuntimeDiagnostic(
            conversationId,
            new RuntimeDiagnostic(
                "ok",
                "asr_transcribed",
                "Audio input was transcribed by the local ASR runtime.",
                asrModel!.Id,
                transcriptResult.Diagnostics.Count == 0
                    ? [$"elapsed_ms: {(long)Math.Round(transcriptResult.Elapsed.TotalMilliseconds)}"]
                    : transcriptResult.Diagnostics)));

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return PersistVoiceFailure(
                conversationId,
                conversation,
                transcript,
                inputArtifact,
                null,
                null,
                null,
                new RuntimeDiagnostic(
                    "error",
                    "asr_empty_transcript",
                    "The local ASR runtime returned an empty transcript, so the conversation turn was not started.",
                    asrModel.Id,
                    [
                        "Send clearer speech audio.",
                        "Verify that the audio language matches the language option, or omit language for auto-detection."
                    ]),
                diagnostics,
                HttpBadRequest);
        }

        var turnRequest = new ConversationTurnRequest(
            transcript,
            "audio",
            request.Model,
            [
                new ConversationAttachment(
                    inputArtifact.Id,
                    "audio",
                    NormalizeOptional(audioName) ?? NormalizeOptional(request.AudioName),
                    inputArtifact.MediaType,
                    inputArtifact.Path,
                    inputArtifact.Bytes,
                    null)
            ],
            request.ToolMode,
            request.Tools,
            request.MaxToolRounds,
            request.Instructions,
            request.MaxTokens,
            request.Temperature,
            request.TopP,
            request.HistoryLimit,
            request.Metadata);

        var turnResult = await RunTurnAsync(conversationId, turnRequest, cancellationToken)
            .ConfigureAwait(false);
        diagnostics.AddRange(turnResult.Response.Diagnostics);

        var currentConversation = turnResult.Response.Conversation;
        var shouldSpeak = request.Speak ?? true;
        if (!shouldSpeak ||
            turnResult.Response.AssistantMessage is null ||
            string.IsNullOrWhiteSpace(turnResult.Response.AssistantMessage.Content))
        {
            var status = turnResult.Response.Status == "ok" ? "ok" : turnResult.Response.Status;
            return new ConversationVoiceTurnResult(
                new ConversationVoiceTurnResponse(
                    status,
                    currentConversation,
                    transcript,
                    inputArtifact,
                    turnResult.Response.UserMessage,
                    turnResult.Response.ToolMessage,
                    turnResult.Response.AssistantMessage,
                    null,
                    diagnostics,
                    turnResult.Response,
                    null,
                    null),
                turnResult.StatusCode);
        }

        if (!TryResolveModelByCapability(
                request.TtsModel,
                "audio-output",
                "/api/conversations/{conversationId}/voice-turns",
                "tts",
                out var ttsModel,
                out var ttsDiagnostic,
                out _))
        {
            var diagnostic = AppendRuntimeDiagnostic(conversationId, ttsDiagnostic!);
            diagnostics.Add(diagnostic);
            return new ConversationVoiceTurnResult(
                new ConversationVoiceTurnResponse(
                    "partial",
                    conversations.Get(conversationId, 1).Conversation,
                    transcript,
                    inputArtifact,
                    turnResult.Response.UserMessage,
                    turnResult.Response.ToolMessage,
                    turnResult.Response.AssistantMessage,
                    null,
                    diagnostics,
                    turnResult.Response,
                    null,
                    null),
                HttpOk);
        }

        var responseFormat = NormalizeSpeechResponseFormat(request.ResponseFormat);
        if (responseFormat is null)
        {
            var diagnostic = AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "warning",
                    "invalid_request",
                    "The voice turn response_format currently supports only 'wav'. Text response was still persisted.",
                    ttsModel!.Id,
                    ["Set response_format to wav or omit it."]));
            diagnostics.Add(diagnostic);
            return new ConversationVoiceTurnResult(
                new ConversationVoiceTurnResponse(
                    "partial",
                    conversations.Get(conversationId, 1).Conversation,
                    transcript,
                    inputArtifact,
                    turnResult.Response.UserMessage,
                    turnResult.Response.ToolMessage,
                    turnResult.Response.AssistantMessage,
                    null,
                    diagnostics,
                    turnResult.Response,
                    null,
                    null),
                HttpOk);
        }

        NativeAudioResult speechResult;
        try
        {
            speechResult = multimodalExecution.SynthesizeSpeech(
                ttsModel!,
                new SpeechSynthesisOptions(
                    turnResult.Response.AssistantMessage.Content,
                    request.Voice,
                    responseFormat,
                    Math.Clamp(request.Speed ?? 1.0, 0.25, 4.0),
                    request.Language),
                cancellationToken);
        }
        catch (InferenceException exception)
        {
            var diagnostic = AppendRuntimeDiagnostic(
                conversationId,
                diagnosticsProvider.GetRuntimeFailure(ttsModel!.Id, exception));
            diagnostics.Add(diagnostic);
            return new ConversationVoiceTurnResult(
                new ConversationVoiceTurnResponse(
                    "partial",
                    conversations.Get(conversationId, 1).Conversation,
                    transcript,
                    inputArtifact,
                    turnResult.Response.UserMessage,
                    turnResult.Response.ToolMessage,
                    turnResult.Response.AssistantMessage,
                    null,
                    diagnostics,
                    turnResult.Response,
                    null,
                    null),
                HttpOk);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            var runtimeException = new InferenceException(
                "native_runtime_unavailable",
                $"The TTS native runtime could not be used: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Use /api/runtime/multimodal to inspect backend readiness."
                ],
                exception);
            var diagnostic = AppendRuntimeDiagnostic(
                conversationId,
                diagnosticsProvider.GetRuntimeFailure(ttsModel!.Id, runtimeException));
            diagnostics.Add(diagnostic);
            return new ConversationVoiceTurnResult(
                new ConversationVoiceTurnResponse(
                    "partial",
                    conversations.Get(conversationId, 1).Conversation,
                    transcript,
                    inputArtifact,
                    turnResult.Response.UserMessage,
                    turnResult.Response.ToolMessage,
                    turnResult.Response.AssistantMessage,
                    null,
                    diagnostics,
                    turnResult.Response,
                    null,
                    null),
                HttpOk);
        }

        var speechArtifactResult = await TryRegisterAudioArtifactAsync(
                conversationId,
                speechResult.Bytes,
                speechResult.MediaType,
                "assistant-speech.wav",
                "assistant-speech",
                "conversation.voice-turn.tts",
                cancellationToken)
            .ConfigureAwait(false);

        if (speechArtifactResult.Error is not null)
        {
            diagnostics.AddRange(speechArtifactResult.Error.Response.Diagnostics
                .Where(diagnostic => !diagnostics.Any(existing => existing.Id == diagnostic.Id)));
            return speechArtifactResult.Error with
            {
                Response = speechArtifactResult.Error.Response with
                {
                    Status = "partial",
                    Transcript = transcript,
                    InputArtifact = inputArtifact,
                    UserMessage = turnResult.Response.UserMessage,
                    ToolMessage = turnResult.Response.ToolMessage,
                    AssistantMessage = turnResult.Response.AssistantMessage,
                    Diagnostics = diagnostics,
                    Turn = turnResult.Response
                },
                StatusCode = HttpOk
            };
        }

        diagnostics.Add(AppendRuntimeDiagnostic(
            conversationId,
            new RuntimeDiagnostic(
                "ok",
                "tts_synthesized",
                "Assistant text was synthesized by the local TTS runtime and registered as a conversation artifact.",
                ttsModel!.Id,
                speechResult.Diagnostics.Count == 0
                    ? [
                        $"elapsed_ms: {(long)Math.Round(speechResult.Elapsed.TotalMilliseconds)}",
                        $"sample_rate: {speechResult.SampleRate}"
                    ]
                    : speechResult.Diagnostics)));

        return new ConversationVoiceTurnResult(
            new ConversationVoiceTurnResponse(
                ResolveVoiceStatus(turnResult.Response.Status, diagnostics),
                conversations.Get(conversationId, 1).Conversation,
                transcript,
                inputArtifact,
                turnResult.Response.UserMessage,
                turnResult.Response.ToolMessage,
                turnResult.Response.AssistantMessage,
                speechArtifactResult.Artifact,
                diagnostics,
                turnResult.Response,
                speechResult.MediaType,
                speechResult.Bytes.LongLength),
            turnResult.StatusCode >= HttpServiceUnavailable ? turnResult.StatusCode : HttpOk);
    }

    private async Task<ConversationTurnResult> PersistFailureAsync(
        string conversationId,
        ConversationAppendMessageResponse userAppend,
        string? model,
        string toolMode,
        RuntimeDiagnostic diagnostic,
        int statusCode,
        CancellationToken cancellationToken)
    {
        var diagnosticAppend = conversations.AppendDiagnostic(
            conversationId,
            new ConversationAppendDiagnosticRequest(
                diagnostic.Status,
                diagnostic.Code,
                diagnostic.Message,
                model,
                RuntimeName,
                diagnostic.Actions,
                null));
        await eventLog.WriteErrorAsync(
                "conversation_turn",
                toolMode,
                null,
                RuntimeName,
                model,
                diagnostic,
                cancellationToken)
            .ConfigureAwait(false);

        var response = new ConversationTurnResponse(
            "error",
            diagnosticAppend.Conversation,
            [userAppend.Message],
            userAppend.Message,
            null,
            null,
            [diagnosticAppend.Diagnostic],
            null);
        return new ConversationTurnResult(response, statusCode);
    }

    private async Task<AudioArtifactRegistrationResult> TryRegisterAudioArtifactAsync(
        string conversationId,
        byte[] bytes,
        string mediaType,
        string name,
        string fileNamePrefix,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = conversations.Get(conversationId, 1);
            var path = WriteConversationArtifactBytes(conversationId, bytes, mediaType, name, fileNamePrefix);
            var artifactAppend = conversations.RegisterArtifact(
                conversationId,
                new ConversationRegisterArtifactRequest(
                    "audio",
                    path,
                    mediaType,
                    source,
                    "available",
                    bytes.LongLength,
                    null));
            return new AudioArtifactRegistrationResult(artifactAppend.Conversation, artifactAppend.Artifact, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var conversation = conversations.Get(conversationId, 1).Conversation;
            var diagnostic = new RuntimeDiagnostic(
                "error",
                "artifact_write_failed",
                $"Conversation audio artifact could not be written: {exception.Message}",
                null,
                ["Check filesystem permissions and available disk space for the Tomur data directory."]);
            var response = PersistVoiceFailure(
                conversationId,
                conversation,
                null,
                null,
                null,
                null,
                null,
                diagnostic,
                [],
                HttpServiceUnavailable);
            await eventLog.WriteErrorAsync(
                    "conversation_voice_turn",
                    "artifact",
                    null,
                    RuntimeName,
                    null,
                    diagnostic,
                    cancellationToken)
                .ConfigureAwait(false);
            return new AudioArtifactRegistrationResult(null, null, response);
        }
    }

    private ConversationVoiceTurnResult PersistVoiceFailure(
        string conversationId,
        ConversationRecord conversation,
        string? transcript,
        ConversationArtifactRecord? inputArtifact,
        ConversationMessageRecord? userMessage,
        ConversationMessageRecord? toolMessage,
        ConversationMessageRecord? assistantMessage,
        RuntimeDiagnostic diagnostic,
        IReadOnlyList<ConversationDiagnosticRecord> previousDiagnostics,
        int statusCode)
    {
        var diagnosticAppend = conversations.AppendDiagnostic(
            conversationId,
            new ConversationAppendDiagnosticRequest(
                diagnostic.Status,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Model,
                RuntimeName,
                diagnostic.Actions,
                null));
        var diagnostics = previousDiagnostics
            .Append(diagnosticAppend.Diagnostic)
            .ToArray();
        return new ConversationVoiceTurnResult(
            new ConversationVoiceTurnResponse(
                "error",
                diagnosticAppend.Conversation,
                transcript,
                inputArtifact,
                userMessage,
                toolMessage,
                assistantMessage,
                null,
                diagnostics,
                null,
                null,
                null),
            statusCode);
    }

    private ConversationDiagnosticRecord AppendRuntimeDiagnostic(
        string conversationId,
        RuntimeDiagnostic diagnostic)
    {
        return conversations.AppendDiagnostic(
                conversationId,
                new ConversationAppendDiagnosticRequest(
                    diagnostic.Status,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Model,
                    RuntimeName,
                    diagnostic.Actions,
                    null))
            .Diagnostic;
    }

    private bool TryResolveModelByCapability(
        string? requestedModel,
        string capability,
        string route,
        string backendId,
        out LocalModelDescriptor? model,
        out RuntimeDiagnostic? diagnostic,
        out int statusCode)
    {
        model = null;
        diagnostic = null;
        statusCode = HttpOk;

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            model = modelCatalog.Find(requestedModel);
            if (model is null)
            {
                diagnostic = diagnosticsProvider.GetModelNotDownloaded(requestedModel);
                statusCode = HttpNotFound;
                return false;
            }

            if (!ModelHasCapability(model, capability))
            {
                diagnostic = multimodalExecution.CreateCapabilityMismatchDiagnostic(model.Id, capability, route);
                statusCode = HttpBadRequest;
                return false;
            }

            return true;
        }

        model = modelCatalog.ListModels().FirstOrDefault(item => ModelHasCapability(item, capability));
        if (model is not null)
        {
            return true;
        }

        diagnostic = multimodalExecution.CreateUnavailableDiagnostic(backendId, null);
        statusCode = HttpServiceUnavailable;
        return false;
    }

    private string WriteConversationArtifactBytes(
        string conversationId,
        byte[] bytes,
        string mediaType,
        string name,
        string fileNamePrefix)
    {
        var directory = Path.Combine(paths.DataDirectory, "files", "conversations", SafePathSegment(conversationId));
        Directory.CreateDirectory(directory);
        var extension = ResolveAudioExtension(mediaType, name);
        var fileName = string.Concat(
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            "-",
            SafePathSegment(fileNamePrefix),
            "-",
            Guid.NewGuid().ToString("N"),
            extension);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string ResolveVoiceStatus(
        string turnStatus,
        IReadOnlyList<ConversationDiagnosticRecord> diagnostics)
    {
        if (diagnostics.Any(static diagnostic => string.Equals(diagnostic.Status, "error", StringComparison.OrdinalIgnoreCase)))
        {
            return "partial";
        }

        return string.Equals(turnStatus, "ok", StringComparison.OrdinalIgnoreCase)
            ? "ok"
            : turnStatus;
    }

    private static bool ModelHasCapability(LocalModelDescriptor model, string capability)
        => model.Capabilities.Any(item => string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<AgentChatMessage> BuildAgentMessages(
        IReadOnlyList<ConversationMessageRecord> messages)
        => messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new AgentChatMessage(message.Role, message.Content))
            .ToArray();

    private static string BuildToolMessageContent(IReadOnlyList<AgentChatToolCall> toolCalls)
        => string.Join(
            "\n\n",
            toolCalls.Select(static tool =>
                $"tool:{tool.Tool}\nstatus:{tool.Status}\nelapsed_ms:{tool.ElapsedMs}\nresult:{tool.Result?.GetRawText() ?? "null"}"));

    private static ConversationToolCall ToConversationToolCall(AgentChatToolCall toolCall)
        => new(
            toolCall.Tool,
            toolCall.Status,
            null,
            $"elapsed_ms:{toolCall.ElapsedMs}",
            toolCall.Result,
            null);

    private static bool IsBlockedToolCall(AgentChatToolCall toolCall)
        => string.Equals(toolCall.Status, "blocked", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeContent(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeModality(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "text" : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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

    private static string SafePathSegment(string value)
    {
        var chars = value
            .Where(static item => char.IsAsciiLetterOrDigit(item) || item is '-' or '_' or '.')
            .ToArray();
        return chars.Length == 0 ? "artifact" : new string(chars);
    }

    private static string ResolveAudioExtension(string? mediaType, string? name)
    {
        var normalizedMediaType = mediaType?.Trim().ToLowerInvariant();
        var fromMediaType = normalizedMediaType switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" => ".mp4",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            _ => null
        };

        if (fromMediaType is not null)
        {
            return fromMediaType;
        }

        var extension = Path.GetExtension(name);
        return !string.IsNullOrWhiteSpace(extension) && extension.Length <= 8
            ? extension.ToLowerInvariant()
            : ".wav";
    }

    private static string ResolveToolModeForAudit(
        string? value,
        IReadOnlyList<AgentChatToolRequest>? tools)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return tools is { Count: > 0 } ? "read_only" : "none";
        }

        return value.Trim();
    }

    private static int ResolveInferenceStatusCode(InferenceException exception)
        => exception.Code is
            "invalid_audio" or
            "invalid_request" or
            "tool_not_found" or
            "tool_round_limit_exceeded" or
            "unsupported_audio_format"
            ? HttpBadRequest
            : HttpServiceUnavailable;

    private static bool IsNativeRuntimeException(Exception exception)
        => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private sealed record AudioArtifactRegistrationResult(
        ConversationRecord? Conversation,
        ConversationArtifactRecord? Artifact,
        ConversationVoiceTurnResult? Error);
}

public sealed record ConversationTurnResult(
    ConversationTurnResponse Response,
    int StatusCode);

public sealed record ConversationVoiceTurnResult(
    ConversationVoiceTurnResponse Response,
    int StatusCode);
