using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tomur.Config;
using Tomur.Agents;
using Tomur.Api;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Conversations;

public sealed class ConversationOrchestrationService
{
    private const string RuntimeName = "Microsoft.Agents.AI.ChatClientAgent";
    private const int HttpOk = 200;
    private const int HttpBadRequest = 400;
    private const int HttpNotFound = 404;
    private const int HttpServiceUnavailable = 503;
    private const int DefaultMaxToolRounds = 4;
    private readonly ConversationStore conversations;
    private readonly AgentRuntimeService agentRuntime;
    private readonly ToolInvoker toolInvoker;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;
    private readonly AgentEventLog eventLog;
    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalExecutionService multimodalExecution;
    private readonly DataPaths paths;
    private readonly ILogger<ConversationOrchestrationService> logger;

    public ConversationOrchestrationService(
        ConversationStore conversations,
        AgentRuntimeService agentRuntime,
        ToolInvoker toolInvoker,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        AgentEventLog eventLog,
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution,
        DataPaths paths,
        ILogger<ConversationOrchestrationService> logger)
    {
        this.conversations = conversations;
        this.agentRuntime = agentRuntime;
        this.toolInvoker = toolInvoker;
        this.diagnosticsProvider = diagnosticsProvider;
        this.eventLog = eventLog;
        this.modelCatalog = modelCatalog;
        this.multimodalExecution = multimodalExecution;
        this.paths = paths;
        this.logger = logger;
    }

