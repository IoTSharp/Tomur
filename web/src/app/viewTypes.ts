import type { ConversationDiagnosticRecord } from "../types";
export type { SettingsSection } from "./settings";

export type AppView = "chat" | "status" | "logs" | "settings";

export type CopyTextHandler = (text: string, successMessage?: string) => Promise<void>;

export type DiagnosticOpenHandler = (diagnostic: ConversationDiagnosticRecord) => void;
