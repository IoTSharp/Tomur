import type { PromptsItemType } from "@ant-design/x";
import type { Conversation } from "../types";

export const initialConversationId = "local-chat";

export const initialConversations: Conversation[] = [
  {
    id: initialConversationId,
    title: "本地 Chat",
    updatedAt: Date.now(),
    messages: []
  }
];

export const promptItems: PromptsItemType[] = [
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

export const promptText: Record<string, string> = {
  runtime: "请根据当前 Tomur 本地 runtime 状态，说明哪些能力已经可用，哪些还需要准备。",
  models: "请列出 Tomur 当前可见的本地模型，并推荐一个适合用于 Chat 的模型。",
  setup: "请根据当前 Tomur 状态，给我一个可以开始使用本地 Chat 的最小准备步骤。"
};
