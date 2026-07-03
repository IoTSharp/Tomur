import { XStream } from "@ant-design/x-sdk";
import type {
  AgentEventLogRecentResponse,
  AgentFrameworkToolBindingResponse,
  AgentRuntimeStatus,
  AgentTelemetryStatus,
  AgentToolInvokeRequest,
  AgentToolInvokeResponse,
  AgentToolMapResponse,
  ChatMessage,
  ConversationAppendMessageRequest,
  ConversationAppendMessageResponse,
  ConversationCreateRequest,
  ConversationCreateResponse,
  ConversationDeleteResponse,
  ConversationDetailResponse,
  ConversationListResponse,
  ConversationTurnRequest,
  ConversationTurnResponse,
  ConversationVoiceTurnResponse,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  NativeBundlePrepareResult,
  OpenAiChatCompletionResponse,
  OpenAiErrorResponse,
  OpenAiModelListResponse,
  RuntimeStatusResponse,
  VersionResponse
} from "./types";

const jsonHeaders = {
  "Content-Type": "application/json"
};

export async function getVersion(signal?: AbortSignal): Promise<VersionResponse> {
  return getJson<VersionResponse>("/api/version", signal);
}

export async function getRuntimeStatus(signal?: AbortSignal): Promise<RuntimeStatusResponse> {
  return getJson<RuntimeStatusResponse>("/api/runtime/status", signal);
}

export async function getModels(signal?: AbortSignal): Promise<OpenAiModelListResponse> {
  return getJson<OpenAiModelListResponse>("/v1/models", signal);
}

export async function getInstalledModels(signal?: AbortSignal): Promise<InstalledModelsResponse> {
  return getJson<InstalledModelsResponse>("/api/models/installed", signal);
}

export async function getModelCatalog(signal?: AbortSignal): Promise<ModelCatalogResponse> {
  return getJson<ModelCatalogResponse>("/api/models/catalog", signal);
}

export async function getMultimodalStatus(signal?: AbortSignal): Promise<MultimodalRuntimeStatus> {
  return getJson<MultimodalRuntimeStatus>("/api/runtime/multimodal", signal);
}

export async function getAgentRuntime(signal?: AbortSignal): Promise<AgentRuntimeStatus> {
  return getJson<AgentRuntimeStatus>("/api/agents/runtime", signal);
}

export async function getAgentTools(signal?: AbortSignal): Promise<AgentToolMapResponse> {
  return getJson<AgentToolMapResponse>("/api/agents/tools", signal);
}

export async function getAgentToolBindings(
  signal?: AbortSignal
): Promise<AgentFrameworkToolBindingResponse> {
  return getJson<AgentFrameworkToolBindingResponse>("/api/agents/tool-bindings", signal);
}

export async function getAgentEvents(signal?: AbortSignal): Promise<AgentEventLogRecentResponse> {
  return getJson<AgentEventLogRecentResponse>("/api/agents/events?limit=20", signal);
}

export async function getAgentTelemetry(signal?: AbortSignal): Promise<AgentTelemetryStatus> {
  return getJson<AgentTelemetryStatus>("/api/agents/telemetry", signal);
}

export async function invokeAgentTool(
  request: AgentToolInvokeRequest,
  signal?: AbortSignal
): Promise<AgentToolInvokeResponse> {
  const response = await fetch("/api/agents/tools/invoke", {
    method: "POST",
    headers: jsonHeaders,
    body: JSON.stringify(request),
    signal
  });

  if (!response.ok && response.status !== 409) {
    throw await createApiError(response);
  }

  return (await response.json()) as AgentToolInvokeResponse;
}

export async function prepareNativeRuntime(signal?: AbortSignal): Promise<NativeBundlePrepareResult> {
  const response = await fetch("/api/runtime/native/prepare", {
    method: "POST",
    headers: jsonHeaders,
    signal
  });

  if (!response.ok) {
    const errorResponse = response.clone();
    try {
      const data = (await response.json()) as NativeBundlePrepareResult;
      if (data.message) {
        return data;
      }
    } catch {
      // Fall through to the common API error parser.
    }

    throw await createApiError(errorResponse);
  }

  return (await response.json()) as NativeBundlePrepareResult;
}

export async function unloadRuntimeSession(signal?: AbortSignal): Promise<RuntimeStatusResponse> {
  return postJson<RuntimeStatusResponse>("/api/runtime/session/unload", undefined, signal);
}

export async function createConversation(
  request: ConversationCreateRequest,
  signal?: AbortSignal
): Promise<ConversationCreateResponse> {
  return postJson<ConversationCreateResponse>("/api/conversations", request, signal);
}

export async function getConversations(signal?: AbortSignal): Promise<ConversationListResponse> {
  return getJson<ConversationListResponse>("/api/conversations", signal);
}

export async function getConversationDetail(
  conversationId: string,
  signal?: AbortSignal
): Promise<ConversationDetailResponse> {
  return getJson<ConversationDetailResponse>(conversationUrl(conversationId), signal);
}

export async function deleteConversation(
  conversationId: string,
  signal?: AbortSignal
): Promise<ConversationDeleteResponse> {
  const response = await fetch(conversationUrl(conversationId), {
    method: "DELETE",
    signal
  });

  if (!response.ok) {
    throw await createApiError(response);
  }

  return (await response.json()) as ConversationDeleteResponse;
}

