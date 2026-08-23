import type { OpenAiModel } from "../types";

export function isChatModel(model: OpenAiModel) {
  const capabilities = model.capabilities ?? [];
  if (capabilities.length === 0) {
    return model.format === "gguf" || model.format === "ggml" || model.family === "llama";
  }

  return capabilities.some(
    (capability) => capability === "chat" || capability === "completion"
  );
}

export function isSpeechModel(model: OpenAiModel) {
  return (model.capabilities ?? []).some((capability) =>
    ["tts", "speech", "audio-output"].includes(capability.toLowerCase())
  );
}

export function isImageModel(model: OpenAiModel) {
  return (model.capabilities ?? []).some((capability) =>
    ["image", "image-generation"].includes(capability.toLowerCase())
  );
}

export function isTranscriptionModel(model: OpenAiModel) {
  return (model.capabilities ?? []).some((capability) =>
    ["audio", "asr", "transcription"].includes(capability.toLowerCase())
  );
}
