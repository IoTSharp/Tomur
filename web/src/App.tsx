import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { App as AntApp } from "antd";
import type { BubbleItemType } from "@ant-design/x";
import {
  getAgentEvents,
  getAgentRuntime,
  getAgentTelemetry,
  getAgentToolBindings,
  getAgentTools,
  invokeAgentTool,
  createConversation,
  deleteConversation,
  getConversationDetail,
  getConversations,
  getInstalledModels,
  getModelCatalog,
  getModels,
  getMultimodalStatus,
  getRuntimeStatus,
  getVersion,
  prepareNativeRuntime,
  sendChatCompletion,
  sendConversationTurn,
  sendConversationVoiceTurn,
  unloadRuntimeSession
} from "./api";
import {
  createDefaultControlledToolArguments,
  isSideEffectAgentTool,
  parseJsonObject
} from "./app/agentTools";
import { cleanupRecording, convertRecordingToPcmWav } from "./app/audio";
import {
  createLocalAttachment,
  formatUserContent,
  summarizeAttachments
} from "./app/attachments";
import {
  createFallbackConversation,
  initialConversationId,
  initialConversations
} from "./app/conversationState";
import {
  appendPlainTurnToBackend,
  buildTurnMessages,
  buildVoiceTurnMessages,
  findLastUserMessageIndex,
  mapConversationDetail,
  mapConversationRecord,
  parseApiTime,
  replaceTurnMessages,
  resolveConversationTitle,
  syncPlainMessagesToBackend
} from "./app/conversations";
import { resolveSettingsSectionFromDiagnostic } from "./app/diagnostics";
import { createTitle } from "./app/format";
import { isChatModel } from "./app/models";
import type { AppView, SettingsSection } from "./app/viewTypes";
import type { ThemeMode } from "./app/theme";
import { ChatWorkspace } from "./components/chat/ChatWorkspace";
import {
  hasMessageFooterContent,
  MessageFooter
} from "./components/chat/MessageFooter";
import { AppSidebar } from "./components/layout/AppSidebar";
import { NavRail } from "./components/layout/NavRail";
import { SettingsPanel } from "./components/settings/SettingsPanel";
import { StatusView } from "./components/status/StatusView";
import { LogsView } from "./components/logs/LogsView";
import type {
  AgentEventLogRecentResponse,
  AgentFrameworkToolBindingResponse,
  AgentRuntimeStatus,
  AgentTelemetryStatus,
  AgentToolInvokeResponse,
  AgentToolMapResponse,
  ChatMessage,
  Conversation,
  ConversationAttachment,
  ConversationDiagnosticRecord,
  ConversationTurnResponse,
  ConversationVoiceTurnResponse,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  NativeBundlePrepareResult,
  OpenAiModel,
  RuntimeStatusResponse,
  VersionResponse
} from "./types";

