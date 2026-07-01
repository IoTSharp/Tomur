import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  Alert,
  App as AntApp,
  Badge,
  Button,
  Card,
  Collapse,
  Descriptions,
  Drawer,
  Empty,
  Flex,
  List,
  Select,
  Segmented,
  Space,
  Spin,
  Tag,
  Tooltip,
  Typography
} from "antd";
import {
  Copy,
  Download,
  FolderOpen,
  HardDrive,
  Layers3,
  MessageSquarePlus,
  Mic,
  PanelRightOpen,
  Paperclip,
  RefreshCcw,
  RotateCcw,
  Settings,
  Square,
  Trash2,
  Volume2,
  Wrench
} from "lucide-react";
import {
  Actions,
  Attachments,
  Bubble,
  Conversations,
  Prompts,
  Sender,
  Welcome
} from "@ant-design/x";
import XMarkdown from "@ant-design/x-markdown";
import type { BubbleItemType, PromptsItemType } from "@ant-design/x";
import {
  appendConversationMessage,
  createConversation,
  getConversationArtifactContentUrl,
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
import type {
  ChatMessage,
  Conversation,
  ConversationArtifactRecord,
  ConversationAttachment,
  ConversationDiagnosticRecord,
  ConversationMessageRecord,
  ConversationRecord,
  ConversationTurnResponse,
  ConversationVoiceTurnResponse,
  DiagnosticItem,
  InstalledModelAsset,
  InstalledModelsResponse,
  InstalledModelPackage,
  ModelCatalogAsset,
  ModelCatalogBundleAsset,
  ModelCatalogPackage,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  NativeBundlePrepareResult,
  OpenAiModel,
  RuntimeStatusResponse,
  VersionResponse
} from "./types";

const initialConversationId = "local-chat";

const initialConversations: Conversation[] = [
  {
    id: initialConversationId,
    title: "本地 Chat",
    updatedAt: Date.now(),
    messages: []
  }
];

const promptItems: PromptsItemType[] = [
  {
    key: "runtime",
    label: "检查 runtime",
    description: "读取当前诊断并说明已接通能力"
  },
  {
    key: "models",
    label: "查看本地模型",
    description: "列出当前可见模型并推荐 Chat 默认模型"
  },
  {
    key: "setup",
    label: "给我起步步骤",
    description: "根据当前状态生成最小可行准备方案"
  }
];

const promptText: Record<string, string> = {
  runtime: "请根据当前 Tomur 本地 runtime 状态，说明哪些能力已经可用，哪些还需要准备。",
  models: "请列出 Tomur 当前可见的本地模型，并推荐一个适合用于 Chat 的模型。",
  setup: "请根据当前 Tomur 状态，给我一个可以开始使用本地 Chat 的最小准备步骤。"
};

type SettingsSection =
  | "general"
  | "models"
  | "downloads"
  | "runtime"
  | "api"
  | "files"
  | "advanced";

function App() {
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
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("runtime");
  const [statusDrawerOpen, setStatusDrawerOpen] = useState(false);
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [runtimeAction, setRuntimeAction] = useState<"prepare" | "unload" | null>(null);
  const [prepareResult, setPrepareResult] = useState<NativeBundlePrepareResult>();
  const [sending, setSending] = useState(false);
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
  const runtimeSeverity = runtimeOk ? "success" : "warning";
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
        nextMultimodalStatus
      ] = await Promise.all([
        getVersion(controller.signal),
        getRuntimeStatus(controller.signal),
        getModels(controller.signal),
        getInstalledModels(controller.signal),
        getModelCatalog(controller.signal),
        getMultimodalStatus(controller.signal)
      ]);

      setVersion(nextVersion);
      setRuntimeStatus(nextRuntimeStatus);
      setModels(nextModels.data);
      setInstalledModels(nextInstalledModels);
      setCatalog(nextCatalog);
      setMultimodalStatus(nextMultimodalStatus);
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
    setSettingsOpen(true);
  }, []);

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

  const removeConversation = useCallback(
    (conversationId: string) => {
      setConversations((current) => {
        if (current.length === 1) {
          return [
            {
              id: initialConversationId,
              title: "本地 Chat",
              updatedAt: Date.now(),
              messages: []
            }
          ];
        }

        return current.filter((conversation) => conversation.id !== conversationId);
      });

      if (activeConversationId === conversationId) {
        const nextConversation = conversations.find((conversation) => conversation.id !== conversationId);
        setActiveConversationId(nextConversation?.id ?? initialConversationId);
      }
    },
    [activeConversationId, conversations]
  );

  const ensureBackendConversation = useCallback(
    async (conversation: Conversation, model: string, signal?: AbortSignal) => {
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
        updatedAt: parseApiTime(response.conversation.updated_at)
      }));

      try {
        await syncPlainMessagesToBackend(response.conversation.id, conversation.messages, signal);
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
            controller.signal
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

        if (activeConversation.backendId) {
          try {
            await appendPlainTurnToBackend(
              activeConversation.backendId,
              model,
              userMessage.content,
              text || accumulated,
              controller.signal
            );
          } catch {
            message.warning("本地会话历史同步失败，当前回复已保留在前端。");
          }
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

  const regenerate = useCallback(() => {
    const lastUser = [...activeConversation.messages].reverse().find((item) => item.role === "user");
    if (lastUser) {
      void submitMessage(lastUser.content);
    }
  }, [activeConversation.messages, submitMessage]);

  const stopGeneration = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const bubbleItems: BubbleItemType[] = activeConversation.messages.map((item) => {
    const assistantSide = item.role === "assistant" || item.role === "tool";
    return {
      key: item.id,
      role: assistantSide ? "ai" : "user",
      content: item.content,
      status: item.status,
      loading: item.status === "loading" && !item.content,
      footer:
        assistantSide
        ? () => (
            <MessageFooter
              message={item}
              onCopy={() => {
                void navigator.clipboard.writeText(item.content);
                message.success("已复制");
              }}
            />
          )
        : undefined
    };
  });

  const conversationItems = conversations.map((conversation) => ({
    key: conversation.id,
    label: conversation.title,
    group: "本地"
  }));

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-copy">
            <span className="brand-mark">
              <img src="/icons/app-icon.svg" alt="" aria-hidden="true" />
            </span>
            <div>
              <Typography.Title level={4}>Tomur</Typography.Title>
              <Typography.Text type="secondary">
                {version?.version ?? "local runtime"}
              </Typography.Text>
            </div>
          </div>
          <Tooltip title="新建会话">
            <Button icon={<MessageSquarePlus size={16} />} onClick={startConversation} />
          </Tooltip>
        </div>

        <Card className="sidebar-card" bordered={false}>
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            <Flex justify="space-between" align="center">
              <Space size={8}>
                <Badge status={runtimeSeverity} />
                <Typography.Text strong>
                  {runtimeOk ? "本地工作台已就绪" : "需要完成本地准备"}
                </Typography.Text>
              </Space>
              <Button type="text" size="small" onClick={() => setStatusDrawerOpen(true)}>
                详情
              </Button>
            </Flex>
            <Typography.Text type="secondary">
              {runtimeStatus?.runtime.message ?? "正在读取本地运行状态"}
            </Typography.Text>
          </Space>
        </Card>

        <Conversations
          className="conversation-list"
          items={conversationItems}
          activeKey={activeConversation.id}
          groupable
          onActiveChange={(key) => setActiveConversationId(String(key))}
          menu={(item) => ({
            items: [{ key: "delete", icon: <Trash2 size={14} />, label: "删除" }],
            onClick: ({ key }) => {
              if (key === "delete") {
                removeConversation(item.key);
              }
            }
          })}
        />
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div className="topbar-copy">
            <Typography.Title level={3}>Chat-first 本地工作台</Typography.Title>
            <Typography.Text type="secondary">
              默认入口直接进入对话区，模型、下载、Runtime 和文件能力通过状态抽屉与 Settings 收敛。
            </Typography.Text>
          </div>

          <div className="topbar-actions">
            <Select
              className="model-select"
              placeholder="选择本地模型"
              value={selectedModelLabel}
              options={visibleChatModels}
              onChange={(value) => setSelectedModel(value)}
              disabled={chatModels.length === 0}
            />
            <Tooltip title="刷新状态">
              <Button
                icon={<RefreshCcw size={16} />}
                loading={loadingStatus}
                onClick={() => void refreshStatus()}
              />
            </Tooltip>
            <Tooltip title="状态抽屉">
              <Button icon={<PanelRightOpen size={16} />} onClick={() => setStatusDrawerOpen(true)} />
            </Tooltip>
            <Tooltip title="Settings">
              <Button icon={<Settings size={16} />} onClick={() => setSettingsOpen(true)} />
            </Tooltip>
          </div>
        </header>

        <section className="status-strip">
          <StatusPill
            label="Runtime"
            value={runtimeStatus?.status ?? "checking"}
            tone={runtimeOk ? "success" : "warning"}
            onClick={() => setStatusDrawerOpen(true)}
          />
          <StatusPill
            label="Models"
            value={String(chatModels.length)}
            tone={chatModels.length > 0 ? "success" : "warning"}
            onClick={() => openSettings("models")}
          />
          <StatusPill
            label="Downloads"
            value={String(catalog?.packages.filter((item) => item.installed).length ?? 0)}
            tone="default"
            onClick={() => openSettings("downloads")}
          />
          <StatusPill
            label="Multimodal"
            value={multimodalStatus?.status ?? "pending"}
            tone={multimodalStatus?.status === "ok" ? "success" : "warning"}
            onClick={() => setStatusDrawerOpen(true)}
          />
        </section>

        {!chatReady && (
          <Alert
            className="inline-diagnostic"
            type="warning"
            showIcon
            message="Tomur 当前没有可见 Chat 模型"
            description="工作台已经连接本地 API，但不会在模型缺失时伪造回复。请先下载或导入模型。"
            action={
              <Button size="small" onClick={() => openSettings("models")}>
                打开 Models
              </Button>
            }
          />
        )}

        {!runtimeOk && warningDiagnostics.length > 0 && (
          <Alert
            className="inline-diagnostic"
            type="info"
            showIcon
            message="当前存在待处理的本地诊断"
            description={warningDiagnostics[0]?.message}
            action={
              <Button size="small" onClick={() => setStatusDrawerOpen(true)}>
                查看诊断
              </Button>
            }
          />
        )}

        <section className="chat-area">
          {activeConversation.messages.length === 0 ? (
            <div className="empty-chat">
              <Welcome
                variant="borderless"
                title="本地 Chat 工作台"
                description="连接 Tomur 本地 OpenAI 兼容接口，使用已安装模型进行对话。"
              />
              <Prompts
                wrap
                items={promptItems}
                onItemClick={({ data }) => {
                  const text = promptText[data.key] ?? String(data.label ?? "");
                  setInput(text);
                }}
              />
            </div>
          ) : (
            <Bubble.List
              className="bubble-list"
              items={bubbleItems}
              autoScroll
              role={{
                ai: {
                  placement: "start",
                  variant: "borderless",
                  contentRender: (content) => (
                    <XMarkdown
                      content={String(content ?? "")}
                      openLinksInNewTab
                      escapeRawHtml
                    />
                  )
                },
                user: {
                  placement: "end",
                  variant: "filled"
                }
              }}
            />
          )}
        </section>

        <footer className="composer">
          <div className="attachment-strip">
            <Attachments
              disabled={sending || recording || uploadingAttachment}
              items={pendingAttachments.map(toAttachmentItem)}
              overflow="wrap"
              beforeUpload={(file) => {
                void addPendingAttachment(file);
                return false;
              }}
              onRemove={(file) => {
                removePendingAttachment(String(file.uid));
                return true;
              }}
              placeholder={{
                title: "附件入口",
                description: "选择图片、音频或文本文件后随下一轮会话发送。"
              }}
            >
              <Button
                icon={<Paperclip size={16} />}
                loading={uploadingAttachment}
                disabled={sending || recording}
              >
                附件
              </Button>
            </Attachments>
            <Tooltip title={recording ? "停止录音并发送" : "录音语音回合"}>
              <Button
                type={recording ? "primary" : "default"}
                danger={recording}
                icon={recording ? <Square size={16} /> : <Mic size={16} />}
                disabled={sending || uploadingAttachment}
                onClick={recording ? stopVoiceRecording : () => void startVoiceRecording()}
              />
            </Tooltip>
            <Tooltip title={speechEnabled ? "本轮请求 TTS 朗读" : "本轮只返回文字"}>
              <Button
                type={speechEnabled ? "primary" : "default"}
                icon={<Volume2 size={16} />}
                disabled={sending || recording}
                onClick={() => setSpeechEnabled((current) => !current)}
              />
            </Tooltip>
            {pendingAttachments.length > 0 && (
              <Button
                type="primary"
                disabled={sending || recording || uploadingAttachment}
                onClick={() => void submitMessage(input)}
              >
                发送附件
              </Button>
            )}
          </div>

          <Sender
            value={input}
            loading={sending}
            disabled={!chatReady || recording}
            placeholder={recording ? "正在录音" : inputPlaceholder}
            submitType="enter"
            onChange={(value) => setInput(value)}
            onSubmit={(value) => void submitMessage(value)}
            onCancel={stopGeneration}
            prefix={
              sending ? (
                <Tooltip title="停止生成">
                  <Button icon={<Square size={14} />} onClick={stopGeneration} />
                </Tooltip>
              ) : undefined
            }
            footer={() => (
              <Flex justify="space-between" align="center" wrap gap={8}>
                <Typography.Text type="secondary">
                  {chatReady
                    ? buildComposerStatus(selectedModelLabel, pendingAttachments.length, speechEnabled, recording)
                    : "模型缺失时不会伪造回复。"}
                </Typography.Text>
                <Space size={4}>
                  <Tooltip title="重新生成上一条">
                    <Button
                      type="text"
                      size="small"
                      icon={<RotateCcw size={14} />}
                      disabled={sending || activeConversation.messages.length === 0}
                      onClick={regenerate}
                    />
                  </Tooltip>
                </Space>
              </Flex>
            )}
          />
        </footer>
      </main>

      <Drawer
        title="状态"
        width={420}
        open={statusDrawerOpen}
        onClose={() => setStatusDrawerOpen(false)}
      >
        <StatusPanel
          runtimeStatus={runtimeStatus}
          multimodalStatus={multimodalStatus}
          loading={loadingStatus}
          onOpenSettings={openSettings}
        />
      </Drawer>

      <Drawer
        title="Settings"
        width={560}
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
      >
        <SettingsPanel
          section={settingsSection}
          onSectionChange={setSettingsSection}
          runtimeStatus={runtimeStatus}
          models={models}
          installedModels={installedModels}
          catalog={catalog}
          multimodalStatus={multimodalStatus}
          runtimeAction={runtimeAction}
          prepareResult={prepareResult}
          onCopyText={copyText}
          onPrepareNativeRuntime={runNativePrepare}
          onUnloadRuntimeSession={runSessionUnload}
        />
      </Drawer>
    </div>
  );
}

