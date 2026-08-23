import type { BubbleItemType } from "@ant-design/x";
import type { ChatOptions } from "../../app/chatOptions";
import { promptItems, promptText } from "../../app/constants";
import { resolvePromptText } from "../../app/promptContext";
import type { SettingsSection } from "../../app/settings";
import type {
  AgentRuntimeStatus,
  AgentToolMapResponse,
  Conversation,
  ConversationAttachment,
  DiagnosticItem,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  OpenAiModel,
  RuntimeStatusResponse
} from "../../types";
import { ChatPane } from "./ChatPane";
import { ComposerBar } from "./ComposerBar";
import { InlineDiagnostics } from "../layout/InlineDiagnostics";
import { WorkspaceHeader } from "../layout/WorkspaceHeader";

type ModelOption = {
  value: string;
  label: string;
};

export function ChatWorkspace({
  activeConversation,
  bubbleItems,
  chatModels,
  selectedModelLabel,
  visibleChatModels,
  chatReady,
  runtimeOk,
  runtimeStatus,
  warningDiagnostics,
  catalog,
  multimodalStatus,
  agentRuntime,
  models,
  installedModels,
  agentTools,
  loadingStatus,
  input,
  pendingAttachments,
  sending,
  recording,
  uploadingAttachment,
  multimodalAction,
  speechEnabled,
  chatOptions,
  ttsModelOptions,
  inputPlaceholder,
  onSelectedModelChange,
  onRefreshStatus,
  onOpenStatus,
  onOpenSettings,
  onInputChange,
  onAddPendingAttachment,
  onRemovePendingAttachment,
  onGenerateImage,
  onTranscribeAudio,
  onStartVoiceRecording,
  onStopVoiceRecording,
  onToggleSpeech,
  onChatOptionsChange,
  onSubmitMessage,
  onStopGeneration,
  onRegenerate
}: {
  activeConversation: Conversation;
  bubbleItems: BubbleItemType[];
  chatModels: OpenAiModel[];
  selectedModelLabel?: string;
  visibleChatModels: ModelOption[];
  chatReady: boolean;
  runtimeOk: boolean;
  runtimeStatus?: RuntimeStatusResponse;
  warningDiagnostics: DiagnosticItem[];
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  models: OpenAiModel[];
  installedModels?: InstalledModelsResponse;
  agentTools?: AgentToolMapResponse;
  loadingStatus: boolean;
  input: string;
  pendingAttachments: ConversationAttachment[];
  sending: boolean;
  recording: boolean;
  uploadingAttachment: boolean;
  multimodalAction: "image" | "transcription" | null;
  speechEnabled: boolean;
  chatOptions: ChatOptions;
  ttsModelOptions: Array<{ value: string; label: string }>;
  inputPlaceholder: string;
  onSelectedModelChange: (value: string) => void;
  onRefreshStatus: () => void | Promise<void>;
  onOpenStatus: () => void;
  onOpenSettings: (section: SettingsSection) => void;
  onInputChange: (value: string) => void;
  onAddPendingAttachment: (file: File) => void | Promise<void>;
  onRemovePendingAttachment: (id: string) => void;
  onGenerateImage: () => void;
  onTranscribeAudio: (file: File) => void | Promise<void>;
  onStartVoiceRecording: () => void | Promise<void>;
  onStopVoiceRecording: () => void;
  onToggleSpeech: () => void;
  onChatOptionsChange: (value: ChatOptions) => void;
  onSubmitMessage: (value: string) => void | Promise<void>;
  onStopGeneration: () => void;
  onRegenerate: () => void | Promise<void>;
}) {
  return (
    <main className="workspace">
      <WorkspaceHeader
        selectedModel={chatModels.find((model) => model.id === selectedModelLabel)}
        selectedModelLabel={selectedModelLabel}
        visibleChatModels={visibleChatModels}
        chatModelCount={chatModels.length}
        loadingStatus={loadingStatus}
        runtimeOk={runtimeOk}
        onModelChange={onSelectedModelChange}
        onRefreshStatus={() => void onRefreshStatus()}
        onOpenStatus={onOpenStatus}
      />

      <InlineDiagnostics
        chatReady={chatReady}
        runtimeOk={runtimeOk}
        warningDiagnostics={warningDiagnostics}
        onOpenSettings={onOpenSettings}
        onOpenStatus={onOpenStatus}
      />

      <ChatPane
        hasMessages={activeConversation.messages.length > 0}
        bubbleItems={bubbleItems}
        promptItems={promptItems}
        onPromptSelect={(promptKey, label) => {
          const text = resolvePromptText(promptKey, {
            runtimeStatus,
            models,
            installedModels,
            catalog,
            multimodalStatus,
            agentRuntime,
            agentTools
          }) ?? promptText[promptKey] ?? String(label ?? "");
          onInputChange(text);
        }}
      />

      <ComposerBar
        input={input}
        sending={sending}
        recording={recording}
        uploadingAttachment={uploadingAttachment}
        multimodalAction={multimodalAction}
        speechEnabled={speechEnabled}
        chatOptions={chatOptions}
        ttsModelOptions={ttsModelOptions}
        chatReady={chatReady}
        inputPlaceholder={inputPlaceholder}
        selectedModelLabel={selectedModelLabel}
        pendingAttachments={pendingAttachments}
        activeMessageCount={activeConversation.messages.length}
        onInputChange={onInputChange}
        onSubmitMessage={(value) => void onSubmitMessage(value)}
        onAddPendingAttachment={(file) => void onAddPendingAttachment(file)}
        onRemovePendingAttachment={onRemovePendingAttachment}
        onGenerateImage={onGenerateImage}
        onTranscribeAudio={(file) => void onTranscribeAudio(file)}
        onStartVoiceRecording={() => void onStartVoiceRecording()}
        onStopVoiceRecording={onStopVoiceRecording}
        onToggleSpeech={onToggleSpeech}
        onChatOptionsChange={onChatOptionsChange}
        onStopGeneration={onStopGeneration}
        onRegenerate={() => void onRegenerate()}
      />
    </main>
  );
}
