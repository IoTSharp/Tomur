import type { ConversationDiagnosticRecord } from "../types";

export type SettingsSection =
  | "general"
  | "models"
  | "runtime"
  | "api"
  | "advanced";

export function resolveSettingsSectionFromDiagnostic(
  diagnostic: ConversationDiagnosticRecord
): SettingsSection {
  const value = [
    diagnostic.code,
    diagnostic.backend,
    diagnostic.message,
    ...diagnostic.actions
  ].join(" ").toLowerCase();

  if (value.includes("model") || value.includes("download") || value.includes("pull")) {
    return "models";
  }

  if (
    value.includes("native") ||
    value.includes("runtime") ||
    value.includes("llama") ||
    value.includes("whisper") ||
    value.includes("tts") ||
    value.includes("ocr") ||
    value.includes("vlm") ||
    value.includes("stable-diffusion")
  ) {
    return "runtime";
  }

  if (value.includes("api") || value.includes("port") || value.includes("key")) {
    return "api";
  }

  return "advanced";
}