function StatusPill({
  label,
  value,
  tone,
  onClick
}: {
  label: string;
  value: string;
  tone: "default" | "success" | "warning";
  onClick: () => void;
}) {
  return (
    <button className={`status-pill status-pill-${tone}`} type="button" onClick={onClick}>
      <span className="status-pill-label">{label}</span>
      <span className="status-pill-value">{value}</span>
    </button>
  );
}

function StatusPanel({
  runtimeStatus,
  multimodalStatus,
  loading,
  onOpenSettings
}: {
  runtimeStatus?: RuntimeStatusResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  loading: boolean;
  onOpenSettings: (section: SettingsSection) => void;
}) {
  if (loading && !runtimeStatus) {
    return <Spin />;
  }

  if (!runtimeStatus) {
    return <Empty description="暂无状态" />;
  }

  const warningDiagnostics = runtimeStatus.diagnostics.filter(
    (item) => item.severity !== "info" || item.status !== "ok"
  );

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Alert
        type={runtimeStatus.status === "ok" ? "success" : "warning"}
        showIcon
        message={runtimeStatus.runtime.message}
        description={runtimeStatus.native_bundle.message}
      />

      <Flex gap={8} wrap>
        <Button icon={<Wrench size={16} />} onClick={() => onOpenSettings("runtime")}>
          Runtime
        </Button>
        <Button icon={<FolderOpen size={16} />} onClick={() => onOpenSettings("files")}>
          Files
        </Button>
      </Flex>

      <List
        size="small"
        header="诊断"
        dataSource={warningDiagnostics.slice(0, 8)}
        locale={{ emptyText: "没有需要处理的诊断" }}
        renderItem={(item) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space>
                  <Tag color={tagColor(item.status)}>{item.status}</Tag>
                  {item.name}
                </Space>
              }
              description={item.message}
            />
          </List.Item>
        )}
      />

      <List
        size="small"
        header="多模态后端"
        dataSource={multimodalStatus?.backends ?? []}
        locale={{ emptyText: "暂无多模态状态" }}
        renderItem={(backend) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space>
                  <Tag color={tagColor(backend.status)}>{backend.status}</Tag>
                  {backend.display_name}
                </Space>
              }
              description={backend.message}
            />
          </List.Item>
        )}
      />
    </Space>
  );
}

