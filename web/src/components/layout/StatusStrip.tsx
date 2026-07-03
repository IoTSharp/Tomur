import type {
  AgentRuntimeStatus,
  MultimodalRuntimeStatus,
  RuntimeStatusResponse
} from "../../types";
import type { SettingsSection } from "../../app/viewTypes";
import { StatusPill } from "../status/StatusPill";

export function StatusStrip({
  runtimeStatus,
  runtimeOk,
  chatModelCount,
  installedDownloadCount,
  multimodalStatus,
  agentRuntime,
  onOpenStatus,
  onOpenSettings
}: {
  runtimeStatus?: RuntimeStatusResponse;
  runtimeOk: boolean;
  chatModelCount: number;
  installedDownloadCount: number;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  onOpenStatus: () => void;
  onOpenSettings: (section: SettingsSection) => void;
}) {
  return (
    <section className="status-strip">
      <StatusPill
        label="Runtime"
        value={runtimeStatus?.status ?? "checking"}
        tone={runtimeOk ? "success" : "warning"}
        onClick={onOpenStatus}
      />
      <StatusPill
        label="Models"
        value={String(chatModelCount)}
        tone={chatModelCount > 0 ? "success" : "warning"}
        onClick={() => onOpenSettings("models")}
      />
      <StatusPill
        label="Downloads"
        value={String(installedDownloadCount)}
        tone="default"
        onClick={() => onOpenSettings("downloads")}
      />
      <StatusPill
        label="Multimodal"
        value={multimodalStatus?.status ?? "pending"}
        tone={multimodalStatus?.status === "ok" ? "success" : "warning"}
        onClick={onOpenStatus}
      />
      <StatusPill
        label="Agents"
        value={agentRuntime?.status ?? "pending"}
        tone={agentRuntime?.status === "ok" ? "success" : "warning"}
        onClick={() => onOpenSettings("agents")}
      />
    </section>
  );
}

