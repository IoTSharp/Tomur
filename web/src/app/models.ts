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