function SettingsPanel({
  section,
  onSectionChange,
  runtimeStatus,
  models,
  installedModels,
  catalog,
  multimodalStatus,
  runtimeAction,
  prepareResult,
  onCopyText,
  onPrepareNativeRuntime,
  onUnloadRuntimeSession
}: {
  section: SettingsSection;
  onSectionChange: (section: SettingsSection) => void;
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  installedModels?: InstalledModelsResponse;
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  runtimeAction: "prepare" | "unload" | null;
  prepareResult?: NativeBundlePrepareResult;
  onCopyText: (text: string, successMessage?: string) => Promise<void>;
  onPrepareNativeRuntime: () => Promise<void>;
  onUnloadRuntimeSession: () => Promise<void>;
}) {
  const visibleModels = installedModels?.visible_models ?? [];
  const installedPackages = installedModels?.packages ?? [];
  const recommendedPackages = (catalog?.packages ?? []).filter((item) => item.recommended);
  const downloadablePackages = (catalog?.packages ?? []).filter((item) => !item.installed);

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Segmented
        block
        value={section}
        onChange={(value) => onSectionChange(value as SettingsSection)}
        options={[
          { label: "General", value: "general" },
          { label: "Models", value: "models" },
          { label: "Downloads", value: "downloads" },
          { label: "Runtime", value: "runtime" },
          { label: "API", value: "api" },
          { label: "Files", value: "files" },
          { label: "Advanced", value: "advanced" }
        ]}
      />

      {section === "general" && (
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="Version">{runtimeStatus?.version ?? "-"}</Descriptions.Item>
          <Descriptions.Item label="Data directory">
            {runtimeStatus?.paths.data_directory ?? "-"}
          </Descriptions.Item>
          <Descriptions.Item label="Theme">System</Descriptions.Item>
          <Descriptions.Item label="Startup">tomur open / tomur serve</Descriptions.Item>
        </Descriptions>
      )}

      {section === "models" && (
        <Space direction="vertical" size={16} className="drawer-stack">
          <Card
            size="small"
            title={`可见模型 ${visibleModels.length}`}
            extra={
              <Button
                type="text"
                size="small"
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur list --data-dir <path>", "已复制查看模型命令")}
              >
                CLI
              </Button>
            }
          >
            <List
              dataSource={visibleModels}
              locale={{ emptyText: "当前没有可见模型" }}
              renderItem={(model) => (
                <List.Item>
                  <List.Item.Meta
                    title={
                      <Space wrap>
                        <span>{model.id}</span>
                        {model.verified ? <Tag color="green">verified</Tag> : <Tag>local</Tag>}
                      </Space>
                    }
                    description={`${model.family} / ${model.format} / ${model.quantization_level}`}
                  />
                </List.Item>
              )}
            />
          </Card>

          <Card
            size="small"
            title={`已安装包 ${installedPackages.length}`}
            extra={
              <Button
                type="text"
                size="small"
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur ps --data-dir <path>", "已复制模型可见性命令")}
              >
                PS
              </Button>
            }
          >
            <List
              dataSource={installedPackages}
              locale={{ emptyText: "当前没有安装包记录" }}
              renderItem={(item) => (
                <List.Item>
                  <List.Item.Meta
                    title={
                      <Space wrap>
                        <span>{item.display_name}</span>
                        <Tag color={tagColor(item.status)}>{item.status}</Tag>
                      </Space>
                    }
                    description={`${item.id} / ${item.segment} / ${formatRelativeTime(item.updated_at_utc)}`}
                  />
                </List.Item>
              )}
            />
          </Card>

          <Card
            size="small"
            title="起步建议"
            extra={
              <Button
                type="primary"
                size="small"
                icon={<Download size={14} />}
                onClick={() =>
                  void onCopyText(
                    "tomur pull recommended",
                    "已复制推荐模型下载命令"
                  )
                }
              >
                复制推荐命令
              </Button>
            }
          >
            <Space direction="vertical" size={10} className="drawer-stack">
              <Typography.Text type="secondary">
                浏览器工作台当前只提供模型状态和下一步指引；模型下载、导入和删除仍通过 Tomur CLI 完成。
              </Typography.Text>
              <Collapse
                size="small"
                items={installedPackages.slice(0, 6).map((item) => ({
                  key: item.id,
                  label: item.display_name,
                  children: (
                    <PackageDetails packageItem={item} assets={item.assets} />
                  )
                }))}
              />
            </Space>
          </Card>
        </Space>
      )}

      {section === "downloads" && (
        <Space direction="vertical" size={16} className="drawer-stack">
          <Card
            size="small"
            title="当前机器建议"
            extra={<HardDrive size={16} />}
          >
            <Space direction="vertical" size={8} className="drawer-stack">
              <Typography.Text>
                硬件档位：<Tag color="blue">{catalog?.hardware.tier ?? "-"}</Tag>
              </Typography.Text>
              <Typography.Text type="secondary">
                {(catalog?.hardware.recommendations ?? []).join(" ")}
              </Typography.Text>
            </Space>
          </Card>

          <Card
            size="small"
            title={`推荐包 ${recommendedPackages.length}`}
            extra={
              <Button
                type="primary"
                size="small"
                icon={<Download size={14} />}
                onClick={() =>
                  void onCopyText(
                    "tomur pull recommended",
                    "已复制推荐下载命令"
                  )
                }
              >
                推荐下载
              </Button>
            }
          >
            <List
              dataSource={recommendedPackages}
              locale={{ emptyText: "暂无推荐包" }}
              renderItem={(item) => (
                <List.Item
                  actions={[
                    <Button
                      key={`${item.id}-copy`}
                      type="text"
                      size="small"
                      onClick={() =>
                        void onCopyText(`tomur pull ${item.id}`, `已复制 ${item.id} 下载命令`)
                      }
                    >
                      复制命令
                    </Button>
                  ]}
                >
                  <List.Item.Meta
                    title={
                      <Space wrap>
                        <span>{item.display_name}</span>
                        <Tag color="blue">recommended</Tag>
                        {item.installed ? <Tag color="green">installed</Tag> : null}
                      </Space>
                    }
                    description={renderCatalogSummary(item)}
                  />
                </List.Item>
              )}
            />
          </Card>

          <Card
            size="small"
            title={`可下载包 ${downloadablePackages.length}`}
            extra={
              <Button
                type="text"
                size="small"
                icon={<Layers3 size={14} />}
                onClick={() =>
                  void onCopyText("tomur list --catalog", "已复制 catalog 查看命令")
                }
              >
                查看完整 Catalog
              </Button>
            }
          >
            <Collapse
              size="small"
              items={downloadablePackages.slice(0, 12).map((item) => ({
                key: item.id,
                label: (
                  <Flex justify="space-between" align="center" gap={12} style={{ width: "100%" }}>
                    <Space wrap>
                      <span>{item.display_name}</span>
                      {item.optional ? <Tag>optional</Tag> : null}
                      {item.research ? <Tag color="purple">research</Tag> : null}
                    </Space>
                    <Tag color={tagColor(item.install_status)}>{item.install_status}</Tag>
                  </Flex>
                ),
                children: (
                  <DownloadPackageDetails
                    item={item}
                    onCopyText={onCopyText}
                  />
                )
              }))}
            />
          </Card>

          <Alert
            type="info"
            showIcon
            message="Web UI 当前不直接执行下载"
            description="Tomur 现阶段仍通过 CLI 执行下载、断点续传、校验和修复。这里提供推荐、状态和可复制命令，避免把未接通的动作伪装成可用功能。"
          />
        </Space>
      )}

      {section === "runtime" && (
        <RuntimeSettingsPanel
          runtimeStatus={runtimeStatus}
          prepareResult={prepareResult}
          runtimeAction={runtimeAction}
          onPrepareNativeRuntime={onPrepareNativeRuntime}
          onUnloadRuntimeSession={onUnloadRuntimeSession}
          onCopyText={onCopyText}
        />
      )}

      {section === "api" && (
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="URL">{runtimeStatus?.port.url ?? "-"}</Descriptions.Item>
          <Descriptions.Item label="Port status">{runtimeStatus?.port.status ?? "-"}</Descriptions.Item>
          <Descriptions.Item label="OpenAI models">/v1/models</Descriptions.Item>
          <Descriptions.Item label="OpenAI chat">/v1/chat/completions</Descriptions.Item>
          <Descriptions.Item label="Ollama chat">/api/chat</Descriptions.Item>
        </Descriptions>
      )}

      {section === "files" && (
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="Models">
            {runtimeStatus?.paths.models_directory ?? "-"}
          </Descriptions.Item>
          <Descriptions.Item label="Runtime">
            {runtimeStatus?.paths.runtime_directory ?? "-"}
          </Descriptions.Item>
          <Descriptions.Item label="Logs">
            {runtimeStatus?.paths.logs_directory ?? "-"}
          </Descriptions.Item>
          <Descriptions.Item label="Database">
            {runtimeStatus?.paths.database_path ?? "-"}
          </Descriptions.Item>
        </Descriptions>
      )}

      {section === "advanced" && (
        <List
          dataSource={multimodalStatus?.backends ?? []}
          locale={{ emptyText: "暂无高级能力状态" }}
          renderItem={(backend) => (
            <List.Item>
              <List.Item.Meta
                title={
                  <Space>
                    <Tag color={tagColor(backend.status)}>{backend.status}</Tag>
                    {backend.capability}
                  </Space>
                }
                description={backend.message}
              />
            </List.Item>
          )}
        />
      )}
    </Space>
  );
}

