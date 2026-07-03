import { Alert, Button, Collapse, Descriptions, List, Space, Tag } from "antd";
import type {
  AgentRuntimeStatus,
  AgentToolMapResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  OpenAiModel,
  RuntimeStatusResponse
} from "../../types";
import { tagColor } from "../../app/format";
import { isChatModel } from "../../app/models";
import type { CopyTextHandler } from "../../app/viewTypes";

export function CapabilitiesPanel({
  runtimeStatus,
  models,
  catalog,
  multimodalStatus,
  agentRuntime,
  agentTools,
  onCopyText
}: {
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  agentTools?: AgentToolMapResponse;
  onCopyText: CopyTextHandler;
}) {
  const rows = buildCapabilityRows({
    runtimeStatus,
    models,
    catalog,
    multimodalStatus,
    agentRuntime,
    agentTools
  });
  const groups = Array.from(new Set(rows.map((row) => row.group)));

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Alert
        type="info"
        showIcon
        message="Tomur 本地能力地图"
        description="这里聚合公开后端能力、当前可用状态和 Web 入口成熟度；产生副作用的动作仍需要显式确认。"
      />

      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Visible models">{models.length}</Descriptions.Item>
        <Descriptions.Item label="Catalog packages">{catalog?.packages.length ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Multimodal">{multimodalStatus?.status ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Agent tools">{agentTools?.tools.length ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Native bundle">{runtimeStatus?.native_bundle.status ?? "-"}</Descriptions.Item>
      </Descriptions>

      <Collapse
        size="small"
        defaultActiveKey={groups.slice(0, 2)}
        items={groups.map((group) => ({
          key: group,
          label: group,
          children: (
            <List
              size="small"
              dataSource={rows.filter((row) => row.group === group)}
              renderItem={(row) => (
                <List.Item
                  actions={[
                    <Button
                      key={`${row.route}-copy`}
                      type="text"
                      size="small"
                      onClick={() => void onCopyText(row.route, `已复制 ${row.title} 路由`)}
                    >
                      路由
                    </Button>,
                    row.sample ? (
                      <Button
                        key={`${row.route}-sample`}
                        type="text"
                        size="small"
                        onClick={() => void onCopyText(row.sample ?? "", `已复制 ${row.title} 示例`)}
                      >
                        示例
                      </Button>
                    ) : null
                  ]}
                >
                  <List.Item.Meta
                    title={
                      <Space wrap>
                        <Tag color={tagColor(row.status)}>{row.status}</Tag>
                        <span>{row.title}</span>
                        <Tag>{row.ui}</Tag>
                      </Space>
                    }
                    description={`${row.route} / ${row.message}`}
                  />
                </List.Item>
              )}
            />
          )
        }))}
      />
    </Space>
  );
}

interface CapabilityRow {
  group: string;
  title: string;
  route: string;
  status: string;
  ui: string;
  message: string;
  sample?: string;
}

function buildCapabilityRows({
  runtimeStatus,
  models,
  catalog,
  multimodalStatus,
  agentRuntime,
  agentTools
}: {
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  agentTools?: AgentToolMapResponse;
}): CapabilityRow[] {
  const chatModels = models.filter(isChatModel);
  const embeddingModels = models.filter((model) =>
    model.capabilities?.some((capability) => capability === "embeddings" || capability === "embedding")
  );
  const generationReady = chatModels.length > 0 ? "ok" : "not_configured";
  const nativeStatus = runtimeStatus?.native_bundle.status ?? "checking";
  const conversationStatus = runtimeStatus?.database.status === "ok" ? "ok" : runtimeStatus?.database.status ?? "checking";
  const multimodal = (keywords: string[]) => findMultimodalBackend(multimodalStatus, keywords);
  const agentTool = (name: string) => agentTools?.tools.find((tool) => tool.name === name);

  return [
    {
      group: "Core",
      title: "Health",
      route: "GET /health",
      status: runtimeStatus ? "ok" : "checking",
      ui: "status",
      message: "本地服务健康检查。"
    },
    {
      group: "Core",
      title: "Version",
      route: "GET /api/version",
      status: runtimeStatus ? "ok" : "checking",
      ui: "status",
      message: "工作台启动时读取版本。"
    },
    {
      group: "Runtime",
      title: "Runtime diagnostics",
      route: "GET /api/runtime/status",
      status: runtimeStatus?.status ?? "checking",
      ui: "drawer",
      message: runtimeStatus?.runtime.message ?? "读取本地 runtime、路径、数据库、proxy、端口和硬件状态。"
    },
    {
      group: "Runtime",
      title: "Native bundle prepare",
      route: "POST /api/runtime/native/prepare",
      status: nativeStatus,
      ui: "action",
      message: runtimeStatus?.native_bundle.message ?? "可从 Runtime 设置中执行 prepare。"
    },
    {
      group: "Runtime",
      title: "Native library resolve/load",
      route: "GET/POST /api/runtime/native/{componentId}/{libraryName}",
      status: nativeStatus,
      ui: "api",
      message: "当前 UI 展示 component 状态；逐库 resolve/load 仍以 API 为主。"
    },
    {
      group: "Models",
      title: "Visible models",
      route: "GET /v1/models",
      status: models.length > 0 ? "ok" : "not_configured",
      ui: "settings",
      message: `${models.length} 个本地可见模型。`
    },
    {
      group: "Models",
      title: "Catalog",
      route: "GET /api/models/catalog",
      status: catalog ? "ok" : "checking",
      ui: "settings",
      message: `${catalog?.packages.length ?? 0} 个 catalog 包；下载动作仍通过 CLI。`
    },
    {
      group: "Conversations",
      title: "Conversation history",
      route: "GET/POST /api/conversations",
      status: conversationStatus,
      ui: "chat",
      message: "会话列表、详情、删除和文本历史同步已接入。"
    },
    {
      group: "Conversations",
      title: "Multimodal turn",
      route: "POST /api/conversations/{conversationId}/turns",
      status: generationReady,
      ui: "chat",
      message: "附件、朗读和会话诊断通过该入口聚合。",
      sample: 'POST /api/conversations/{conversationId}/turns {"content":"列出当前 runtime 状态。","model":"<local-model>"}'
    },
    {
      group: "Conversations",
      title: "Voice turn",
      route: "POST /api/conversations/{conversationId}/voice-turns",
      status: multimodal(["asr"])?.status ?? "checking",
      ui: "chat",
      message: "按钮录音会转为 PCM WAV 并走语音回合服务。"
    },
    {
      group: "Conversations",
      title: "Messages / artifacts / diagnostics",
      route: "POST /api/conversations/{conversationId}/messages, POST /api/conversations/{conversationId}/artifacts, POST /api/conversations/{conversationId}/diagnostics",
      status: conversationStatus,
      ui: "chat",
      message: "消息同步、产物登记和诊断记录由会话服务管理。"
    },
    {
      group: "Conversations",
      title: "Artifact content",
      route: "GET /api/conversations/{conversationId}/artifacts/{artifactId}/content",
      status: conversationStatus,
      ui: "artifact",
      message: "音频播放、图片预览和产物打开入口复用该内容 API。"
    },
    {
      group: "OpenAI",
      title: "Chat completions",
      route: "POST /v1/chat/completions",
      status: generationReady,
      ui: "chat",
      message: `${chatModels.length} 个 Chat 模型可用于文本 streaming。`,
      sample: 'POST /v1/chat/completions {"model":"<local-model>","messages":[{"role":"user","content":"你好"}],"stream":true}'
    },
    {
      group: "OpenAI",
      title: "Completions",
      route: "POST /v1/completions",
      status: generationReady,
      ui: "api",
      message: "后端已接入；Web 当前优先使用 Chat 与 Conversations。",
      sample: 'POST /v1/completions {"model":"<local-model>","prompt":"你好","stream":false}'
    },
    {
      group: "OpenAI",
      title: "Embeddings",
      route: "POST /v1/embeddings",
      status: embeddingModels.length > 0 ? "ok" : "not_configured",
      ui: "api",
      message: `${embeddingModels.length} 个 embedding 模型可见；文件检索 UI 后续补齐。`,
      sample: 'POST /v1/embeddings {"model":"<embedding-model>","input":"Tomur"}'
    },
    {
      group: "OpenAI",
      title: "Image generation",
      route: "POST /v1/images/generations",
      status: multimodal(["image", "stable-diffusion"])?.status ?? "not_configured",
      ui: "artifact",
      message: "后端已接入；Web 先通过会话产物展示，独立生成面板后续补齐。"
    },
    {
      group: "OpenAI",
      title: "Audio transcription",
      route: "POST /v1/audio/transcriptions",
      status: multimodal(["asr", "whisper"])?.status ?? "not_configured",
      ui: "chat",
      message: "语音回合已复用 ASR；独立转写面板后续补齐。"
    },
    {
      group: "OpenAI",
      title: "Audio speech",
      route: "POST /v1/audio/speech",
      status: multimodal(["tts", "audio-output"])?.status ?? "not_configured",
      ui: "chat",
      message: "回复朗读和语音回合 TTS 已接入。"
    },
    {
      group: "Claude Code",
      title: "Model discovery",
      route: "GET /v1/models?limit=1000",
      status: chatModels.length > 0 ? "ok" : "not_configured",
      ui: "api",
      message: `${chatModels.length} 个文本模型会暴露为 claude-tomur-* 发现别名。`
    },
    {
      group: "Claude Code",
      title: "Messages",
      route: "POST /v1/messages",
      status: generationReady,
      ui: "api",
      message: "Anthropic Messages 兼容入口会映射到 Tomur 本地文本 Chat runtime。",
      sample: 'POST /v1/messages {"model":"<claude-tomur-alias>","max_tokens":512,"messages":[{"role":"user","content":"你好"}],"stream":false}'
    },
    {
      group: "Claude Code",
      title: "Streaming messages",
      route: "POST /v1/messages {\"stream\":true}",
      status: generationReady,
      ui: "api",
      message: "支持 message_start、content_block_delta、message_delta 和 message_stop SSE 事件。",
      sample: 'POST /v1/messages {"model":"<claude-tomur-alias>","max_tokens":512,"messages":[{"role":"user","content":"你好"}],"stream":true}'
    },
    {
      group: "Claude Code",
      title: "Token count",
      route: "POST /v1/messages/count_tokens",
      status: chatModels.length > 0 ? "ok" : "not_configured",
      ui: "api",
      message: "本地估算输入 token 数，用于 Claude Code 兼容探测和上下文预算。",
      sample: 'POST /v1/messages/count_tokens {"model":"<claude-tomur-alias>","messages":[{"role":"user","content":"你好"}]}'
    },
    {
      group: "Multimodal",
      title: "Vision analyze",
      route: "POST /api/vision/analyze",
      status: multimodal(["vision", "vlm"])?.status ?? "not_configured",
      ui: "chat",
      message: "图片附件会进入会话编排；独立 Vision 面板后续补齐。"
    },
    {
      group: "Multimodal",
      title: "OCR analyze",
      route: "POST /api/ocr/analyze",
      status: multimodal(["ocr"])?.status ?? "not_configured",
      ui: "chat",
      message: "图片或文件相关诊断会回填到 Chat。"
    },
    {
      group: "Ollama",
      title: "Tags",
      route: "GET /api/tags",
      status: models.length > 0 ? "ok" : "not_configured",
      ui: "api",
      message: "Ollama 兼容模型列表。"
    },
    {
      group: "Ollama",
      title: "Generate / Chat / Show",
      route: "POST /api/generate, POST /api/chat, POST /api/show",
      status: generationReady,
      ui: "api",
      message: "后端兼容接口已接入；Web 当前默认走 OpenAI/Conversations。",
      sample: 'POST /api/chat {"model":"<local-model>","messages":[{"role":"user","content":"你好"}],"stream":false}'
    },
    {
      group: "Agents",
      title: "Agent runtime",
      route: "GET /api/agents/runtime",
      status: agentRuntime?.status ?? "checking",
      ui: "settings",
      message: agentRuntime?.orchestration.message ?? "Agent runtime 状态。"
    },
    {
      group: "Agents",
      title: "Agent chat",
      route: "POST /api/agents/chat",
      status: agentRuntime?.chat_client.status ?? agentRuntime?.status ?? "checking",
      ui: "planned",
      message: "后端已接入 Agent Framework chat；Web 受控 Agent 对话入口后续补齐。",
      sample: 'POST /api/agents/chat {"message":"检查 runtime 状态","tool_mode":"auto_read_only"}'
    },
    {
      group: "Agents",
      title: "Read-only workflow",
      route: "POST /api/agents/workflows/read-only",
      status: agentRuntime?.orchestration.status ?? agentRuntime?.status ?? "checking",
      ui: "planned",
      message: "后端已接入受控只读 workflow；Web 调用入口后续补齐。",
      sample: 'POST /api/agents/workflows/read-only {"message":"汇总本地诊断","tools":[{"tool":"runtime.diagnose"}],"respond":true}'
    },
    {
      group: "Agents",
      title: "Tool map",
      route: "GET /api/agents/tools",
      status: agentTools?.status ?? "checking",
      ui: "settings",
      message: `${agentTools?.tools.length ?? 0} 个工具；只读与确认式受控调用已在 Agents 分组展示。`
    },
    {
      group: "Agents",
      title: "Tool bindings",
      route: "GET /api/agents/tool-bindings",
      status: agentRuntime?.agent_framework.status ?? agentRuntime?.status ?? "checking",
      ui: "settings",
      message: "只读 AITool 与受控声明工具已在 Agents 分组展示。"
    },
    {
      group: "Agents",
      title: "Read-only diagnostics tools",
      route: "POST /api/agents/tools/invoke",
      status: agentTool("runtime.diagnose")?.status ?? agentRuntime?.status ?? "checking",
      ui: "action",
      message: "runtime.diagnose、tools.inspect 与 files.search 已提供只读 Web 调用。",
      sample: 'POST /api/agents/tools/invoke {"tool":"runtime.diagnose","mode":"read_only"}'
    },
    {
      group: "Agents",
      title: "Controlled side-effect tools",
      route: "POST /api/agents/tools/invoke",
      status: agentTool("runtime.repair")?.status ?? agentRuntime?.status ?? "checking",
      ui: "action",
      message: "image.generate、audio.speak 与 runtime.repair 等受控工具在 Web 中保持显式确认边界。",
      sample: 'POST /api/agents/tools/invoke {"tool":"runtime.repair","mode":"controlled","confirm":true,"arguments":{"action":"session.unload"}}'
    },
    {
      group: "Agents",
      title: "Events and telemetry",
      route: "GET /api/agents/events, GET /api/agents/telemetry",
      status: agentRuntime ? "ok" : "checking",
      ui: "settings",
      message: "事件和 telemetry 边界已聚合到 Agents 分组。"
    }
  ];
}

function findMultimodalBackend(
  multimodalStatus: MultimodalRuntimeStatus | undefined,
  keywords: string[]
) {
  const normalized = keywords.map((keyword) => keyword.toLowerCase());
  return multimodalStatus?.backends.find((backend) => {
    const value = [
      backend.id,
      backend.display_name,
      backend.capability,
      backend.native_component_id,
      backend.model_requirement
    ].join(" ").toLowerCase();
    return normalized.some((keyword) => value.includes(keyword));
  });
}
