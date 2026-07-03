import type { AgentToolMapResponse } from "../types";
import { formatJsonPreview } from "./format";

export function isSideEffectAgentTool(tool: AgentToolMapResponse["tools"][number]) {
  const sideEffect = tool.side_effect.trim().toLowerCase();
  return tool.requires_confirmation || (sideEffect !== "" && sideEffect !== "read" && sideEffect !== "none");
}

export function createDefaultControlledToolArguments(toolName: string) {
  const defaults: Record<string, unknown> =
    toolName === "image.generate"
      ? {
          prompt: "A precise product-style image of a local AI runtime workspace",
          size: "1024x1024",
          steps: 4
        }
      : toolName === "audio.speak"
        ? {
            input: "Tomur local speech synthesis test.",
            response_format: "wav",
            speed: 1
          }
        : toolName === "runtime.repair"
          ? {
              action: "session.unload",
              reason: "Confirmed from Tomur Web Settings."
            }
          : {};

  return formatJsonPreview(defaults);
}

export function parseJsonObject(value: string): Record<string, unknown> {
  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  const parsed = JSON.parse(trimmed) as unknown;
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("工具参数必须是 JSON object");
  }

  return parsed as Record<string, unknown>;
}

export function buildControlledToolInvokeSample(
  toolName: string,
  argumentsText: string,
  confirm: boolean
) {
  const payload: Record<string, unknown> = {
    tool: toolName,
    mode: "controlled"
  };
  if (confirm) {
    payload.confirm = true;
  }

  try {
    payload.arguments = parseJsonObject(argumentsText);
  } catch {
    payload.arguments = "<JSON object>";
  }

  return `POST /api/agents/tools/invoke ${formatJsonPreview(payload)}`;
}