function RuntimeSettingsPanel({
  runtimeStatus,
  prepareResult,
  runtimeAction,
  onPrepareNativeRuntime,
  onUnloadRuntimeSession,
  onCopyText
}: {
  runtimeStatus?: RuntimeStatusResponse;
  prepareResult?: NativeBundlePrepareResult;
  runtimeAction: "prepare" | "unload" | null;
  onPrepareNativeRuntime: () => Promise<void>;
  onUnloadRuntimeSession: () => Promise<void>;
  onCopyText: (text: string, successMessage?: string) => Promise<void>;
}) {
  const diagnostics = runtimeStatus?.diagnostics ?? [];
  const visibleDiagnostics = diagnostics.filter(
    (item) => item.severity !== "info" || item.status !== "ok"
  );
  const nativeReady = runtimeStatus?.native_bundle.status === "ok";
  const sessionLoaded = runtimeStatus?.runtime.code === "runtime_loaded";
  const prepareChangedFiles =
    prepareResult?.files.filter((file) =>
      ["copied", "repaired", "aliased", "error"].includes(file.status)
    ) ?? [];
  const primaryDiagnostic = visibleDiagnostics.at(0);
  const runtimeHints = collectRuntimeHints(runtimeStatus, visibleDiagnostics);

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Alert
        type={runtimeStatus?.status === "ok" ? "success" : "warning"}
        showIcon
        message={runtimeStatus?.runtime.message ?? "Runtime 状态尚未加载"}
        description={runtimeStatus?.native_bundle.message ?? "刷新状态后可以查看 native bundle、session 和诊断动作。"}
      />

      <Card
        size="small"
        title="Native runtime"
        extra={<Tag color={tagColor(runtimeStatus?.native_bundle.status ?? "checking")}>{runtimeStatus?.native_bundle.status ?? "checking"}</Tag>}
      >
        <ActionBlock
          title={nativeReady ? "Bundle 已准备" : "准备或修复 native bundle"}
          description={
            nativeReady
              ? "托管 runtime 目录中的 native library 已通过当前探测。需要重新释放或修复时可以再次执行 prepare。"
              : "执行后端 prepare API，从随包 native bundle 释放或修复 Tomur 管理目录中的 runtime 文件。"
          }
          nextStep={
            nativeReady
              ? "下一步：发送一次 Chat 请求会按需加载 llama.cpp session。"
              : "下一步：prepare 完成后重新查看组件状态；若仍失败，运行 doctor 查看缺失或校验错误。"
          }
          action={
            <Space wrap>
              <Button
                type={nativeReady ? "default" : "primary"}
                icon={<Wrench size={14} />}
                loading={runtimeAction === "prepare"}
                disabled={runtimeAction === "unload"}
                onClick={() => void onPrepareNativeRuntime()}
              >
                {nativeReady ? "重新准备" : "准备 runtime"}
              </Button>
              <Button
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur native prepare", "已复制 native prepare 命令")}
              >
                复制 CLI
              </Button>
            </Space>
          }
        />

        {prepareResult && (
          <Alert
            className="runtime-result"
            type={prepareResult.status === "error" ? "warning" : "success"}
            showIcon
            message={prepareResult.message}
            description={`${prepareChangedFiles.length} 个文件在最近一次 prepare 中发生复制、修复或错误。`}
          />
        )}
        {prepareChangedFiles.length > 0 && (
          <List
            className="runtime-result-list"
            size="small"
            dataSource={prepareChangedFiles.slice(0, 5)}
            renderItem={(file) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(file.status)}>{file.status}</Tag>
                      {file.destination_path}
                    </Space>
                  }
                  description={file.message}
                />
              </List.Item>
            )}
          />
        )}
      </Card>

      <Card
        size="small"
        title="Session"
        extra={<Tag color={tagColor(runtimeStatus?.runtime.status ?? "checking")}>{runtimeStatus?.runtime.status ?? "checking"}</Tag>}
      >
        <ActionBlock
          title={sessionLoaded ? "当前已有加载的 session" : "当前没有加载的 session"}
          description={
            sessionLoaded
              ? "卸载会释放当前 llama.cpp session；下一次 Chat、completion 或 embedding 请求会按需重新加载。"
              : "Tomur 采用首个兼容请求按需加载 session。没有加载时无需手动 unload。"
          }
          nextStep={
            sessionLoaded
              ? "下一步：如果要切换模型或释放内存，先卸载 session，再发起新的请求。"
              : "下一步：选择一个可见 GGUF 模型并发送 Chat 请求，runtime 会加载首个 session。"
          }
          action={
            <Space wrap>
              <Button
                danger={sessionLoaded}
                icon={<Trash2 size={14} />}
                loading={runtimeAction === "unload"}
                disabled={!sessionLoaded || runtimeAction === "prepare"}
                onClick={() => void onUnloadRuntimeSession()}
              >
                卸载 session
              </Button>
              <Button
                icon={<Copy size={14} />}
                onClick={() =>
                  void onCopyText(
                    "POST /api/runtime/session/unload",
                    "已复制 session unload API"
                  )
                }
              >
                复制 API
              </Button>
            </Space>
          }
        />
      </Card>

      <Card
        size="small"
        title="诊断与后端提示"
        extra={
          primaryDiagnostic ? (
            <Tag color={tagColor(primaryDiagnostic.status)}>{primaryDiagnostic.status}</Tag>
          ) : (
            <Tag color="green">ok</Tag>
          )
        }
      >
        <Space direction="vertical" size={12} className="drawer-stack">
          <Typography.Text type="secondary">
            {primaryDiagnostic?.message ?? "当前没有需要处理的 runtime 诊断。"}
          </Typography.Text>
          <List
            size="small"
            dataSource={runtimeHints}
            locale={{ emptyText: "暂无后端提示" }}
            renderItem={(item) => <List.Item>{item}</List.Item>}
          />
          <Space wrap>
            <Button
              icon={<RefreshCcw size={14} />}
              onClick={() =>
                void onCopyText("GET /api/runtime/status", "已复制 runtime status API")
              }
            >
              复制状态 API
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() => void onCopyText("tomur doctor", "已复制 doctor 命令")}
            >
              复制 doctor
            </Button>
          </Space>
        </Space>
      </Card>

      <List
        dataSource={runtimeStatus?.native_bundle.components ?? []}
        locale={{ emptyText: "暂无 native component 状态" }}
        renderItem={(item) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space>
                  <Tag color={tagColor(item.status)}>{item.status}</Tag>
                  {item.display_name}
                </Space>
              }
              description={item.message}
            />
          </List.Item>
        )}
      />
    </Space>
  );
}