function App({
  themeMode,
  onToggleTheme
}: {
  themeMode: ThemeMode;
  onToggleTheme: () => void;
}) {
  const { message } = AntApp.useApp();
  const [conversations, setConversations] = useState<Conversation[]>(initialConversations);
  const [activeConversationId, setActiveConversationId] = useState(initialConversationId);
  const [input, setInput] = useState("");
  const [selectedModel, setSelectedModel] = useState<string>();
  const [version, setVersion] = useState<VersionResponse>();
  const [runtimeStatus, setRuntimeStatus] = useState<RuntimeStatusResponse>();
  const [models, setModels] = useState<OpenAiModel[]>([]);
  const [installedModels, setInstalledModels] = useState<InstalledModelsResponse>();
  const [catalog, setCatalog] = useState<ModelCatalogResponse>();
  const [multimodalStatus, setMultimodalStatus] = useState<MultimodalRuntimeStatus>();
  const [agentRuntime, setAgentRuntime] = useState<AgentRuntimeStatus>();
  const [agentTools, setAgentTools] = useState<AgentToolMapResponse>();
  const [agentToolBindings, setAgentToolBindings] = useState<AgentFrameworkToolBindingResponse>();
  const [agentEvents, setAgentEvents] = useState<AgentEventLogRecentResponse>();
  const [agentTelemetry, setAgentTelemetry] = useState<AgentTelemetryStatus>();
  const [agentToolInvokeAction, setAgentToolInvokeAction] = useState<string | null>(null);
  const [agentToolInvokeResult, setAgentToolInvokeResult] = useState<AgentToolInvokeResponse>();
  const [fileSearchQuery, setFileSearchQuery] = useState("");
  const [controlledToolName, setControlledToolName] = useState("runtime.repair");
  const [controlledToolArguments, setControlledToolArguments] = useState(
    createDefaultControlledToolArguments("runtime.repair")
  );
  const [controlledToolConfirmed, setControlledToolConfirmed] = useState(false);
  const [activeView, setActiveView] = useState<AppView>("chat");
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("general");
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [runtimeAction, setRuntimeAction] = useState<"prepare" | "unload" | null>(null);
  const [prepareResult, setPrepareResult] = useState<NativeBundlePrepareResult>();
  const [sending, setSending] = useState(false);
  const [loadingConversations, setLoadingConversations] = useState(false);
  const [pendingAttachments, setPendingAttachments] = useState<ConversationAttachment[]>([]);
  const [uploadingAttachment, setUploadingAttachment] = useState(false);
  const [speechEnabled, setSpeechEnabled] = useState(false);
  const [recording, setRecording] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const mediaRecorderRef = useRef<MediaRecorder | null>(null);
  const recordingStreamRef = useRef<MediaStream | null>(null);
  const recordingChunksRef = useRef<Blob[]>([]);

  const activeConversation = useMemo(
    () =>
      conversations.find((conversation) => conversation.id === activeConversationId) ??
      conversations[0],
    [activeConversationId, conversations]
  );

  const chatModels = useMemo(() => models.filter(isChatModel), [models]);
  const selectedChatModel = useMemo(
    () => chatModels.find((model) => model.id === selectedModel) ?? chatModels.at(0),
    [chatModels, selectedModel]
  );
  const chatReady = Boolean(selectedChatModel);
  const runtimeOk = runtimeStatus?.status === "ok";
  const runtimeSeverity: "success" | "warning" = runtimeOk ? "success" : "warning";
  const visibleChatModels = useMemo(
    () =>
      chatModels.map((model) => ({
        value: model.id,
        label: model.capabilities?.length
          ? `${model.id} · ${model.capabilities.join(", ")}`
          : model.id
      })),
    [chatModels]
  );
  const controlledAgentTools = useMemo(
    () => (agentTools?.tools ?? []).filter(isSideEffectAgentTool),
    [agentTools]
  );
  const selectedModelLabel = selectedChatModel?.id;

  const warningDiagnostics = useMemo(
    () =>
      (runtimeStatus?.diagnostics ?? []).filter(
        (item) => item.severity !== "info" || item.status !== "ok"
      ),
    [runtimeStatus]
  );

  const inputPlaceholder = useMemo(() => {
    if (!chatReady) {
      return "请先准备本地 Chat 模型";
    }

    if (pendingAttachments.length > 0) {
      return "输入附件说明，Enter 发送";
    }

    return "输入消息，Enter 发送";
  }, [chatReady, pendingAttachments.length]);

  const copyText = useCallback(
    async (text: string, successMessage = "已复制命令") => {
      if (!text.trim()) {
        message.warning("当前没有可复制内容");
        return;
      }

      try {
        await navigator.clipboard.writeText(text);
        message.success(successMessage);
      } catch {
        message.error("复制失败，请手动复制");
      }
    },
    [message]
  );

  const refreshStatus = useCallback(async () => {
    const controller = new AbortController();
    setLoadingStatus(true);

    try {
      const [
        nextVersion,
        nextRuntimeStatus,
        nextModels,
        nextInstalledModels,
        nextCatalog,
        nextMultimodalStatus,
        nextAgentRuntime,
        nextAgentTools,
        nextAgentToolBindings,
        nextAgentEvents,
        nextAgentTelemetry
      ] = await Promise.all([
        getVersion(controller.signal),
        getRuntimeStatus(controller.signal),
        getModels(controller.signal),
        getInstalledModels(controller.signal),
        getModelCatalog(controller.signal),
        getMultimodalStatus(controller.signal),
        getAgentRuntime(controller.signal),
        getAgentTools(controller.signal),
        getAgentToolBindings(controller.signal),
        getAgentEvents(controller.signal),
        getAgentTelemetry(controller.signal)
      ]);

      setVersion(nextVersion);
      setRuntimeStatus(nextRuntimeStatus);
      setModels(nextModels.data);
      setInstalledModels(nextInstalledModels);
      setCatalog(nextCatalog);
      setMultimodalStatus(nextMultimodalStatus);
      setAgentRuntime(nextAgentRuntime);
      setAgentTools(nextAgentTools);
      setAgentToolBindings(nextAgentToolBindings);
      setAgentEvents(nextAgentEvents);
      setAgentTelemetry(nextAgentTelemetry);
      const nextChatModels = nextModels.data.filter(isChatModel);
      setSelectedModel((current) =>
        current && nextChatModels.some((model) => model.id === current)
          ? current
          : nextChatModels.at(0)?.id
      );
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Tomur 状态刷新失败");
    } finally {
      setLoadingStatus(false);
    }
  }, [message]);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  const refreshConversations = useCallback(async () => {
    const controller = new AbortController();
    setLoadingConversations(true);

    try {
      const response = await getConversations(controller.signal);
      const backendConversations = response.conversations.map(mapConversationRecord);
      if (backendConversations.length === 0) {
        setConversations((current) => current.length === 0 ? initialConversations : current);
        return;
      }

      setConversations((current) => {
        const localOnly = current.filter((conversation) => !conversation.backendId && conversation.messages.length > 0);
        const backendIds = new Set(backendConversations.map((conversation) => conversation.id));
        const loadedBackend = current.filter(
          (conversation) => conversation.backendId && backendIds.has(conversation.backendId)
        );
        const loadedByBackendId = new Map(loadedBackend.map((conversation) => [conversation.backendId, conversation]));
        const mergedBackend = backendConversations.map((conversation) => {
          const existing = loadedByBackendId.get(conversation.backendId);
          return existing?.loaded
            ? { ...conversation, messages: existing.messages, loaded: true, loading: false }
            : conversation;
        });
        return [...mergedBackend, ...localOnly];
      });
      setActiveConversationId((current) =>
        backendConversations.some((conversation) => conversation.id === current)
          ? current
          : backendConversations[0].id
      );
    } catch (error) {
      message.warning(error instanceof Error ? error.message : "本地会话历史加载失败");
    } finally {
      setLoadingConversations(false);
    }
  }, [message]);

  useEffect(() => {
    void refreshConversations();
  }, [refreshConversations]);

  const selectControlledAgentTool = useCallback((tool: string) => {
    setControlledToolName(tool);
    setControlledToolArguments(createDefaultControlledToolArguments(tool));
    setControlledToolConfirmed(false);
  }, []);

  useEffect(() => {
    if (controlledAgentTools.length === 0) {
      return;
    }

    if (!controlledAgentTools.some((tool) => tool.name === controlledToolName)) {
      selectControlledAgentTool(controlledAgentTools[0].name);
    }
  }, [controlledAgentTools, controlledToolName, selectControlledAgentTool]);

  const runNativePrepare = useCallback(async () => {
    const controller = new AbortController();
    setRuntimeAction("prepare");

    try {
      const result = await prepareNativeRuntime(controller.signal);
      setPrepareResult(result);
      message[result.status === "error" ? "warning" : "success"](result.message);
      await refreshStatus();
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Native runtime 准备失败");
    } finally {
      setRuntimeAction(null);
    }
  }, [message, refreshStatus]);

  const runSessionUnload = useCallback(async () => {
    const controller = new AbortController();
    setRuntimeAction("unload");

    try {
      const nextRuntimeStatus = await unloadRuntimeSession(controller.signal);
      setRuntimeStatus(nextRuntimeStatus);
      message.success("已卸载当前 runtime session");
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Runtime session 卸载失败");
    } finally {
      setRuntimeAction(null);
    }
  }, [message]);

  const runReadOnlyAgentTool = useCallback(
    async (tool: string, argumentsPayload?: Record<string, unknown>) => {
      const controller = new AbortController();
      setAgentToolInvokeAction(tool);

      try {
        const result = await invokeAgentTool(
          {
            tool,
            mode: "read_only",
            arguments: argumentsPayload
          },
          controller.signal
        );
        setAgentToolInvokeResult(result);
        message[result.status === "ok" ? "success" : "warning"](
          `${tool} ${result.status}`
        );
      } catch (error) {
        message.error(error instanceof Error ? error.message : `${tool} 调用失败`);
      } finally {
        setAgentToolInvokeAction(null);
      }
    },
    [message]
  );

  const runFileSearch = useCallback(async () => {
    const query = fileSearchQuery.trim();
    if (!query) {
      message.warning("请输入要检索的本地文件查询");
      return;
    }

    await runReadOnlyAgentTool("files.search", {
      query,
      top_k: 8,
      refresh: true
    });
  }, [fileSearchQuery, message, runReadOnlyAgentTool]);

  const runControlledAgentTool = useCallback(async () => {
    const selectedTool = controlledAgentTools.find((tool) => tool.name === controlledToolName);
    if (!selectedTool) {
      message.warning("当前没有可调用的受控工具");
      return;
    }

    if (selectedTool.requires_confirmation && !controlledToolConfirmed) {
      message.warning("请先确认本次副作用工具调用");
      return;
    }

    let argumentsPayload: Record<string, unknown>;
    try {
      argumentsPayload = parseJsonObject(controlledToolArguments);
    } catch (error) {
      message.warning(error instanceof Error ? error.message : "工具参数必须是 JSON object");
      return;
    }

    const controller = new AbortController();
    setAgentToolInvokeAction(selectedTool.name);

    try {
      const result = await invokeAgentTool(
        {
          tool: selectedTool.name,
          mode: "controlled",
          confirm: selectedTool.requires_confirmation ? true : controlledToolConfirmed || undefined,
          arguments: argumentsPayload
        },
        controller.signal
      );
      setAgentToolInvokeResult(result);
      if (result.status === "ok") {
        message.success(`${selectedTool.name} ${result.status}`);
      } else if (result.status === "blocked") {
        message.warning(`${selectedTool.name} ${result.status}`);
      } else {
        message.error(`${selectedTool.name} ${result.status}`);
      }
      setControlledToolConfirmed(false);
      await refreshStatus();
    } catch (error) {
      message.error(error instanceof Error ? error.message : `${selectedTool.name} 调用失败`);
    } finally {
      setAgentToolInvokeAction(null);
    }
  }, [
    controlledAgentTools,
    controlledToolArguments,
    controlledToolConfirmed,
    controlledToolName,
    message,
    refreshStatus
  ]);

  const updateConversation = useCallback(
    (conversationId: string, updater: (conversation: Conversation) => Conversation) => {
      setConversations((current) =>
        current.map((conversation) =>
          conversation.id === conversationId ? updater(conversation) : conversation
        )
      );
    },
    []
  );

  const openSettings = useCallback((section: SettingsSection) => {
    setSettingsSection(section);
    setActiveView("settings");
  }, []);

  const openDiagnosticContext = useCallback((diagnostic: ConversationDiagnosticRecord) => {
    openSettings(resolveSettingsSectionFromDiagnostic(diagnostic));
  }, [openSettings]);

  const startConversation = useCallback(() => {
    const id = `chat-${crypto.randomUUID()}`;
    const conversation: Conversation = {
      id,
      title: "新会话",
      updatedAt: Date.now(),
      messages: []
    };

    setConversations((current) => [conversation, ...current]);
    setActiveConversationId(id);
  }, []);

  const removeConversationFromState = useCallback(
    (conversationId: string) => {
      const next = conversations.length === 1
        ? [createFallbackConversation()]
        : conversations.filter((conversation) => conversation.id !== conversationId);

      setConversations(next);
      if (activeConversationId === conversationId) {
        setActiveConversationId(next[0]?.id ?? initialConversationId);
      }
    },
    [activeConversationId, conversations]
  );

  const removeConversation = useCallback(
    async (conversationId: string) => {
      const conversation = conversations.find((item) => item.id === conversationId);
      removeConversationFromState(conversationId);

      if (!conversation?.backendId) {
        return;
      }

      const controller = new AbortController();
      try {
        await deleteConversation(conversation.backendId, controller.signal);
        message.success("已移除本地会话");
      } catch (error) {
        message.warning(error instanceof Error ? error.message : "本地会话删除失败");
        void refreshConversations();
      }
    },
    [conversations, message, refreshConversations, removeConversationFromState]
  );

  const openConversation = useCallback(
    async (conversationId: string) => {
      setActiveConversationId(conversationId);
      const conversation = conversations.find((item) => item.id === conversationId);
      const backendId = conversation?.backendId;
      if (!backendId || conversation.loaded || conversation.loading) {
        return;
      }

      updateConversation(conversationId, (current) => ({ ...current, loading: true }));
      const controller = new AbortController();
      try {
        const detail = await getConversationDetail(backendId, controller.signal);
        updateConversation(conversationId, () => mapConversationDetail(detail));
      } catch (error) {
        updateConversation(conversationId, (current) => ({ ...current, loading: false }));
        message.warning(error instanceof Error ? error.message : "本地会话详情加载失败");
      }
    },
    [conversations, message, updateConversation]
  );

  useEffect(() => {
    if (activeConversation.backendId && !activeConversation.loaded && !activeConversation.loading) {
      void openConversation(activeConversation.id);
    }
  }, [activeConversation, openConversation]);

  const ensureBackendConversation = useCallback(
    async (
      conversation: Conversation,
      model: string,
      signal?: AbortSignal,
      messagesToSync = conversation.messages
    ) => {
      if (conversation.backendId) {
        return conversation.backendId;
      }

      const response = await createConversation(
        {
          title: conversation.title,
          model
        },
        signal
      );

      updateConversation(conversation.id, (current) => ({
        ...current,
        backendId: response.conversation.id,
        title: response.conversation.title || current.title,
        updatedAt: parseApiTime(response.conversation.updated_at),
        loaded: true
      }));

      try {
        await syncPlainMessagesToBackend(response.conversation.id, messagesToSync, signal);
      } catch (error) {
        if (error instanceof DOMException && error.name === "AbortError") {
          throw error;
        }

        message.warning("本地会话历史同步失败，当前回合会继续发送。");
      }

      return response.conversation.id;
    },
    [message, updateConversation]
  );

  const applyTurnResponse = useCallback(
    (
      conversationId: string,
      userMessageId: string,
      assistantMessageId: string,
      response: ConversationTurnResponse
    ) => {
      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        backendId: response.conversation.id,
        title: resolveConversationTitle(conversation, response.conversation),
        updatedAt: parseApiTime(response.conversation.updated_at),
        loaded: true,
        loading: false,
        messages: replaceTurnMessages(
          conversation.messages,
          userMessageId,
          assistantMessageId,
          buildTurnMessages(response, assistantMessageId)
        )
      }));
    },
    [updateConversation]
  );

  const applyVoiceTurnResponse = useCallback(
    (
      conversationId: string,
      userMessageId: string,
      assistantMessageId: string,
      response: ConversationVoiceTurnResponse
    ) => {
      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        backendId: response.conversation.id,
        title: resolveConversationTitle(conversation, response.conversation, response.transcript),
        updatedAt: parseApiTime(response.conversation.updated_at),
        loaded: true,
        loading: false,
        messages: replaceTurnMessages(
          conversation.messages,
          userMessageId,
          assistantMessageId,
          buildVoiceTurnMessages(response, userMessageId, assistantMessageId)
        )
      }));
    },
    [updateConversation]
  );

  const addPendingAttachment = useCallback(
    async (file: File) => {
      if (sending || recording) {
        return;
      }

      setUploadingAttachment(true);
      try {
        const attachment = await createLocalAttachment(file);
        setPendingAttachments((current) => [...current, attachment]);
      } catch (error) {
        message.error(error instanceof Error ? error.message : "附件读取失败");
      } finally {
        setUploadingAttachment(false);
      }
    },
    [message, recording, sending]
  );

  const removePendingAttachment = useCallback((id: string) => {
    setPendingAttachments((current) => current.filter((item) => item.id !== id));
  }, []);

  const submitVoiceBlob = useCallback(
    async (recordedBlob: Blob) => {
      const model = selectedModelLabel;

      if (sending) {
        return;
      }

      if (!model) {
        openSettings("models");
        message.warning("Tomur 当前没有可见 Chat 模型");
        return;
      }

      const conversationId = activeConversation.id;
      const userMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: "正在转写语音...",
        status: "loading"
      };
      const assistantMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "assistant",
        content: "",
        status: "loading"
      };

      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        title: conversation.messages.length === 0 ? "语音会话" : conversation.title,
        updatedAt: Date.now(),
        messages: [...conversation.messages, userMessage, assistantMessage]
      }));

      const controller = new AbortController();
      abortRef.current = controller;
      setSending(true);

      try {
        const wavBlob = await convertRecordingToPcmWav(recordedBlob);
        const backendConversationId = await ensureBackendConversation(
          activeConversation,
          model,
          controller.signal
        );
        const response = await sendConversationVoiceTurn(
          backendConversationId,
          wavBlob,
          {
            fileName: `voice-${Date.now()}.wav`,
            model,
            speak: true,
            responseFormat: "wav"
          },
          controller.signal
        );
        applyVoiceTurnResponse(conversationId, userMessage.id, assistantMessage.id, response);
      } catch (error) {
        const errorText =
          error instanceof DOMException && error.name === "AbortError"
            ? "已停止语音回合。"
            : error instanceof Error
              ? error.message
              : "Tomur 语音回合请求失败";

        updateConversation(conversationId, (conversation) => ({
          ...conversation,
          messages: replaceTurnMessages(conversation.messages, userMessage.id, assistantMessage.id, [
            { ...userMessage, content: "语音输入", status: "error" },
            { ...assistantMessage, content: errorText, status: "error" }
          ])
        }));
      } finally {
        setSending(false);
        abortRef.current = null;
      }
    },
    [
      activeConversation,
      applyVoiceTurnResponse,
      ensureBackendConversation,
      message,
      openSettings,
      selectedModelLabel,
      sending,
      updateConversation
    ]
  );

  const startVoiceRecording = useCallback(async () => {
    if (recording || sending) {
      return;
    }

    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined") {
      message.error("当前浏览器不支持录音");
      return;
    }

    if (!selectedModelLabel) {
      openSettings("models");
      message.warning("Tomur 当前没有可见 Chat 模型");
      return;
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: true,
          noiseSuppression: true
        }
      });
      const recorder = new MediaRecorder(stream);
      recordingChunksRef.current = [];
      recordingStreamRef.current = stream;
      mediaRecorderRef.current = recorder;

      recorder.ondataavailable = (event) => {
        if (event.data.size > 0) {
          recordingChunksRef.current.push(event.data);
        }
      };

      recorder.onstop = () => {
        const chunks = recordingChunksRef.current;
        const mimeType = recorder.mimeType || "audio/webm";
        cleanupRecording(mediaRecorderRef, recordingStreamRef, recordingChunksRef);
        setRecording(false);
        if (chunks.length > 0) {
          void submitVoiceBlob(new Blob(chunks, { type: mimeType }));
        }
      };

      recorder.onerror = () => {
        cleanupRecording(mediaRecorderRef, recordingStreamRef, recordingChunksRef);
        setRecording(false);
        message.error("录音失败");
      };

      recorder.start();
      setRecording(true);
    } catch (error) {
      cleanupRecording(mediaRecorderRef, recordingStreamRef, recordingChunksRef);
      setRecording(false);
      message.error(error instanceof Error ? error.message : "无法访问麦克风");
    }
  }, [message, openSettings, recording, selectedModelLabel, sending, submitVoiceBlob]);

  const stopVoiceRecording = useCallback(() => {
    const recorder = mediaRecorderRef.current;
    if (recorder && recorder.state !== "inactive") {
      recorder.stop();
      return;
    }

    cleanupRecording(mediaRecorderRef, recordingStreamRef, recordingChunksRef);
    setRecording(false);
  }, []);

  useEffect(
    () => () => {
      abortRef.current?.abort();
      cleanupRecording(mediaRecorderRef, recordingStreamRef, recordingChunksRef);
    },
    []
  );

  const submitMessage = useCallback(
    async (content: string) => {
      const trimmed = content.trim();
      const model = selectedModelLabel;
      const attachments = pendingAttachments;
      const useConversationTurn = attachments.length > 0 || speechEnabled;

      if ((!trimmed && attachments.length === 0) || sending) {
        return;
      }

      if (!model) {
        openSettings("models");
        message.warning("Tomur 当前没有可见 Chat 模型");
        return;
      }

      const conversationId = activeConversation.id;
      const userMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: formatUserContent(trimmed, attachments),
        status: "local",
        attachments
      };
      const assistantMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "assistant",
        content: "",
        status: "loading"
      };

      setInput("");
      setPendingAttachments([]);
      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        title: conversation.messages.length === 0
          ? createTitle(trimmed || summarizeAttachments(attachments))
          : conversation.title,
        updatedAt: Date.now(),
        messages: [...conversation.messages, userMessage, assistantMessage]
      }));

      const controller = new AbortController();
      abortRef.current = controller;
      setSending(true);

      try {
        if (useConversationTurn) {
          const backendConversationId = await ensureBackendConversation(
            activeConversation,
            model,
            controller.signal,
            activeConversation.messages
          );
          const response = await sendConversationTurn(
            backendConversationId,
            {
              content: trimmed || undefined,
              modality: attachments.length > 0 ? "multimodal" : "text",
              model,
              attachments,
              max_tool_rounds: 4,
              speak: speechEnabled,
              confirm: speechEnabled,
              response_format: "wav"
            },
            controller.signal
          );
          applyTurnResponse(conversationId, userMessage.id, assistantMessage.id, response);
          return;
        }

        const sentMessages = [...activeConversation.messages, userMessage];
        let accumulated = "";

        const text = await sendChatCompletion(
          model,
          sentMessages,
          controller.signal,
          (chunk) => {
            accumulated += chunk;
            updateConversation(conversationId, (conversation) => ({
              ...conversation,
              messages: conversation.messages.map((item) =>
                item.id === assistantMessage.id
                  ? { ...item, content: accumulated, status: "loading" }
                  : item
              )
            }));
          }
        );

        updateConversation(conversationId, (conversation) => ({
          ...conversation,
          updatedAt: Date.now(),
          messages: conversation.messages.map((item) =>
            item.id === assistantMessage.id
              ? { ...item, content: text || accumulated, status: "success" }
              : item
          )
        }));

        try {
          const backendConversationId = await ensureBackendConversation(
            activeConversation,
            model,
            controller.signal,
            activeConversation.messages
          );
          await appendPlainTurnToBackend(
            backendConversationId,
            model,
            userMessage.content,
            text || accumulated,
            controller.signal
          );
        } catch (error) {
          if (error instanceof DOMException && error.name === "AbortError") {
            throw error;
          }

          message.warning("本地会话历史同步失败，当前回复已保留在前端。");
        }
      } catch (error) {
        if (attachments.length > 0) {
          setPendingAttachments((current) => [...attachments, ...current]);
        }

        const errorText =
          error instanceof DOMException && error.name === "AbortError"
            ? "已停止生成。"
            : error instanceof Error
              ? error.message
              : "Tomur Chat 请求失败";

        updateConversation(conversationId, (conversation) => ({
          ...conversation,
          messages: conversation.messages.map((item) =>
            item.id === assistantMessage.id
              ? { ...item, content: errorText, status: "error" }
              : item
          )
        }));
      } finally {
        setSending(false);
        abortRef.current = null;
      }
    },
    [
      activeConversation.id,
      activeConversation.messages,
      activeConversation,
      applyTurnResponse,
      ensureBackendConversation,
      message,
      openSettings,
      pendingAttachments,
      selectedModelLabel,
      sending,
      speechEnabled,
      updateConversation
    ]
  );

  const regenerate = useCallback(async () => {
    if (sending) {
      return;
    }

    const model = selectedModelLabel;
    if (!model) {
      openSettings("models");
      message.warning("Tomur 当前没有可见 Chat 模型");
      return;
    }

    const lastUserIndex = findLastUserMessageIndex(activeConversation.messages);
    if (lastUserIndex < 0) {
      return;
    }

    const lastUser = activeConversation.messages[lastUserIndex];
    if (lastUser.attachments?.length || lastUser.transcript) {
      message.warning("附件或语音回合请重新发送原始输入。");
      return;
    }

    const conversationId = activeConversation.id;
    const history = activeConversation.messages.slice(0, lastUserIndex + 1);
    const assistantMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: "assistant",
      content: "",
      status: "loading"
    };

    updateConversation(conversationId, (conversation) => ({
      ...conversation,
      updatedAt: Date.now(),
      messages: [...conversation.messages.slice(0, lastUserIndex + 1), assistantMessage]
    }));

    const controller = new AbortController();
    abortRef.current = controller;
    setSending(true);

    try {
      let accumulated = "";
      const text = await sendChatCompletion(
        model,
        history,
        controller.signal,
        (chunk) => {
          accumulated += chunk;
          updateConversation(conversationId, (conversation) => ({
            ...conversation,
            messages: conversation.messages.map((item) =>
              item.id === assistantMessage.id
                ? { ...item, content: accumulated, status: "loading" }
                : item
            )
          }));
        }
      );

      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        updatedAt: Date.now(),
        messages: conversation.messages.map((item) =>
          item.id === assistantMessage.id
            ? { ...item, content: text || accumulated, status: "success" }
            : item
        )
      }));
    } catch (error) {
      const errorText =
        error instanceof DOMException && error.name === "AbortError"
          ? "已停止生成。"
          : error instanceof Error
            ? error.message
            : "Tomur Chat 请求失败";

      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        messages: conversation.messages.map((item) =>
          item.id === assistantMessage.id
            ? { ...item, content: errorText, status: "error" }
            : item
        )
      }));
    } finally {
      setSending(false);
      abortRef.current = null;
    }
  }, [
    activeConversation.id,
    activeConversation.messages,
    message,
    openSettings,
    selectedModelLabel,
    sending,
    updateConversation
  ]);

  const stopGeneration = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const bubbleItems: BubbleItemType[] = activeConversation.messages.map((item) => {
    const assistantSide = item.role === "assistant" || item.role === "tool";
    const hasFooter = assistantSide || hasMessageFooterContent(item);
    return {
      key: item.id,
      role: assistantSide ? "ai" : "user",
      content: item.content,
      status: item.status,
      loading: item.status === "loading" && !item.content,
      footer:
        hasFooter
        ? () => (
            <MessageFooter
              message={item}
              showCopyAction={assistantSide}
              onOpenDiagnostic={openDiagnosticContext}
              onCopyMessage={() => void copyText(item.content, "已复制消息")}
              onCopyText={copyText}
            />
          )
        : undefined
    };
  });

  return (
    <div className="app-shell">
      <NavRail
        activeView={activeView}
        onChangeView={setActiveView}
        themeMode={themeMode}
        onToggleTheme={onToggleTheme}
        version={version?.version}
        runtimeOk={runtimeOk}
      />

      <div className="app-content">
        {activeView === "chat" && (
          <div className="chat-layout">
            <AppSidebar
              version={version?.version}
              runtimeOk={runtimeOk}
              runtimeSeverity={runtimeSeverity}
              runtimeMessage={runtimeStatus?.runtime.message}
              loadingConversations={loadingConversations}
              conversations={conversations}
              activeConversation={activeConversation}
              onStartConversation={startConversation}
              onOpenStatus={() => setActiveView("status")}
              onOpenConversation={(conversationId) => void openConversation(conversationId)}
              onRemoveConversation={(conversationId) => void removeConversation(conversationId)}
            />

            <ChatWorkspace
              activeConversation={activeConversation}
              bubbleItems={bubbleItems}
              chatModels={chatModels}
              selectedModelLabel={selectedModelLabel}
              visibleChatModels={visibleChatModels}
              chatReady={chatReady}
              runtimeOk={runtimeOk}
              runtimeStatus={runtimeStatus}
              warningDiagnostics={warningDiagnostics}
              catalog={catalog}
              multimodalStatus={multimodalStatus}
              agentRuntime={agentRuntime}
              models={models}
              installedModels={installedModels}
              agentTools={agentTools}
              loadingStatus={loadingStatus}
              input={input}
              pendingAttachments={pendingAttachments}
              sending={sending}
              recording={recording}
              uploadingAttachment={uploadingAttachment}
              speechEnabled={speechEnabled}
              inputPlaceholder={inputPlaceholder}
              onSelectedModelChange={setSelectedModel}
              onRefreshStatus={() => void refreshStatus()}
              onOpenStatus={() => setActiveView("status")}
              onOpenSettings={openSettings}
              onInputChange={setInput}
              onAddPendingAttachment={(file) => void addPendingAttachment(file)}
              onRemovePendingAttachment={removePendingAttachment}
              onStartVoiceRecording={() => void startVoiceRecording()}
              onStopVoiceRecording={stopVoiceRecording}
              onToggleSpeech={() => setSpeechEnabled((current) => !current)}
              onSubmitMessage={(value) => void submitMessage(value)}
              onStopGeneration={stopGeneration}
              onRegenerate={() => void regenerate()}
            />
          </div>
        )}

        {activeView === "status" && (
          <StatusView
            runtimeStatus={runtimeStatus}
            multimodalStatus={multimodalStatus}
            agentRuntime={agentRuntime}
            models={models}
            catalog={catalog}
            agentTools={agentTools}
            loading={loadingStatus}
            onRefreshStatus={() => void refreshStatus()}
            onOpenSettings={openSettings}
          />
        )}

        {activeView === "logs" && <LogsView />}

        {activeView === "settings" && (
          <SettingsPanel
            section={settingsSection}
            onSectionChange={setSettingsSection}
            themeMode={themeMode}
            onToggleTheme={onToggleTheme}
            runtimeStatus={runtimeStatus}
            models={models}
            installedModels={installedModels}
            catalog={catalog}
            multimodalStatus={multimodalStatus}
            agentRuntime={agentRuntime}
            agentTools={agentTools}
            agentToolBindings={agentToolBindings}
            agentEvents={agentEvents}
            agentTelemetry={agentTelemetry}
            agentToolInvokeAction={agentToolInvokeAction}
            agentToolInvokeResult={agentToolInvokeResult}
            fileSearchQuery={fileSearchQuery}
            controlledToolName={controlledToolName}
            controlledToolArguments={controlledToolArguments}
            controlledToolConfirmed={controlledToolConfirmed}
            runtimeAction={runtimeAction}
            prepareResult={prepareResult}
            onCopyText={copyText}
            onPrepareNativeRuntime={runNativePrepare}
            onUnloadRuntimeSession={runSessionUnload}
            onRunReadOnlyAgentTool={runReadOnlyAgentTool}
            onFileSearchQueryChange={setFileSearchQuery}
            onRunFileSearch={runFileSearch}
            onControlledToolChange={selectControlledAgentTool}
            onControlledToolArgumentsChange={setControlledToolArguments}
            onControlledToolConfirmedChange={setControlledToolConfirmed}
            onRunControlledAgentTool={runControlledAgentTool}
          />
        )}
      </div>
    </div>
  );
}

export default App;