    public async Task<ConversationTurnResult> RunTurnAsync(
        string conversationId,
        ConversationTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(request);

        var content = NormalizeContent(request.Content);
        var attachments = request.Attachments ?? [];
        if (content is null && attachments.Count == 0)
        {
            throw new ConversationStoreException(
                "error",
                "invalid_request",
                "Conversation turns require text content or at least one local attachment.",
                [
                    "Send text content for this turn.",
                    "Attach an image, audio recording or local file reference when using multimodal turn orchestration."
                ]);
        }

        var diagnostics = new List<ConversationDiagnosticRecord>();
        var artifacts = new List<ConversationArtifactRecord>();
        var attachmentPreparation = await PrepareAttachmentsAsync(
                conversationId,
                attachments,
                cancellationToken)
            .ConfigureAwait(false);
        diagnostics.AddRange(attachmentPreparation.Diagnostics);
        artifacts.AddRange(attachmentPreparation.Artifacts);

        var userAppend = conversations.AppendMessage(
            conversationId,
            new ConversationAppendMessageRequest(
                "user",
                content ?? BuildAttachmentOnlyContent(attachmentPreparation.NormalizedAttachments),
                NormalizeModality(request.Modality),
                "ok",
                request.Model,
                attachmentPreparation.NormalizedAttachments,
                null,
                artifacts.Select(static artifact => artifact.Id).ToArray(),
                request.Metadata));
        var messages = new List<ConversationMessageRecord>
        {
            userAppend.Message
        };
        var model = request.Model ?? userAppend.Message.Model ?? userAppend.Conversation.Model;
        var toolMode = ResolveToolModeForAudit(request.ToolMode, request.Tools);
        var plannedTools = new List<AgentChatToolRequest>();
        var planningText = content ?? string.Empty;

        try
        {
            plannedTools.AddRange(attachmentPreparation.Tools);

            plannedTools.AddRange(PlanTurnTools(
                request,
                planningText,
                attachmentPreparation.NormalizedAttachments));

            var maxToolRounds = Math.Clamp(
                request.MaxToolRounds ?? DefaultMaxToolRounds,
                1,
                DefaultMaxToolRounds);
            var requestedToolCount = (request.Tools?.Count ?? 0) + plannedTools.Count;
            if (requestedToolCount > maxToolRounds)
            {
                diagnostics.Add(AppendRuntimeDiagnostic(
                    conversationId,
                    new RuntimeDiagnostic(
                        "warning",
                        "tool_round_limit_applied",
                        $"This turn planned {requestedToolCount} tool calls, but only {maxToolRounds} can run in the current local boundary.",
                        model,
                        ["Reduce attachments or set max_tool_rounds up to the local limit of 4."])));
            }

            var effectiveToolMode = ResolveEffectiveToolMode(request.ToolMode, request.Tools, plannedTools);
            var effectiveTools = MergeToolRequests(request.Tools, plannedTools, maxToolRounds);
            if (content is null && (effectiveTools is null || effectiveTools.Count == 0))
            {
                IReadOnlyList<ConversationDiagnosticRecord> responseDiagnostics = diagnostics.Count == 0
                    ? new[]
                    {
                        AppendRuntimeDiagnostic(
                            conversationId,
                            new RuntimeDiagnostic(
                                "error",
                                "attachment_processing_unavailable",
                                "No attachment could be prepared for this conversation turn.",
                                model,
                                ["Send a valid image, audio or local file attachment, or include text content."]))
                    }
                    : diagnostics;
                var errorResponse = new ConversationTurnResponse(
                    "error",
                    userAppend.Conversation,
                    messages,
                    userAppend.Message,
                    null,
                    null,
                    responseDiagnostics,
                    artifacts,
                    null,
                    null,
                    null,
                    null);
                return new ConversationTurnResult(errorResponse, HttpBadRequest);
            }

            var recentMessages = conversations.ListRecentMessages(conversationId, request.HistoryLimit);
            var agentRequest = new AgentChatRequest(
                model,
                null,
                BuildAgentMessages(recentMessages),
                null,
                effectiveToolMode,
                effectiveTools,
                ResolveMaxToolRounds(request.MaxToolRounds, effectiveTools),
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

            var toolArtifacts = RegisterToolArtifacts(conversationId, agentResponse.ToolCalls);
            foreach (var artifact in toolArtifacts)
            {
                artifacts.Add(artifact);
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
                        ResolveMessageArtifactIds(agentResponse.ToolCalls, toolArtifacts),
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
                        ["Inspect GET /api/agents/tools; model-selected side-effect tools require an explicit allowlist and confirmation, while dedicated endpoints remain available for manual execution."],
                        null));
                diagnostics.Add(diagnosticAppend.Diagnostic);
            }

            foreach (var diagnostic in AppendFailedToolDiagnostics(conversationId, agentResponse))
            {
                diagnostics.Add(diagnostic);
            }

            ConversationArtifactRecord? speechArtifact = null;
            string? speechMediaType = null;
            long? speechBytes = null;
            if (IsSpeechRequested(request, content) && request.Confirm != true)
            {
                diagnostics.Add(AppendRuntimeDiagnostic(
                    conversationId,
                    new RuntimeDiagnostic(
                        "warning",
                        "tool_requires_confirmation",
                        "Conversation speech output generates a local audio artifact and requires confirm=true.",
                        request.TtsModel,
                        ["Set confirm=true when the user explicitly approves local speech synthesis."])));
            }

            if (ShouldSpeak(request, content, agentResponse.ToolCalls) &&
                assistantAppend is not null &&
                !string.IsNullOrWhiteSpace(assistantAppend.Message.Content))
            {
                var speech = TrySynthesizeTurnSpeech(
                    conversationId,
                    request,
                    assistantAppend.Message.Content,
                    diagnostics,
                    cancellationToken);
                speechArtifact = speech.Artifact;
                speechMediaType = speech.MediaType;
                speechBytes = speech.Bytes;
                if (speech.Artifact is not null)
                {
                    artifacts.Add(speech.Artifact);
                }
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
                artifacts,
                speechArtifact,
                speechMediaType,
                speechBytes,
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
                    diagnostics,
                    artifacts,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            logger.TurnNativeRuntimeUnavailable(exception);
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
                    diagnostics,
                    artifacts,
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
                asrModel,
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
                diagnosticsProvider.GetRuntimeFailure(asrModel.Id, exception),
                diagnostics,
                ResolveInferenceStatusCode(exception));
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            logger.TurnNativeRuntimeUnavailable(exception);
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
                diagnosticsProvider.GetRuntimeFailure(asrModel.Id, runtimeException),
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
                asrModel.Id,
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
                    "artifact",
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
            request.Metadata,
            Confirm: request.Speak ?? true,
            Speak: false,
            Voice: request.Voice,
            TtsModel: request.TtsModel,
            ResponseFormat: request.ResponseFormat,
            Speed: request.Speed,
            Language: request.Language);

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
                    ttsModel.Id,
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
                ttsModel,
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
                diagnosticsProvider.GetRuntimeFailure(ttsModel.Id, exception));
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
            logger.TurnNativeRuntimeUnavailable(exception);
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
                diagnosticsProvider.GetRuntimeFailure(ttsModel.Id, runtimeException));
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
                ttsModel.Id,
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
        IReadOnlyList<ConversationDiagnosticRecord> previousDiagnostics,
        IReadOnlyList<ConversationArtifactRecord> artifacts,
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

        var responseDiagnostics = previousDiagnostics
            .Append(diagnosticAppend.Diagnostic)
            .ToArray();
        var response = new ConversationTurnResponse(
            "error",
            diagnosticAppend.Conversation,
            [userAppend.Message],
            userAppend.Message,
            null,
            null,
            responseDiagnostics,
            artifacts,
            null,
            null,
            null,
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
            logger.TurnFileOperationFailed(exception.Message);
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

    private async Task<PreparedAttachmentPlan> PrepareAttachmentsAsync(
        string conversationId,
        IReadOnlyList<ConversationAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var normalized = new List<ConversationAttachment>(attachments.Count);
        var artifacts = new List<ConversationArtifactRecord>();
        var diagnostics = new List<ConversationDiagnosticRecord>();
        var tools = new List<AgentChatToolRequest>();
        _ = conversations.Get(conversationId, 1);

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = NormalizeAttachmentType(attachment);
            switch (kind)
            {
                case "image":
                    PrepareImageAttachment(conversationId, attachment, normalized, artifacts, diagnostics, tools);
                    break;
                case "audio":
                    PrepareAudioAttachment(conversationId, attachment, normalized, artifacts, diagnostics, tools);
                    break;
                case "file":
                    PrepareFileAttachment(conversationId, attachment, normalized, artifacts, diagnostics, tools);
                    break;
                case "reference":
                    normalized.Add(attachment);
                    break;
                default:
                    normalized.Add(attachment);
                    diagnostics.Add(AppendRuntimeDiagnostic(
                        conversationId,
                        new RuntimeDiagnostic(
                            "warning",
                            "attachment_type_unsupported",
                            $"Attachment type '{kind}' is recorded but not orchestrated in this R10 boundary.",
                            null,
                            ["Use type image, audio or file for conversation turn orchestration."])));
                    break;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new PreparedAttachmentPlan(normalized, artifacts, diagnostics, tools);
    }

    private void PrepareImageAttachment(
        string conversationId,
        ConversationAttachment attachment,
        List<ConversationAttachment> normalized,
        List<ConversationArtifactRecord> artifacts,
        List<ConversationDiagnosticRecord> diagnostics,
        List<AgentChatToolRequest> tools)
    {
        if (!TryResolveAttachmentBytes(
                attachment,
                fallbackMediaType: "image/png",
                maxBytes: CompatibilityProtocolLimits.MaxImageBytes,
                out var bytes,
                out var mediaType,
                out var diagnostic))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(conversationId, diagnostic!));
            normalized.Add(attachment);
            return;
        }

        if (!TryRegisterBinaryArtifact(
                conversationId,
                bytes,
                mediaType ?? "image/png",
                attachment.Name ?? "image.png",
                "image",
                "conversation.attachment.image",
                "image",
                out var artifact,
                out var writeDiagnostic))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(conversationId, writeDiagnostic!));
            normalized.Add(attachment);
            return;
        }

        var registeredArtifact = artifact!;
        artifacts.Add(registeredArtifact);
        normalized.Add(ToAttachment(attachment, registeredArtifact));
        tools.Add(new AgentChatToolRequest(
            ShouldUseOcr(attachment)
                ? "ocr.recognize"
                : "vision.analyze",
            ShouldUseOcr(attachment)
                ? CreateOcrArguments(attachment, registeredArtifact, bytes, mediaType)
                : CreateVisionArguments(attachment, registeredArtifact, bytes, mediaType)));
    }

    private void PrepareAudioAttachment(
        string conversationId,
        ConversationAttachment attachment,
        List<ConversationAttachment> normalized,
        List<ConversationArtifactRecord> artifacts,
        List<ConversationDiagnosticRecord> diagnostics,
        List<AgentChatToolRequest> tools)
    {
        if (!TryResolveAttachmentBytes(
                attachment,
                fallbackMediaType: "audio/wav",
                maxBytes: CompatibilityProtocolLimits.MaxAudioBytes,
                out var bytes,
                out var mediaType,
                out var diagnostic))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(conversationId, diagnostic!));
            normalized.Add(attachment);
            return;
        }

        if (!TryRegisterBinaryArtifact(
                conversationId,
                bytes,
                mediaType ?? "audio/wav",
                attachment.Name ?? "audio.wav",
                "audio",
                "conversation.attachment.audio",
                "audio",
                out var artifact,
                out var writeDiagnostic))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(conversationId, writeDiagnostic!));
            normalized.Add(attachment);
            return;
        }

        var registeredArtifact = artifact!;
        artifacts.Add(registeredArtifact);
        normalized.Add(ToAttachment(attachment, registeredArtifact));
        tools.Add(new AgentChatToolRequest(
            "audio.transcribe",
            CreateAudioTranscriptionArguments(attachment, bytes, mediaType)));
    }

    private void PrepareFileAttachment(
        string conversationId,
        ConversationAttachment attachment,
        List<ConversationAttachment> normalized,
        List<ConversationArtifactRecord> artifacts,
        List<ConversationDiagnosticRecord> diagnostics,
        List<AgentChatToolRequest> tools)
    {
        var filesRoot = Path.GetFullPath(Path.Combine(paths.DataDirectory, "files"));
        Directory.CreateDirectory(filesRoot);
        var path = NormalizeOptional(attachment.Path);
        var inlineText = NormalizeOptional(attachment.Text) ?? NormalizeOptional(attachment.Content);
        if (!string.IsNullOrWhiteSpace(inlineText))
        {
            var bytes = Encoding.UTF8.GetBytes(inlineText);
            if (!TryRegisterBinaryArtifact(
                    conversationId,
                    bytes,
                    attachment.MediaType ?? "text/plain",
                    attachment.Name ?? "attachment.txt",
                    "file",
                    "conversation.attachment.file",
                    "file",
                    out var artifact,
                    out var writeDiagnostic))
            {
                diagnostics.Add(AppendRuntimeDiagnostic(conversationId, writeDiagnostic!));
                normalized.Add(attachment);
                return;
            }

            var registeredArtifact = artifact!;
            artifacts.Add(registeredArtifact);
            normalized.Add(ToAttachment(attachment, registeredArtifact));
            tools.Add(new AgentChatToolRequest(
                "files.search",
                CreateFileSearchArguments(path: Path.GetDirectoryName(registeredArtifact.Path), query: BuildFileSearchQuery(attachment))));
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "warning",
                    "file_attachment_missing_content",
                    "File attachments require inline text/content or a path under the Tomur managed files directory.",
                    null,
                    [$"Place files under: {filesRoot}"])));
            normalized.Add(attachment);
            return;
        }

        var absolutePath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(filesRoot, path));
        if (!IsWithinRoot(absolutePath, filesRoot))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "warning",
                    "file_attachment_outside_data_dir",
                    "File attachment paths must stay under the Tomur managed files directory.",
                    null,
                    [$"Move the file under: {filesRoot}"])));
            normalized.Add(attachment);
            return;
        }

        if (!File.Exists(absolutePath))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "warning",
                    "file_attachment_not_found",
                    $"File attachment was not found: {absolutePath}",
                    null,
                    ["Check the file path and retry the conversation turn."])));
            normalized.Add(attachment);
            return;
        }

        var info = new FileInfo(absolutePath);
        var artifactAppend = conversations.RegisterArtifact(
            conversationId,
            new ConversationRegisterArtifactRequest(
                "file",
                absolutePath,
                attachment.MediaType ?? ResolveFileMediaType(info.Extension),
                "conversation.attachment.file",
                "available",
                info.Length,
                null));
        artifacts.Add(artifactAppend.Artifact);
        normalized.Add(ToAttachment(attachment, artifactAppend.Artifact));
        tools.Add(new AgentChatToolRequest(
            "files.search",
            CreateFileSearchArguments(Path.GetDirectoryName(absolutePath), BuildFileSearchQuery(attachment))));
    }

    private IReadOnlyList<AgentChatToolRequest> PlanTurnTools(
        ConversationTurnRequest request,
        string content,
        IReadOnlyList<ConversationAttachment> attachments)
    {
        var planned = new List<AgentChatToolRequest>();
        if (MentionsImageGeneration(content))
        {
            planned.Add(new AgentChatToolRequest(
                "image.generate",
                CreateImageGenerationArguments(request, content))
            {
                Confirm = request.Confirm
            });
        }

        if (MentionsFileSearch(content) &&
            attachments.All(static attachment => !string.Equals(NormalizeAttachmentType(attachment), "file", StringComparison.OrdinalIgnoreCase)))
        {
            planned.Add(new AgentChatToolRequest(
                "files.search",
                CreateFileSearchArguments(null, CreateQuery(content))));
        }

        return planned;
    }

    private IReadOnlyList<ConversationArtifactRecord> RegisterToolArtifacts(
        string conversationId,
        IReadOnlyList<AgentChatToolCall> toolCalls)
    {
        var artifacts = new List<ConversationArtifactRecord>();
        foreach (var toolCall in toolCalls)
        {
            if (!TryReadToolArtifact(toolCall.Result, out var artifact))
            {
                continue;
            }

            var artifactAppend = conversations.RegisterArtifact(
                conversationId,
                new ConversationRegisterArtifactRequest(
                    artifact.Type,
                    artifact.Path,
                    artifact.MediaType,
                    $"agent-tool:{toolCall.Tool}",
                    "available",
                    artifact.Bytes,
                    null));
            artifacts.Add(artifactAppend.Artifact);
        }

        return artifacts;
    }

    private IReadOnlyList<ConversationDiagnosticRecord> AppendFailedToolDiagnostics(
        string conversationId,
        AgentChatResponse agentResponse)
    {
        var diagnostics = new List<ConversationDiagnosticRecord>();
        foreach (var toolCall in agentResponse.ToolCalls.Where(static tool => IsNonOkStatus(tool.Status)))
        {
            var code = TryReadToolDiagnosticCode(toolCall.Result) ??
                (IsBlockedToolCall(toolCall) ? "tool_call_blocked" : "tool_call_failed");
            var message = TryReadToolDiagnosticMessage(toolCall.Result) ??
                $"Tool '{toolCall.Tool}' returned status '{toolCall.Status}'.";
            diagnostics.Add(conversations.AppendDiagnostic(
                    conversationId,
                    new ConversationAppendDiagnosticRequest(
                        IsBlockedToolCall(toolCall) ? "warning" : "error",
                        code,
                        message,
                        agentResponse.Model,
                        toolCall.Tool,
                        toolCall.Diagnostics.Count > 0
                            ? toolCall.Diagnostics
                            : ["Inspect the tool result recorded on the conversation tool message."],
                        null))
                .Diagnostic);
        }

        return diagnostics;
    }

    private SpeechTurnResult TrySynthesizeTurnSpeech(
        string conversationId,
        ConversationTurnRequest request,
        string text,
        List<ConversationDiagnosticRecord> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!TryResolveModelByCapability(
                request.TtsModel,
                "audio-output",
                "/api/conversations/{conversationId}/turns",
                "tts",
                out var ttsModel,
                out var ttsDiagnostic,
                out _))
        {
            diagnostics.Add(AppendRuntimeDiagnostic(conversationId, ttsDiagnostic!));
            return new SpeechTurnResult(null, null, null);
        }

        var responseFormat = NormalizeSpeechResponseFormat(request.ResponseFormat);
        if (responseFormat is null)
        {
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "warning",
                    "invalid_request",
                    "The conversation turn response_format currently supports only 'wav'. Text response was still persisted.",
                    ttsModel.Id,
                    ["Set response_format to wav or omit it."])));
            return new SpeechTurnResult(null, null, null);
        }

        try
        {
            var speechResult = multimodalExecution.SynthesizeSpeech(
                ttsModel,
                new SpeechSynthesisOptions(
                    text,
                    request.Voice,
                    responseFormat,
                    Math.Clamp(request.Speed ?? 1.0, 0.25, 4.0),
                    request.Language),
                cancellationToken);
            var artifact = RegisterBinaryArtifact(
                conversationId,
                speechResult.Bytes,
                speechResult.MediaType,
                "assistant-speech.wav",
                "audio",
                "conversation.turn.tts",
                "assistant-speech");
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                new RuntimeDiagnostic(
                    "ok",
                    "tts_synthesized",
                    "Assistant text was synthesized by the local TTS runtime and registered as a conversation artifact.",
                    ttsModel.Id,
                    speechResult.Diagnostics.Count == 0
                        ? [
                            $"elapsed_ms: {(long)Math.Round(speechResult.Elapsed.TotalMilliseconds)}",
                            $"sample_rate: {speechResult.SampleRate}"
                        ]
                        : speechResult.Diagnostics)));
            return new SpeechTurnResult(artifact, speechResult.MediaType, speechResult.Bytes.LongLength);
        }
        catch (InferenceException exception)
        {
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                diagnosticsProvider.GetRuntimeFailure(ttsModel.Id, exception)));
            return new SpeechTurnResult(null, null, null);
        }
        catch (Exception exception) when (IsNativeRuntimeException(exception))
        {
            logger.TurnNativeRuntimeUnavailable(exception);
            var runtimeException = new InferenceException(
                "native_runtime_unavailable",
                $"The TTS native runtime could not be used: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Use /api/runtime/multimodal to inspect backend readiness."
                ],
                exception);
            diagnostics.Add(AppendRuntimeDiagnostic(
                conversationId,
                diagnosticsProvider.GetRuntimeFailure(ttsModel.Id, runtimeException)));
            return new SpeechTurnResult(null, null, null);
        }
    }

    private bool TryResolveModelByCapability(
        string? requestedModel,
        string capability,
        string route,
        string backendId,
        [NotNullWhen(true)] out LocalModelDescriptor? model,
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
        var extension = ResolveArtifactExtension(mediaType, name);
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

    private ConversationArtifactRecord RegisterBinaryArtifact(
        string conversationId,
        byte[] bytes,
        string mediaType,
        string name,
        string type,
        string source,
        string fileNamePrefix)
    {
        var path = WriteConversationArtifactBytes(conversationId, bytes, mediaType, name, fileNamePrefix);
        return conversations.RegisterArtifact(
                conversationId,
                new ConversationRegisterArtifactRequest(
                    type,
                    path,
                    mediaType,
                    source,
                    "available",
                    bytes.LongLength,
                    null))
            .Artifact;
    }

    private bool TryRegisterBinaryArtifact(
        string conversationId,
        byte[] bytes,
        string mediaType,
        string name,
        string type,
        string source,
        string fileNamePrefix,
        out ConversationArtifactRecord? artifact,
        out RuntimeDiagnostic? diagnostic)
    {
        artifact = null;
        diagnostic = null;
        try
        {
            artifact = RegisterBinaryArtifact(
                conversationId,
                bytes,
                mediaType,
                name,
                type,
                source,
                fileNamePrefix);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.TurnFileOperationFailed(exception.Message);
            diagnostic = new RuntimeDiagnostic(
                "error",
                "artifact_write_failed",
                $"Conversation artifact could not be written: {exception.Message}",
                null,
                ["Check filesystem permissions and available disk space for the Tomur data directory."]);
            return false;
        }
    }

    private bool TryResolveAttachmentBytes(
        ConversationAttachment attachment,
        string fallbackMediaType,
        long maxBytes,
        out byte[] bytes,
        out string? mediaType,
        out RuntimeDiagnostic? diagnostic)
    {
        bytes = [];
        mediaType = NormalizeOptional(attachment.MediaType) ?? fallbackMediaType;
        diagnostic = null;

        var source = NormalizeOptional(attachment.DataUri) ?? NormalizeOptional(attachment.Base64);
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!TryDecodeInlineBytes(source, mediaType, maxBytes, out bytes, out mediaType, out var error))
            {
                diagnostic = new RuntimeDiagnostic(
                    "warning",
                    "attachment_decode_failed",
                    error,
                    null,
                    ["Send attachments as base64 or data URI payloads within the supported local size limits."]);
                return false;
            }

            return true;
        }

        var path = NormalizeOptional(attachment.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostic = new RuntimeDiagnostic(
                "warning",
                "attachment_missing_payload",
                "Attachment requires a data_uri, base64 payload or local path.",
                null,
                ["Provide a local attachment payload or path before running this turn."]);
            return false;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            diagnostic = new RuntimeDiagnostic(
                "warning",
                "remote_attachment_not_fetched",
                "Remote attachment URLs are not fetched by local conversation orchestration.",
                null,
                ["Send a data URI/base64 payload or register a local file under the Tomur data directory."]);
            return false;
        }

        try
        {
            var absolutePath = Path.GetFullPath(path);
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
            {
                diagnostic = new RuntimeDiagnostic(
                    "warning",
                    "attachment_file_not_found",
                    $"Attachment file was not found: {absolutePath}",
                    null,
                    ["Check the file path and retry the conversation turn."]);
                return false;
            }

            if (info.Length <= 0 || info.Length > maxBytes)
            {
                diagnostic = new RuntimeDiagnostic(
                    "warning",
                    "attachment_size_invalid",
                    $"Attachment size must be between 1 and {maxBytes} bytes.",
                    null,
                    ["Send a smaller local attachment."]);
                return false;
            }

            bytes = File.ReadAllBytes(absolutePath);
            mediaType = NormalizeOptional(attachment.MediaType) ?? ResolveMediaTypeFromPath(absolutePath) ?? fallbackMediaType;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.TurnFileOperationFailed(exception.Message);
            diagnostic = new RuntimeDiagnostic(
                "warning",
                "attachment_read_failed",
                $"Attachment file could not be read: {exception.Message}",
                null,
                ["Check local file permissions and retry the conversation turn."]);
            return false;
        }
    }

    private static bool TryDecodeInlineBytes(
        string source,
        string? fallbackMediaType,
        long maxBytes,
        out byte[] bytes,
        out string? mediaType,
        out string error)
    {
        bytes = [];
        mediaType = fallbackMediaType;
        error = string.Empty;
        var payload = source.Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                error = "Data URI is missing the payload separator.";
                return false;
            }

            var metadata = payload[5..comma];
            if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                error = "Data URI attachments must use base64 encoding.";
                return false;
            }

            mediaType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static item => item.Contains('/', StringComparison.Ordinal))
                ?? fallbackMediaType;
            payload = payload[(comma + 1)..];
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            if (bytes.LongLength <= 0)
            {
                error = "Attachment payload is empty.";
                return false;
            }

            if (bytes.LongLength > maxBytes)
            {
                bytes = [];
                error = $"Attachment payload is too large. Limit: {maxBytes} bytes.";
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            error = "Attachment payload is not valid base64.";
            return false;
        }
    }

    private static JsonElement CreateVisionArguments(
        ConversationAttachment attachment,
        ConversationArtifactRecord artifact,
        byte[] bytes,
        string? mediaType)
        => JsonSerializer.SerializeToElement(
            new ConversationVisionToolArguments(
                NormalizeOptional(attachment.Text) ??
                    NormalizeOptional(attachment.Content) ??
                    "Describe the image and answer the user's request.",
                [
                    new ConversationImageToolInput(
                        ToDataUri(mediaType ?? artifact.MediaType ?? "image/png", bytes),
                        mediaType ?? artifact.MediaType,
                        TryGetMetadataString(attachment.Metadata, "detail"))
                ]),
            AppJsonSerializerContext.Default.ConversationVisionToolArguments);

    private static JsonElement CreateOcrArguments(
        ConversationAttachment attachment,
        ConversationArtifactRecord artifact,
        byte[] bytes,
        string? mediaType)
        => JsonSerializer.SerializeToElement(
            new ConversationOcrToolArguments(
                new ConversationImageToolInput(
                    ToDataUri(mediaType ?? artifact.MediaType ?? "image/png", bytes),
                    mediaType ?? artifact.MediaType,
                    null),
                TryGetMetadataString(attachment.Metadata, "language"),
                NormalizeOptional(attachment.Text) ?? NormalizeOptional(attachment.Content)),
            AppJsonSerializerContext.Default.ConversationOcrToolArguments);

    private static JsonElement CreateAudioTranscriptionArguments(
        ConversationAttachment attachment,
        byte[] bytes,
        string? mediaType)
        => JsonSerializer.SerializeToElement(
            new ConversationAudioTranscriptionToolArguments(
                ToDataUri(mediaType ?? attachment.MediaType ?? "audio/wav", bytes),
                mediaType ?? attachment.MediaType,
                TryGetMetadataString(attachment.Metadata, "language")),
            AppJsonSerializerContext.Default.ConversationAudioTranscriptionToolArguments);

    private static JsonElement CreateFileSearchArguments(string? path, string query)
        => JsonSerializer.SerializeToElement(
            new FileSearchToolArguments(query, path, 5, true, null, null),
            AppJsonSerializerContext.Default.FileSearchToolArguments);

    private static JsonElement CreateImageGenerationArguments(ConversationTurnRequest request, string content)
        => JsonSerializer.SerializeToElement(
            new ConversationImageGenerationToolArguments(
                CreatePromptFromText(content),
                TryGetMetadataString(request.Metadata, "size") ?? "1024x1024",
                request.Model),
            AppJsonSerializerContext.Default.ConversationImageGenerationToolArguments);

    private static IReadOnlyList<string>? ResolveMessageArtifactIds(
        IReadOnlyList<AgentChatToolCall> toolCalls,
        IReadOnlyList<ConversationArtifactRecord> artifacts)
    {
        if (artifacts.Count == 0 && !toolCalls.Any(static tool => TryReadToolArtifact(tool.Result, out _)))
        {
            return null;
        }

        return artifacts.Select(static artifact => artifact.Id).ToArray();
    }

    private static bool TryReadToolArtifact(JsonElement? result, out AgentToolArtifact artifact)
    {
        artifact = new AgentToolArtifact(string.Empty, string.Empty, null, null, 0, null);
        if (result is null || result.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!result.Value.TryGetProperty("artifact", out var artifactElement) ||
            artifactElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var path = TryGetString(artifactElement, "path");
        var type = TryGetString(artifactElement, "type");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        artifact = new AgentToolArtifact(
            type!,
            path!,
            TryGetString(artifactElement, "media_type"),
            TryGetString(artifactElement, "format"),
            TryGetLong(artifactElement, "bytes") ?? 0,
            TryGetInt(artifactElement, "sample_rate"));
        return true;
    }

    private static string? TryReadToolDiagnosticCode(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (result.Value.TryGetProperty("diagnostic", out var diagnostic) &&
            diagnostic.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(diagnostic, "code");
        }

        return TryGetString(result.Value, "code");
    }

    private static string? TryReadToolDiagnosticMessage(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (result.Value.TryGetProperty("diagnostic", out var diagnostic) &&
            diagnostic.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(diagnostic, "message");
        }

        return TryGetString(result.Value, "message");
    }

    private static string ToDataUri(string mediaType, byte[] bytes)
        => $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

    private static string BuildAttachmentOnlyContent(IReadOnlyList<ConversationAttachment> attachments)
        => attachments.Count == 0
            ? string.Empty
            : $"Process {attachments.Count} local attachment(s).";

    private static ConversationAttachment ToAttachment(
        ConversationAttachment source,
        ConversationArtifactRecord artifact)
        => source with
        {
            Id = artifact.Id,
            Type = artifact.Type,
            MediaType = artifact.MediaType ?? source.MediaType,
            Path = artifact.Path,
            Bytes = artifact.Bytes,
            DataUri = null,
            Base64 = null
        };

    private static bool ShouldUseOcr(ConversationAttachment attachment)
    {
        var text = string.Join(
            " ",
            NormalizeOptional(attachment.Text),
            NormalizeOptional(attachment.Content),
            TryGetMetadataString(attachment.Metadata, "mode"));
        return ContainsAny(text, "ocr", "文字", "识别文字", "提取文字", "read text", "extract text");
    }

    private static bool ShouldSpeak(
        ConversationTurnRequest request,
        string? content,
        IReadOnlyList<AgentChatToolCall> toolCalls)
    {
        if (request.Speak is not null)
        {
            return request.Speak.Value;
        }

        if (toolCalls.Any(static call => string.Equals(call.Tool, "audio.speak", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(call.Status, "ok", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return IsSpeechRequested(request, content) && request.Confirm == true;
    }

    private static bool IsSpeechRequested(ConversationTurnRequest request, string? content)
        => request.Speak == true || (request.Speak is null && MentionsSpeechOutput(content ?? string.Empty));

    private static string ResolveEffectiveToolMode(
        string? requestedToolMode,
        IReadOnlyList<AgentChatToolRequest>? explicitTools,
        IReadOnlyList<AgentChatToolRequest> plannedTools)
    {
        if (plannedTools.Any(IsControlledTool))
        {
            return "controlled";
        }

        if (!string.IsNullOrWhiteSpace(requestedToolMode))
        {
            return requestedToolMode;
        }

        if (explicitTools is { Count: > 0 } && explicitTools.Any(IsControlledTool))
        {
            return "controlled";
        }

        return explicitTools is { Count: > 0 } || plannedTools.Count > 0
            ? "read_only"
            : "none";
    }

    private static IReadOnlyList<AgentChatToolRequest>? MergeToolRequests(
        IReadOnlyList<AgentChatToolRequest>? explicitTools,
        IReadOnlyList<AgentChatToolRequest> plannedTools,
        int maxToolRounds)
    {
        var merged = new List<AgentChatToolRequest>();
        if (explicitTools is { Count: > 0 })
        {
            merged.AddRange(explicitTools);
        }

        merged.AddRange(plannedTools);
        if (merged.Count == 0)
        {
            return null;
        }

        return merged
            .Where(static tool => !string.IsNullOrWhiteSpace(tool.Tool))
            .Take(Math.Clamp(maxToolRounds, 1, DefaultMaxToolRounds))
            .ToArray();
    }

    private static int? ResolveMaxToolRounds(
        int? requestedMaxToolRounds,
        IReadOnlyList<AgentChatToolRequest>? tools)
    {
        if (requestedMaxToolRounds is > 0)
        {
            return requestedMaxToolRounds;
        }

        return tools is { Count: > 0 }
            ? Math.Clamp(tools.Count, 1, DefaultMaxToolRounds)
            : null;
    }

    private static bool IsControlledTool(AgentChatToolRequest tool)
        => tool.Tool is "image.generate" or "vision.analyze" or "ocr.recognize" or "audio.transcribe" or "audio.speak" or "runtime.repair";

    private static bool IsNonOkStatus(string status)
        => !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAttachmentType(ConversationAttachment attachment)
    {
        var explicitType = NormalizeOptional(attachment.Type)?.ToLowerInvariant();
        if (explicitType is "image" or "audio" or "file")
        {
            return explicitType;
        }

        if (explicitType is "artifact" or "reference")
        {
            return "reference";
        }

        var mediaType = NormalizeOptional(attachment.MediaType)?.ToLowerInvariant();
        if (mediaType?.StartsWith("image/", StringComparison.Ordinal) == true)
        {
            return "image";
        }

        if (mediaType?.StartsWith("audio/", StringComparison.Ordinal) == true)
        {
            return "audio";
        }

        return "file";
    }

    private static bool MentionsImageGeneration(string value)
        => ContainsAny(
            value,
            "generate image",
            "create image",
            "draw",
            "make a picture",
            "生成图片",
            "生成一张图",
            "画一张",
            "出图");

    private static bool MentionsSpeechOutput(string value)
        => ContainsAny(
            value,
            "read aloud",
            "speak",
            "tts",
            "voice reply",
            "朗读",
            "读出来",
            "语音回复",
            "合成语音");

    private static bool MentionsFileSearch(string value)
        => ContainsAny(
            value,
            "file",
            "files",
            "document",
            "documents",
            "rag",
            "search",
            "lookup",
            "local knowledge",
            "文件",
            "文档",
            "资料",
            "检索",
            "搜索",
            "问答");

    private static bool ContainsAny(string value, params string[] terms)
        => !string.IsNullOrWhiteSpace(value) &&
            terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string CreatePromptFromText(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "Generate an image." : normalized;
    }

    private static string CreateQuery(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > 240 ? normalized[..240] : normalized;
    }

    private static string BuildFileSearchQuery(ConversationAttachment attachment)
    {
        var text = NormalizeOptional(attachment.Text) ??
            NormalizeOptional(attachment.Content) ??
            NormalizeOptional(attachment.Name) ??
            "local file attachment";
        return CreateQuery(text);
    }

    private static string? TryGetMetadataString(JsonElement? metadata, string propertyName)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(metadata.Value, propertyName);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? NormalizeOptional(property.GetString())
            : null;

    private static int? TryGetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
                ? value
                : null;

    private static long? TryGetLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out var value)
                ? value
                : null;

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string? ResolveMediaTypeFromPath(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            ".md" or ".markdown" => "text/markdown",
            ".json" or ".jsonl" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            ".txt" or ".log" => "text/plain",
            _ => null
        };

    private static string ResolveFileMediaType(string extension)
        => extension.Trim().ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "text/markdown",
            ".json" or ".jsonl" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            _ => "text/plain"
        };

    private static string ResolveArtifactExtension(string? mediaType, string? name)
    {
        var normalizedMediaType = mediaType?.Trim().ToLowerInvariant();
        var fromMediaType = normalizedMediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" => ".mp4",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "text/markdown" => ".md",
            "application/json" => ".json",
            "text/csv" => ".csv",
            "text/plain" => ".txt",
            _ => null
        };

        if (fromMediaType is not null)
        {
            return fromMediaType;
        }

        var extension = Path.GetExtension(name);
        return !string.IsNullOrWhiteSpace(extension) && extension.Length <= 10
            ? extension.ToLowerInvariant()
            : ".bin";
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

    private sealed record PreparedAttachmentPlan(
        IReadOnlyList<ConversationAttachment> NormalizedAttachments,
        IReadOnlyList<ConversationArtifactRecord> Artifacts,
        IReadOnlyList<ConversationDiagnosticRecord> Diagnostics,
        IReadOnlyList<AgentChatToolRequest> Tools);

    private sealed record SpeechTurnResult(
        ConversationArtifactRecord? Artifact,
        string? MediaType,
        long? Bytes);
}

public sealed record ConversationTurnResult(
    ConversationTurnResponse Response,
    int StatusCode);

public sealed record ConversationVoiceTurnResult(
    ConversationVoiceTurnResponse Response,
    int StatusCode);