function ActionBlock({
  title,
  description,
  nextStep,
  action
}: {
  title: string;
  description: string;
  nextStep: string;
  action: ReactNode;
}) {
  return (
    <div className="runtime-action">
      <div className="runtime-action-copy">
        <Typography.Text strong>{title}</Typography.Text>
        <Typography.Text type="secondary">{description}</Typography.Text>
        <Typography.Text className="runtime-next-step">{nextStep}</Typography.Text>
      </div>
      <div className="runtime-action-controls">{action}</div>
    </div>
  );
}

function collectRuntimeHints(
  runtimeStatus: RuntimeStatusResponse | undefined,
  diagnostics: DiagnosticItem[]
) {
  const actions = [
    ...(runtimeStatus?.runtime.actions ?? []),
    ...diagnostics.flatMap((diagnostic) => diagnostic.actions)
  ];

  return Array.from(new Set(actions)).slice(0, 6);
}

function PackageDetails({
  packageItem,
  assets
}: {
  packageItem: InstalledModelPackage;
  assets: InstalledModelAsset[];
}) {
  return (
    <Space direction="vertical" size={10} className="drawer-stack">
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Package ID">{packageItem.id}</Descriptions.Item>
        <Descriptions.Item label="Directory">{packageItem.directory}</Descriptions.Item>
        <Descriptions.Item label="Primary path">{packageItem.primary_path}</Descriptions.Item>
        <Descriptions.Item label="License notice">{packageItem.license_notice}</Descriptions.Item>
      </Descriptions>
      <List
        size="small"
        dataSource={assets}
        locale={{ emptyText: "当前包没有资产记录" }}
        renderItem={(asset) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space wrap>
                  <span>{asset.path}</span>
                  {asset.sha256_verified ? <Tag color="green">sha256</Tag> : <Tag>unchecked</Tag>}
                </Space>
              }
              description={`${asset.source_repository_id} / ${formatBytes(asset.size_bytes)}`}
            />
          </List.Item>
        )}
      />
    </Space>
  );
}

