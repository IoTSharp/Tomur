import type {
  AgentRuntimeStatus,
  AgentToolMapResponse,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  OpenAiModel,
  RuntimeStatusResponse
} from "../types";
import { isChatModel } from "./models";

export function resolvePromptText(
  key: string,
  context: {
    runtimeStatus?: RuntimeStatusResponse;
    models: OpenAiModel[];
    installedModels?: InstalledModelsResponse;
    catalog?: ModelCatalogResponse;
    multimodalStatus?: MultimodalRuntimeStatus;
    agentRuntime?: AgentRuntimeStatus;
    agentTools?: AgentToolMapResponse;
  }
) {
  const snapshot = buildLocalContextSnapshot(context);
  switch (key) {
    case "runtime":
      return `请根据下面的 Tomur 本地状态摘要，说明哪些能力已经可用，哪些还需要准备。\n\n${snapshot}`;
    case "models":
      return `请根据下面的 Tomur 本地模型和 catalog 摘要，列出当前可见模型，并推荐一个适合用于 Chat 的模型。\n\n${snapshot}`;
    case "setup":
      return `请根据下面的 Tomur 本地状态摘要，给我一个可以开始使用本地 Chat 和多模态能力的最小准备步骤。\n\n${snapshot}`;
    default:
      return undefined;
  }
}

function buildLocalContextSnapshot({
  runtimeStatus,
  models,
  installedModels,
  catalog,
  multimodalStatus,
  agentRuntime,
  agentTools
}: {
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  installedModels?: InstalledModelsResponse;
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  agentTools?: AgentToolMapResponse;
}) {
  const visibleModels = installedModels?.visible_models ?? [];
  const diagnostics = runtimeStatus?.diagnostics
    .filter((item) => item.severity !== "info" || item.status !== "ok")
    .slice(0, 5)
    .map((item) => `${item.name}: ${item.status} - ${item.message}`) ?? [];
  const multimodal = multimodalStatus?.backends
    .map((backend) => `${backend.capability}: ${backend.status} - ${backend.message}`)
    .slice(0, 8) ?? [];
  const tools = agentTools?.tools
    .map((tool) => `${tool.name}: ${tool.status}${tool.requires_confirmation ? " / confirm" : ""} - ${tool.message}`)
    .slice(0, 10) ?? [];

  return [
    "Tomur 本地状态摘要：",
    `- Runtime: ${runtimeStatus?.status ?? "unknown"} / ${runtimeStatus?.runtime.message ?? "未加载"}`,
    `- Native bundle: ${runtimeStatus?.native_bundle.status ?? "unknown"} / ${runtimeStatus?.native_bundle.message ?? "未加载"}`,
    `- Chat models: ${models.filter(isChatModel).map((model) => model.id).join(", ") || "none"}`,
    `- Visible models: ${visibleModels.map((model) => model.id).join(", ") || "none"}`,
    `- Installed packages: ${installedModels?.packages.length ?? 0}`,
    `- Catalog packages: ${catalog?.packages.length ?? 0}`,
    `- Agent runtime: ${agentRuntime?.status ?? "unknown"} / ${agentRuntime?.orchestration.message ?? "未加载"}`,
    diagnostics.length ? `- Diagnostics:\n  - ${diagnostics.join("\n  - ")}` : "- Diagnostics: none",
    multimodal.length ? `- Multimodal:\n  - ${multimodal.join("\n  - ")}` : "- Multimodal: none",
    tools.length ? `- Agent tools:\n  - ${tools.join("\n  - ")}` : "- Agent tools: none"
  ].join("\n");
}