export async function sendConversationTurn(
  conversationId: string,
  request: ConversationTurnRequest,
  signal?: AbortSignal
): Promise<ConversationTurnResponse> {
  const response = await fetch(`${conversationUrl(conversationId)}/turns`, {
    method: "POST",
    headers: jsonHeaders,
    body: JSON.stringify(request),
    signal
  });

  return readConversationResponse<ConversationTurnResponse>(response);
}

export async function appendConversationMessage(
  conversationId: string,
  request: ConversationAppendMessageRequest,
  signal?: AbortSignal
): Promise<ConversationAppendMessageResponse> {
  return postJson<ConversationAppendMessageResponse>(
    `${conversationUrl(conversationId)}/messages`,
    request,
    signal
  );
}

export async function sendConversationVoiceTurn(
  conversationId: string,
  file: Blob,
  options: {
    fileName: string;
    model?: string;
    speak?: boolean;
    language?: string;
    voice?: string;
    responseFormat?: string;
    speed?: number;
  },
  signal?: AbortSignal
): Promise<ConversationVoiceTurnResponse> {
  const form = new FormData();
  form.append("file", file, options.fileName);
  form.append("audio_name", options.fileName);
  form.append("audio_media_type", file.type || "audio/wav");
  if (options.model) {
    form.append("model", options.model);
  }
  if (options.speak !== undefined) {
    form.append("speak", String(options.speak));
  }
  if (options.language) {
    form.append("language", options.language);
  }
  if (options.voice) {
    form.append("voice", options.voice);
  }
  if (options.responseFormat) {
    form.append("response_format", options.responseFormat);
  }
  if (options.speed !== undefined) {
    form.append("speed", String(options.speed));
  }

  const response = await fetch(`${conversationUrl(conversationId)}/voice-turns`, {
    method: "POST",
    body: form,
    signal
  });

  return readConversationResponse<ConversationVoiceTurnResponse>(response);
}

export function getConversationArtifactContentUrl(conversationId: string, artifactId: string): string {
  return `${conversationUrl(conversationId)}/artifacts/${encodeURIComponent(artifactId)}/content`;
}

export async function sendChatCompletion(
  model: string,
  messages: ChatMessage[],
  signal?: AbortSignal,
  onChunk?: (content: string) => void
): Promise<string> {
  const response = await fetch("/v1/chat/completions", {
    method: "POST",
    headers: jsonHeaders,
    body: JSON.stringify({
      model,
      stream: true,
      messages: messages
        .filter((message) =>
          message.role !== "tool" &&
          message.content.trim().length > 0 &&
          message.status !== "loading"
        )
        .map((message) => ({
          role: message.role,
          content: message.content.trim()
        }))
    }),
    signal
  });

  if (!response.ok) {
    throw await createApiError(response);
  }

  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("text/event-stream") && response.body) {
    return readOpenAiStream(response.body, onChunk);
  }

  const data = (await response.json()) as OpenAiChatCompletionResponse | OpenAiErrorResponse;
  if ("error" in data) {
    throw new Error(data.error.message);
  }

  const text = data.choices.at(0)?.message.content ?? "";
  onChunk?.(text);
  return text;
}

async function readOpenAiStream(
  body: ReadableStream<Uint8Array>,
  onChunk?: (content: string) => void
): Promise<string> {
  let fullText = "";

  for await (const event of XStream({ readableStream: body })) {
    if (!event.data || event.data === "[DONE]") {
      continue;
    }

    const parsed = JSON.parse(event.data as string) as
      | OpenAiErrorResponse
      | {
          choices?: Array<{
            delta?: {
              content?: string | null;
            };
            finish_reason?: string | null;
          }>;
        };

    if ("error" in parsed) {
      throw new Error(parsed.error.message);
    }

    const content = parsed.choices?.at(0)?.delta?.content;
    if (content) {
      fullText += content;
      onChunk?.(content);
    }
  }

  return fullText;
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) {
    throw await createApiError(response);
  }

  return (await response.json()) as T;
}

async function postJson<T>(url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    method: "POST",
    headers: jsonHeaders,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal
  });

  if (!response.ok) {
    throw await createApiError(response);
  }

  return (await response.json()) as T;
}

async function readConversationResponse<T>(response: Response): Promise<T> {
  if (response.ok) {
    return (await response.json()) as T;
  }

  const errorResponse = response.clone();
  try {
    const data = (await response.json()) as {
      conversation?: unknown;
      diagnostics?: unknown;
      transcript?: unknown;
      user_message?: unknown;
    };
    if (
      Array.isArray(data.diagnostics) &&
      (data.conversation || data.transcript !== undefined || data.user_message)
    ) {
      return data as T;
    }
  } catch {
    // Fall through to the common API error parser.
  }

  throw await createApiError(errorResponse);
}

function conversationUrl(conversationId: string): string {
  return `/api/conversations/${encodeURIComponent(conversationId)}`;
}

async function createApiError(response: Response): Promise<Error> {
  try {
    const data = (await response.json()) as Partial<OpenAiErrorResponse> & {
      code?: string;
      message?: string;
      diagnostics?: Array<{ message?: string }>;
    };
    if (data.error?.message) {
      return new Error(data.error.message);
    }

    if (data.message) {
      return new Error(data.message);
    }

    if (data.code) {
      return new Error(data.code);
    }

    const diagnosticMessage = data.diagnostics?.find((item) => item.message)?.message;
    if (diagnosticMessage) {
      return new Error(diagnosticMessage);
    }
  } catch {
    // Fall through to a status-based error when the body is not JSON.
  }

  return new Error(`Tomur API returned ${response.status}`);
}