function DownloadPackageDetails({
  item,
  onCopyText
}: {
  item: ModelCatalogPackage;
  onCopyText: (text: string, successMessage?: string) => Promise<void>;
}) {
  return (
    <Space direction="vertical" size={12} className="drawer-stack">
      <Typography.Text type="secondary">{item.description}</Typography.Text>
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Package ID">{item.id}</Descriptions.Item>
        <Descriptions.Item label="Task">{item.task}</Descriptions.Item>
        <Descriptions.Item label="Runtime">{item.runtime}</Descriptions.Item>
        <Descriptions.Item label="Primary file">{item.primary_file_name ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Recommended tier">{item.hardware_tier}</Descriptions.Item>
        <Descriptions.Item label="Minimum memory">
          {item.minimum_memory_bytes ? formatBytes(item.minimum_memory_bytes) : "-"}
        </Descriptions.Item>
        <Descriptions.Item label="License">{item.license ?? "-"}</Descriptions.Item>
      </Descriptions>

      <Space wrap>
        <Button
          size="small"
          type="primary"
          icon={<Download size={14} />}
          onClick={() => void onCopyText(`tomur pull ${item.id}`, `已复制 ${item.id} 下载命令`)}
        >
          复制下载命令
        </Button>
        <Button
          size="small"
          onClick={() =>
            void onCopyText(`tomur pull ${item.id} --force`, `已复制 ${item.id} 强制重装命令`)
          }
        >
          复制重装命令
        </Button>
      </Space>

      <List
        size="small"
        header="远端资产"
        dataSource={item.assets}
        locale={{ emptyText: "暂无远端资产明细" }}
        renderItem={(asset) => <List.Item>{renderAssetSummary(asset)}</List.Item>}
      />

      <List
        size="small"
        header="Bundle sidecar"
        dataSource={item.bundle_assets}
        locale={{ emptyText: "当前包没有 sidecar bundle 资产" }}
        renderItem={(asset) => <List.Item>{renderBundleAssetSummary(asset)}</List.Item>}
      />
    </Space>
  );
}

function renderCatalogSummary(item: ModelCatalogPackage) {
  const parts = [
    item.task,
    item.runtime,
    item.quantization,
    item.size_bytes ? formatBytes(item.size_bytes) : null
  ].filter(Boolean);

  return parts.join(" / ");
}

function renderAssetSummary(asset: ModelCatalogAsset) {
  return `${asset.repository_id} / ${asset.relative_path} -> ${asset.target_relative_path}`;
}

function renderBundleAssetSummary(asset: ModelCatalogBundleAsset) {
  const required = asset.is_required ? "required" : "optional";
  const details = [asset.role, required, asset.file_name, asset.size_bytes ? formatBytes(asset.size_bytes) : null]
    .filter(Boolean)
    .join(" / ");

  return `${details} - ${asset.description}`;
}

function MessageFooter({
  message,
  onCopy
}: {
  message: ChatMessage;
  onCopy: () => void;
}) {
  const diagnostics = message.diagnostics ?? [];
  const artifacts = message.artifacts ?? [];

  return (
    <div className="message-footer">
      {message.audioUrl && (
        <audio className="message-audio" controls src={message.audioUrl} aria-label="助手语音回复" />
      )}
      {(diagnostics.length > 0 || artifacts.length > 0) && (
        <Space size={6} wrap className="message-meta">
          {diagnostics.slice(0, 3).map((diagnostic) => (
            <Tooltip key={diagnostic.id} title={diagnostic.message}>
              <Tag color={tagColor(diagnostic.status)}>{diagnostic.code}</Tag>
            </Tooltip>
          ))}
          {artifacts.slice(0, 3).map((artifact) => (
            <Tooltip key={artifact.id} title={artifact.path ?? artifact.source ?? artifact.id}>
              <Tag color={tagColor(artifact.status)}>{artifact.type}</Tag>
            </Tooltip>
          ))}
        </Space>
      )}
      <Actions
        items={[
          {
            key: "copy",
            icon: <Copy size={14} />,
            label: "复制",
            onItemClick: onCopy
          }
        ]}
      />
    </div>
  );
}

function buildTurnMessages(
  response: ConversationTurnResponse,
  assistantFallbackId: string
): ChatMessage[] {
  const artifactMap = createArtifactMap(response.artifacts);
  const messages = [
    mapConversationMessage(response.user_message, response.diagnostics, artifactMap),
    response.tool_message
      ? mapConversationMessage(response.tool_message, response.diagnostics, artifactMap)
      : null,
    response.assistant_message
      ? mapConversationMessage(
          response.assistant_message,
          response.diagnostics,
          artifactMap,
          response.speech_artifact,
          response.speech_media_type
        )
      : null
  ].filter((item): item is ChatMessage => item !== null);

  if (messages.some((item) => item.role === "assistant")) {
    return messages;
  }

  return [
    ...messages,
    {
      id: assistantFallbackId,
      role: "assistant",
      content: summarizeDiagnostics(response.diagnostics) || "本轮没有生成助手回复。",
      status: response.status === "ok" ? "success" : "error",
      diagnostics: response.diagnostics,
      artifacts: response.artifacts,
      audioUrl: response.speech_artifact
        ? getConversationArtifactContentUrl(response.speech_artifact.conversation_id, response.speech_artifact.id)
        : undefined,
      audioMediaType: response.speech_media_type
    }
  ];
}

function buildVoiceTurnMessages(
  response: ConversationVoiceTurnResponse,
  userFallbackId: string,
  assistantFallbackId: string
): ChatMessage[] {
  const artifacts = [
    ...(response.turn?.artifacts ?? []),
    response.input_artifact,
    response.speech_artifact
  ].filter((item): item is ConversationArtifactRecord => Boolean(item));
  const artifactMap = createArtifactMap(artifacts);
  const userMessage = response.user_message
    ? mapConversationMessage(response.user_message, response.diagnostics, artifactMap)
    : {
        id: userFallbackId,
        role: "user" as const,
        content: response.transcript ? `语音输入：${response.transcript}` : "语音输入",
        status: response.status === "error" ? "error" : "success",
        transcript: response.transcript,
        artifacts: response.input_artifact ? [response.input_artifact] : [],
        diagnostics: response.diagnostics
      };
  const toolMessage = response.tool_message
    ? mapConversationMessage(response.tool_message, response.diagnostics, artifactMap)
    : null;
  const assistantMessage = response.assistant_message
    ? mapConversationMessage(
        response.assistant_message,
        response.diagnostics,
        artifactMap,
        response.speech_artifact,
        response.speech_media_type
      )
    : {
        id: assistantFallbackId,
        role: "assistant" as const,
        content: summarizeDiagnostics(response.diagnostics) || "语音回合没有生成助手回复。",
        status: response.status === "ok" ? "success" : "error",
        diagnostics: response.diagnostics,
        artifacts: response.speech_artifact ? [response.speech_artifact] : [],
        audioUrl: response.speech_artifact
          ? getConversationArtifactContentUrl(response.speech_artifact.conversation_id, response.speech_artifact.id)
          : undefined,
        audioMediaType: response.speech_media_type
      };

  return [userMessage, toolMessage, assistantMessage].filter(
    (item): item is ChatMessage => item !== null
  );
}

function mapConversationMessage(
  record: ConversationMessageRecord,
  diagnostics: ConversationDiagnosticRecord[],
  artifactsById: Map<string, ConversationArtifactRecord>,
  speechArtifact?: ConversationArtifactRecord | null,
  speechMediaType?: string | null
): ChatMessage {
  const role = normalizeRole(record.role);
  const artifacts = [
    ...record.artifact_ids
      .map((id) => artifactsById.get(id))
      .filter((item): item is ConversationArtifactRecord => Boolean(item)),
    speechArtifact ?? null
  ].filter((item): item is ConversationArtifactRecord => Boolean(item));

  return {
    id: record.id,
    role,
    content: role === "user" && record.modality === "audio"
      ? `语音输入：${record.content}`
      : record.content,
    status: record.status === "ok" ? "success" : record.status === "partial" ? "success" : "error",
    attachments: record.attachments,
    artifacts,
    diagnostics: diagnostics.length > 0 ? diagnostics : undefined,
    audioUrl: speechArtifact
      ? getConversationArtifactContentUrl(speechArtifact.conversation_id, speechArtifact.id)
      : undefined,
    audioMediaType: speechMediaType,
    transcript: record.modality === "audio" ? record.content : undefined
  };
}

function replaceTurnMessages(
  messages: ChatMessage[],
  userMessageId: string,
  assistantMessageId: string,
  replacements: ChatMessage[]
): ChatMessage[] {
  const result: ChatMessage[] = [];
  let replaced = false;
  for (const message of messages) {
    if (message.id === userMessageId) {
      result.push(...replacements);
      replaced = true;
      continue;
    }

    if (message.id === assistantMessageId) {
      continue;
    }

    result.push(message);
  }

  return replaced ? result : [...messages, ...replacements];
}

function createArtifactMap(artifacts: ConversationArtifactRecord[]) {
  return new Map(artifacts.map((artifact) => [artifact.id, artifact]));
}

function normalizeRole(role: string): ChatMessage["role"] {
  if (role === "user" || role === "assistant" || role === "system" || role === "tool") {
    return role;
  }

  return "assistant";
}

function summarizeDiagnostics(diagnostics: ConversationDiagnosticRecord[]) {
  const diagnostic = diagnostics.find((item) => item.status === "error") ?? diagnostics.at(0);
  return diagnostic?.message ?? "";
}

function resolveConversationTitle(
  conversation: Conversation,
  record: ConversationRecord,
  fallback?: string | null
) {
  if (conversation.title === "新会话" || conversation.title === "语音会话") {
    return createTitle(fallback || record.title || conversation.title);
  }

  return record.title || conversation.title;
}

function parseApiTime(value?: string | null) {
  if (!value) {
    return Date.now();
  }

  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? Date.now() : parsed;
}

async function syncPlainMessagesToBackend(
  conversationId: string,
  messages: ChatMessage[],
  signal?: AbortSignal
) {
  for (const message of messages) {
    if (
      message.status === "loading" ||
      message.attachments?.length ||
      message.artifacts?.length ||
      message.role === "tool"
    ) {
      continue;
    }

    const content = message.content.trim();
    if (!content) {
      continue;
    }

    await appendConversationMessage(
      conversationId,
      {
        role: message.role,
        content,
        modality: "text",
        status: message.status === "error" ? "error" : "ok"
      },
      signal
    );
  }
}

async function appendPlainTurnToBackend(
  conversationId: string,
  model: string,
  userContent: string,
  assistantContent: string,
  signal?: AbortSignal
) {
  await appendConversationMessage(
    conversationId,
    {
      role: "user",
      content: userContent,
      modality: "text",
      status: "ok",
      model
    },
    signal
  );
  await appendConversationMessage(
    conversationId,
    {
      role: "assistant",
      content: assistantContent,
      modality: "text",
      status: "ok",
      model
    },
    signal
  );
}

async function createLocalAttachment(file: File): Promise<ConversationAttachment> {
  const mediaType = file.type || resolveMediaTypeFromFileName(file.name);
  if (mediaType.startsWith("image/") || mediaType.startsWith("audio/")) {
    return {
      id: crypto.randomUUID(),
      type: mediaType.startsWith("image/") ? "image" : "audio",
      name: file.name,
      media_type: mediaType,
      bytes: file.size,
      data_uri: await readFileAsDataUri(file)
    };
  }

  if (isTextAttachment(file, mediaType)) {
    return {
      id: crypto.randomUUID(),
      type: "file",
      name: file.name,
      media_type: mediaType || "text/plain",
      bytes: file.size,
      text: await file.text()
    };
  }

  throw new Error("当前前端只支持图片、音频和文本文件附件");
}

function toAttachmentItem(attachment: ConversationAttachment) {
  const mediaType = attachment.media_type ?? "";
  const cardType = mediaType.startsWith("image/")
    ? "image"
    : mediaType.startsWith("audio/")
      ? "audio"
      : "file";

  return {
    uid: attachment.id ?? attachment.name ?? crypto.randomUUID(),
    name: attachment.name ?? "attachment",
    status: "done" as const,
    size: attachment.bytes ?? undefined,
    byte: attachment.bytes ?? undefined,
    type: attachment.media_type ?? undefined,
    cardType,
    description: attachment.type ?? undefined
  };
}

function isTextAttachment(file: File, mediaType: string) {
  const extension = file.name.split(".").pop()?.toLowerCase();
  return (
    mediaType.startsWith("text/") ||
    ["md", "markdown", "json", "csv", "tsv", "txt", "log", "xml", "yaml", "yml"].includes(extension ?? "")
  );
}

function readFileAsDataUri(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("附件读取失败"));
    reader.onload = () => {
      if (typeof reader.result === "string") {
        resolve(reader.result);
        return;
      }

      reject(new Error("附件读取结果无效"));
    };
    reader.readAsDataURL(file);
  });
}

