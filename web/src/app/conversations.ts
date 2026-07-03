import { appendConversationMessage, getConversationArtifactContentUrl } from "../api";
import type {
  ChatMessage,
  Conversation,
  ConversationArtifactRecord,
  ConversationDetailResponse,
  ConversationDiagnosticRecord,
  ConversationMessageRecord,
  ConversationRecord,
  ConversationTurnResponse,
  ConversationVoiceTurnResponse
} from "../types";
import { createTitle } from "./format";

export function buildTurnMessages(
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

export function mapConversationRecord(record: ConversationRecord): Conversation {
  return {
    id: record.id,
    backendId: record.id,
    title: record.title || "本地会话",
    updatedAt: parseApiTime(record.updated_at),
    messages: [],
    loaded: false,
    loading: false
  };
}

export function mapConversationDetail(response: ConversationDetailResponse): Conversation {
  const artifactMap = createArtifactMap(response.artifacts);
  const diagnostics = response.diagnostics;
  const latestAssistantId = [...response.messages]
    .reverse()
    .find((message) => message.role === "assistant")?.id;
  const latestSpeechArtifact = resolveLatestSpeechArtifact(response.artifacts);
  const messages = response.messages.map((message) => {
    const speechArtifact =
      resolveExplicitSpeechArtifact(message, response.artifacts) ??
      (message.id === latestAssistantId ? latestSpeechArtifact : null);
    return mapConversationMessage(
      message,
      diagnostics,
      artifactMap,
      speechArtifact,
      speechArtifact?.media_type
    );
  });

  return {
    id: response.conversation.id,
    backendId: response.conversation.id,
    title: response.conversation.title || "本地会话",
    updatedAt: parseApiTime(response.conversation.updated_at),
    messages,
    loaded: true,
    loading: false
  };
}

function resolveExplicitSpeechArtifact(
  message: ConversationMessageRecord,
  artifacts: ConversationArtifactRecord[]
) {
  if (message.role !== "assistant") {
    return null;
  }

  const explicit = message.artifact_ids
    .map((id) => artifacts.find((artifact) => artifact.id === id))
    .find((artifact) => artifact?.media_type?.startsWith("audio/"));
  if (explicit) {
    return explicit;
  }

  return null;
}

function resolveLatestSpeechArtifact(artifacts: ConversationArtifactRecord[]) {
  return artifacts
    .filter((artifact) =>
      artifact.type === "audio" &&
      artifact.media_type?.startsWith("audio/") &&
      (artifact.source === "conversation.turn.tts" ||
        artifact.source === "conversation.voice-turn.tts")
    )
    .sort((left, right) => Date.parse(right.created_at) - Date.parse(left.created_at))
    .at(0) ?? null;
}

export function buildVoiceTurnMessages(
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

export function replaceTurnMessages(
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

export function findLastUserMessageIndex(messages: ChatMessage[]) {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    if (messages[index].role === "user") {
      return index;
    }
  }

  return -1;
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


export function resolveConversationTitle(
  conversation: Conversation,
  record: ConversationRecord,
  fallback?: string | null
) {
  if (conversation.title === "新会话" || conversation.title === "语音会话") {
    return createTitle(fallback || record.title || conversation.title);
  }

  return record.title || conversation.title;
}

export function parseApiTime(value?: string | null) {
  if (!value) {
    return Date.now();
  }

  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? Date.now() : parsed;
}

export async function syncPlainMessagesToBackend(
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

export async function appendPlainTurnToBackend(
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
