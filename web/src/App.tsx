import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  App as AntApp,
  Badge,
  Button,
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
  FileText,
  FolderOpen,
  MessageSquarePlus,
  Mic,
  PanelRightOpen,
  Paperclip,
  RefreshCcw,
  RotateCcw,
  Settings,
  Square,
  Trash2,
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
  getInstalledModels,
  getModelCatalog,
  getModels,
  getMultimodalStatus,
  getRuntimeStatus,
  getVersion,
  sendChatCompletion
} from "./api";
import type {
  ChatMessage,
  Conversation,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
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
    label: "检查本地 runtime 状态",
    description: "读取 Tomur 当前诊断并解释可用能力"
  },
  {
    key: "models",
    label: "列出本地可见模型",
    description: "说明哪些模型可用于 Chat 或多模态入口"
  },
  {
    key: "setup",
    label: "我该先准备什么",
    description: "根据本机状态给出下一步建议"
  }
];

const promptText: Record<string, string> = {
  runtime: "请根据当前 Tomur 本地 runtime 状态，说明哪些能力已经可用，哪些需要准备。",
  models: "请列出 Tomur 当前可见的本地模型，并说明哪个适合用于 Chat。",
  setup: "请根据 Tomur 当前本机状态，给我一个最小可行的准备步骤。"
};

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
  const [settingsSection, setSettingsSection] = useState("runtime");
  const [statusDrawerOpen, setStatusDrawerOpen] = useState(false);
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [sending, setSending] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const activeConversation = useMemo(
    () =>
      conversations.find((conversation) => conversation.id === activeConversationId) ??
      conversations[0],
    [activeConversationId, conversations]
  );

  const selectedModelLabel = selectedModel ?? models.at(0)?.id;
  const chatReady = Boolean(selectedModelLabel);
  const runtimeSeverity = runtimeStatus?.status === "ok" ? "success" : "warning";
  const visibleChatModels = useMemo(
    () =>
      models.map((model) => ({
        value: model.id,
        label: model.id
      })),
    [models]
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
      setSelectedModel((current) => current ?? nextModels.data.at(0)?.id);
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Tomur 状态刷新失败");
    } finally {
      setLoadingStatus(false);
    }

    return () => controller.abort();
  }, [message]);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

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
        const next = conversations.find((conversation) => conversation.id !== conversationId);
        setActiveConversationId(next?.id ?? initialConversationId);
      }
    },
    [activeConversationId, conversations]
  );

  const submitMessage = useCallback(
    async (content: string) => {
      const trimmed = content.trim();
      const model = selectedModelLabel;
      if (!trimmed || sending) {
        return;
      }

      if (!model) {
        setSettingsSection("models");
        setSettingsOpen(true);
        message.warning("Tomur 当前没有可见 Chat 模型");
        return;
      }

      const conversationId = activeConversation.id;
      const userMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: trimmed,
        status: "local"
      };
      const assistantMessage: ChatMessage = {
        id: crypto.randomUUID(),
        role: "assistant",
        content: "",
        status: "loading"
      };

      setInput("");
      updateConversation(conversationId, (conversation) => ({
        ...conversation,
        title: conversation.messages.length === 0 ? createTitle(trimmed) : conversation.title,
        updatedAt: Date.now(),
        messages: [...conversation.messages, userMessage, assistantMessage]
      }));

      const controller = new AbortController();
      abortRef.current = controller;
      setSending(true);

      try {
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
    },
    [
      activeConversation.id,
      activeConversation.messages,
      message,
      selectedModelLabel,
      sending,
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

  const bubbleItems: BubbleItemType[] = activeConversation.messages.map((item) => ({
    key: item.id,
    role: item.role === "assistant" ? "ai" : "user",
    content: item.content,
    status: item.status,
    loading: item.status === "loading" && !item.content,
    footer:
      item.role === "assistant"
        ? () => (
            <Actions
              items={[
                {
                  key: "copy",
                  icon: <Copy size={14} />,
                  label: "复制",
                  onItemClick: () => {
                    void navigator.clipboard.writeText(item.content);
                    message.success("已复制");
                  }
                }
              ]}
            />
          )
        : undefined
  }));

  const conversationItems = conversations.map((conversation) => ({
    key: conversation.id,
    label: conversation.title,
    group: "Local"
  }));

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div>
            <Typography.Title level={4}>Tomur</Typography.Title>
            <Typography.Text type="secondary">{version?.version ?? "local runtime"}</Typography.Text>
          </div>
          <Tooltip title="新建会话">
            <Button icon={<MessageSquarePlus size={16} />} onClick={startConversation} />
          </Tooltip>
        </div>

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
          <Space size={8} wrap>
            <Badge status={runtimeSeverity} />
            <Button type="text" onClick={() => setStatusDrawerOpen(true)}>
              {runtimeStatus?.status ?? "checking"}
            </Button>
            <Select
              className="model-select"
              placeholder="选择本地模型"
              value={selectedModel}
              options={visibleChatModels}
              onChange={(value) => setSelectedModel(value)}
              disabled={models.length === 0}
            />
          </Space>

          <Space>
            <Tooltip title="刷新状态">
              <Button
                icon={<RefreshCcw size={16} />}
                loading={loadingStatus}
                onClick={() => void refreshStatus()}
              />
            </Tooltip>
            <Tooltip title="状态">
              <Button icon={<PanelRightOpen size={16} />} onClick={() => setStatusDrawerOpen(true)} />
            </Tooltip>
            <Tooltip title="设置">
              <Button icon={<Settings size={16} />} onClick={() => setSettingsOpen(true)} />
            </Tooltip>
          </Space>
        </header>

        {!chatReady && (
          <Alert
            className="inline-diagnostic"
            type="warning"
            showIcon
            message="Tomur 当前没有可见 Chat 模型"
            description="Chat 工作台已连接本地 API。请先通过 CLI 下载或导入模型，然后刷新状态。"
            action={
              <Button size="small" onClick={() => {
                setSettingsSection("models");
                setSettingsOpen(true);
              }}>
                查看模型
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
                description="连接 Tomur 本地 API，使用已安装模型进行对话。"
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
              disabled
              items={[]}
              placeholder={{
                title: "附件入口",
                description: "图片、文件和音频入口会在后端能力接通后启用"
              }}
            >
              <Button icon={<Paperclip size={16} />} disabled>
                附件
              </Button>
            </Attachments>
            <Tooltip title="录音转写入口会在语音回合服务接通后启用">
              <Button icon={<Mic size={16} />} disabled />
            </Tooltip>
            <Tooltip title="文件问答入口会在本地文件索引接通后启用">
              <Button icon={<FileText size={16} />} disabled />
            </Tooltip>
          </div>

          <Sender
            value={input}
            loading={sending}
            disabled={!chatReady}
            placeholder={chatReady ? "输入消息，Enter 发送" : "请先准备本地 Chat 模型"}
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
              <Flex justify="space-between" align="center" wrap>
                <Typography.Text type="secondary">
                  {chatReady
                    ? "使用 Tomur 本地 OpenAI 兼容接口"
                    : "模型缺失时不会伪造回复"}
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
          onOpenSettings={(section) => {
            setSettingsSection(section);
            setSettingsOpen(true);
          }}
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
          onSectionChange={(value) => setSettingsSection(value)}
          runtimeStatus={runtimeStatus}
          models={models}
          installedModels={installedModels}
          catalog={catalog}
          multimodalStatus={multimodalStatus}
        />
      </Drawer>
    </div>
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
  onOpenSettings: (section: string) => void;
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
        dataSource={warningDiagnostics.slice(0, 6)}
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
  multimodalStatus
}: {
  section: string;
  onSectionChange: (section: string) => void;
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  installedModels?: InstalledModelsResponse;
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
}) {
  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Segmented
        block
        value={section}
        onChange={(value) => onSectionChange(String(value))}
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
        <List
          header={`可见模型 ${models.length}`}
          dataSource={models}
          locale={{ emptyText: "当前没有可见模型" }}
          renderItem={(model) => {
            const visible = installedModels?.visible_models.find((item) => item.id === model.id);
            return (
              <List.Item>
                <List.Item.Meta
                  title={model.id}
                  description={
                    visible
                      ? `${visible.family} / ${visible.format} / ${visible.quantization_level}`
                      : model.owned_by
                  }
                />
                {visible?.verified ? <Tag color="green">verified</Tag> : <Tag>local</Tag>}
              </List.Item>
            );
          }}
        />
      )}

      {section === "downloads" && (
        <List
          header={`Catalog ${catalog?.packages.length ?? 0}`}
          dataSource={(catalog?.packages ?? []).slice(0, 12)}
          locale={{ emptyText: "Catalog 暂无数据" }}
          renderItem={(item) => (
            <List.Item>
              <List.Item.Meta
                title={item.display_name}
                description={`${item.task} / ${item.runtime} / ${item.install_status}`}
              />
              {item.recommended && <Tag color="blue">recommended</Tag>}
            </List.Item>
          )}
        />
      )}

      {section === "runtime" && (
        <Space direction="vertical" size={12} className="drawer-stack">
          <Alert
            type={runtimeStatus?.status === "ok" ? "success" : "warning"}
            showIcon
            message={runtimeStatus?.runtime.message ?? "Runtime 状态尚未加载"}
          />
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
      )}

      {section === "api" && (
        <Descriptions column={1} size="small" bordered>
          <Descriptions.Item label="URL">{runtimeStatus?.port.url ?? "-"}</Descriptions.Item>
          <Descriptions.Item label="Port status">
            {runtimeStatus?.port.status ?? "-"}
          </Descriptions.Item>
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
          <Descriptions.Item label="Logs">{runtimeStatus?.paths.logs_directory ?? "-"}</Descriptions.Item>
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

function createTitle(content: string) {
  return content.length > 20 ? `${content.slice(0, 20)}...` : content;
}

function tagColor(status: string) {
  if (status === "ok" || status === "ready" || status === "installed") {
    return "green";
  }

  if (status === "error" || status === "blocked") {
    return "red";
  }

  if (status === "warning" || status === "partial" || status === "not_configured") {
    return "gold";
  }

  return "default";
}

export default App;