function formatUserContent(content: string, attachments: ConversationAttachment[]) {
  if (attachments.length === 0) {
    return content;
  }

  const summary = summarizeAttachments(attachments);
  return content ? `${content}\n\n${summary}` : summary;
}

function summarizeAttachments(attachments: ConversationAttachment[]) {
  if (attachments.length === 0) {
    return "";
  }

  return `附件：${attachments.map((item) => item.name ?? item.type ?? "attachment").join("、")}`;
}

function buildComposerStatus(
  selectedModel: string | undefined,
  attachmentCount: number,
  speechEnabled: boolean,
  recording: boolean
) {
  if (recording) {
    return "正在录音，停止后会发送语音回合。";
  }

  const parts = [`使用 ${selectedModel} 会话`];
  if (attachmentCount > 0) {
    parts.push(`${attachmentCount} 个附件`);
  }
  if (speechEnabled) {
    parts.push("请求朗读");
  }

  return `${parts.join(" · ")}。`;
}

function resolveMediaTypeFromFileName(name: string) {
  const extension = name.split(".").pop()?.toLowerCase();
  switch (extension) {
    case "png":
      return "image/png";
    case "jpg":
    case "jpeg":
      return "image/jpeg";
    case "webp":
      return "image/webp";
    case "gif":
      return "image/gif";
    case "wav":
      return "audio/wav";
    case "mp3":
      return "audio/mpeg";
    case "ogg":
      return "audio/ogg";
    case "webm":
      return "audio/webm";
    case "json":
      return "application/json";
    case "csv":
      return "text/csv";
    case "md":
    case "markdown":
    case "txt":
    case "log":
      return "text/plain";
    default:
      return "application/octet-stream";
  }
}

