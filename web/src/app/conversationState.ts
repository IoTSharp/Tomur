import type { Conversation } from "../types";
import { initialConversationId as fallbackConversationId } from "./constants";
export { initialConversationId, initialConversations } from "./constants";

export function createFallbackConversation(): Conversation {
  return {
    id: fallbackConversationId,
    title: "本地 Chat",
    updatedAt: Date.now(),
    messages: []
  };
}
