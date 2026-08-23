export interface ChatGenerationOptions {
  maxTokens: number;
  temperature: number;
  topP: number;
  historyLimit: number;
}

export interface ChatOptions extends ChatGenerationOptions {
  ttsModel: string;
  voice: string;
  language: string;
  speed: number;
}

export const defaultChatOptions: ChatOptions = {
  maxTokens: 256,
  temperature: 0.7,
  topP: 0.9,
  historyLimit: 200,
  ttsModel: "",
  voice: "",
  language: "",
  speed: 1
};

const STORAGE_KEY = "tomur-chat-options";

export function readStoredChatOptions(): ChatOptions {
  if (typeof window === "undefined") {
    return defaultChatOptions;
  }

  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (!stored) {
      return defaultChatOptions;
    }

    const value = JSON.parse(stored) as Partial<ChatOptions>;
    return normalizeChatOptions(value);
  } catch {
    return defaultChatOptions;
  }
}

export function persistChatOptions(options: ChatOptions): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(normalizeChatOptions(options)));
  } catch {
    // Browser storage can be unavailable in restricted local profiles.
  }
}

export function normalizeChatOptions(value: Partial<ChatOptions>): ChatOptions {
  return {
    maxTokens: clampInteger(value.maxTokens, 1, 4096, defaultChatOptions.maxTokens),
    temperature: clampNumber(value.temperature, 0, 2, defaultChatOptions.temperature),
    topP: clampNumber(value.topP, 0.01, 1, defaultChatOptions.topP),
    historyLimit: clampInteger(value.historyLimit, 1, 1000, defaultChatOptions.historyLimit),
    ttsModel: normalizeText(value.ttsModel),
    voice: normalizeText(value.voice),
    language: normalizeText(value.language),
    speed: clampNumber(value.speed, 0.25, 4, defaultChatOptions.speed)
  };
}

function clampInteger(value: number | undefined, minimum: number, maximum: number, fallback: number) {
  const normalized = Number.isFinite(value) ? Math.trunc(value!) : fallback;
  return Math.min(maximum, Math.max(minimum, normalized));
}

function clampNumber(value: number | undefined, minimum: number, maximum: number, fallback: number) {
  const normalized = Number.isFinite(value) ? value! : fallback;
  return Math.min(maximum, Math.max(minimum, normalized));
}

function normalizeText(value: string | undefined) {
  return typeof value === "string" ? value.trim() : "";
}