function cleanupRecording(
  recorderRef: { current: MediaRecorder | null },
  streamRef: { current: MediaStream | null },
  chunksRef: { current: Blob[] }
) {
  streamRef.current?.getTracks().forEach((track) => track.stop());
  streamRef.current = null;
  recorderRef.current = null;
  chunksRef.current = [];
}

async function convertRecordingToPcmWav(blob: Blob): Promise<Blob> {
  const arrayBuffer = await blob.arrayBuffer();
  const AudioContextClass = getAudioContextConstructor();
  if (!AudioContextClass) {
    throw new Error("当前浏览器不支持音频转码");
  }

  const context = new AudioContextClass();
  try {
    const decoded = await context.decodeAudioData(arrayBuffer.slice(0));
    const mono = mixToMono(decoded);
    const resampled = resampleLinear(mono, decoded.sampleRate, 16_000);
    const wav = encodePcm16Wav(resampled, 16_000);
    return new Blob([wav], { type: "audio/wav" });
  } finally {
    await context.close();
  }
}

function getAudioContextConstructor(): typeof AudioContext | undefined {
  return window.AudioContext ?? (
    window as Window & {
      webkitAudioContext?: typeof AudioContext;
    }
  ).webkitAudioContext;
}

function mixToMono(buffer: AudioBuffer) {
  const output = new Float32Array(buffer.length);
  for (let channel = 0; channel < buffer.numberOfChannels; channel += 1) {
    const data = buffer.getChannelData(channel);
    for (let index = 0; index < data.length; index += 1) {
      output[index] += data[index] / buffer.numberOfChannels;
    }
  }

  return output;
}

function resampleLinear(input: Float32Array, sourceRate: number, targetRate: number) {
  if (sourceRate === targetRate) {
    return input;
  }

  const ratio = sourceRate / targetRate;
  const outputLength = Math.max(1, Math.round(input.length / ratio));
  const output = new Float32Array(outputLength);
  for (let index = 0; index < outputLength; index += 1) {
    const sourceIndex = index * ratio;
    const left = Math.floor(sourceIndex);
    const right = Math.min(left + 1, input.length - 1);
    const fraction = sourceIndex - left;
    output[index] = input[left] * (1 - fraction) + input[right] * fraction;
  }

  return output;
}

function encodePcm16Wav(samples: Float32Array, sampleRate: number) {
  const bytesPerSample = 2;
  const dataLength = samples.length * bytesPerSample;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);

  writeAscii(view, 0, "RIFF");
  view.setUint32(4, 36 + dataLength, true);
  writeAscii(view, 8, "WAVE");
  writeAscii(view, 12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * bytesPerSample, true);
  view.setUint16(32, bytesPerSample, true);
  view.setUint16(34, 16, true);
  writeAscii(view, 36, "data");
  view.setUint32(40, dataLength, true);

  let offset = 44;
  for (const sample of samples) {
    const clamped = Math.max(-1, Math.min(1, sample));
    view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
    offset += bytesPerSample;
  }

  return buffer;
}

function writeAscii(view: DataView, offset: number, value: string) {
  for (let index = 0; index < value.length; index += 1) {
    view.setUint8(offset + index, value.charCodeAt(index));
  }
}

function formatBytes(value?: number | null) {
  if (!value || value <= 0) {
    return "-";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = value;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }

  return `${size >= 10 || unitIndex === 0 ? size.toFixed(0) : size.toFixed(1)} ${units[unitIndex]}`;
}

function formatRelativeTime(value: string) {
  const time = Date.parse(value);
  if (Number.isNaN(time)) {
    return value;
  }

  const diff = Date.now() - time;
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;

  if (diff < hour) {
    return `${Math.max(1, Math.floor(diff / minute))} 分钟前`;
  }

  if (diff < day) {
    return `${Math.floor(diff / hour)} 小时前`;
  }

  return `${Math.floor(diff / day)} 天前`;
}

function createTitle(content: string) {
  return content.length > 20 ? `${content.slice(0, 20)}...` : content;
}

function isChatModel(model: OpenAiModel) {
  const capabilities = model.capabilities ?? [];
  if (capabilities.length === 0) {
    return model.format === "gguf" || model.format === "ggml" || model.family === "llama";
  }

  return capabilities.some(
    (capability) => capability === "chat" || capability === "completion"
  );
}

function tagColor(status: string) {
  if (
    status === "ok" ||
    status === "ready" ||
    status === "installed" ||
    status === "available" ||
    status === "prepared" ||
    status === "unchanged"
  ) {
    return "green";
  }

  if (status === "error" || status === "blocked") {
    return "red";
  }

  if (
    status === "warning" ||
    status === "partial" ||
    status === "not_configured" ||
    status === "copied" ||
    status === "repaired" ||
    status === "aliased"
  ) {
    return "gold";
  }

  return "default";
}

export default App;
