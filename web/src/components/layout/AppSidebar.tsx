import type { Conversation } from "../../types";
import { WorkbenchSidebar } from "./WorkbenchSidebar";

export function AppSidebar({
  version,
  runtimeOk,
  runtimeSeverity,
  runtimeMessage,
  loadingConversations,
  conversations,
  activeConversation,
  onStartConversation,
  onOpenStatus,
  onOpenConversation,
  onRemoveConversation
}: {
  version?: string;
  runtimeOk: boolean;
  runtimeSeverity: "success" | "warning";
  runtimeMessage?: string;
  loadingConversations: boolean;
  conversations: Conversation[];
  activeConversation: Conversation;
  onStartConversation: () => void;
  onOpenStatus: () => void;
  onOpenConversation: (conversationId: string) => void | Promise<void>;
  onRemoveConversation: (conversationId: string) => void | Promise<void>;
}) {
  const conversationItems = conversations.map((conversation) => ({
    key: conversation.id,
    label: conversation.title,
    group: "本地"
  }));

  return (
    <WorkbenchSidebar
      versionLabel={version ?? "local runtime"}
      runtimeOk={runtimeOk}
      runtimeSeverity={runtimeSeverity}
      loadingConversations={loadingConversations}
      runtimeMessage={runtimeMessage}
      conversationItems={conversationItems}
      activeConversationId={activeConversation.id}
      onStartConversation={onStartConversation}
      onOpenStatus={onOpenStatus}
      onOpenConversation={(conversationId) => void onOpenConversation(conversationId)}
      onRemoveConversation={(conversationId) => void onRemoveConversation(conversationId)}
    />
  );
}
