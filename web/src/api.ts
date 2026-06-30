import { XStream } from "@ant-design/x-sdk";
import type {
  ChatMessage,
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
        .filter((message) => message.content.trim().length > 0 && message.status !== "loading")
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

async function createApiError(response: Response): Promise<Error> {
  try {
    const data = (await response.json()) as Partial<OpenAiErrorResponse> & {
      message?: string;
      diagnostics?: Array<{ message?: string }>;
    };
    if (data.error?.message) {
      return new Error(data.error.message);
    }

    if (data.message) {
      return new Error(data.message);
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
