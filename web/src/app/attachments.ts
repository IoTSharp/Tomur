import type { ConversationAttachment } from "../types";

export async function createLocalAttachment(file: File): Promise<ConversationAttachment> {
  const mediaType = file.type || resolveMediaTypeFromFileName(file.name);
  if (mediaType.startsWith("image/") || mediaType.startsWith("audio/")) {
    return {
      id: crypto.randomUUID(),
      type: mediaType.startsWith("image/") ? "image" : "audio",
      name: file.name,
      media_type: mediaType,
      bytes: file.size,
      data_uri: await readFileAsDataUri(file)
    };
  }

  if (isTextAttachment(file, mediaType)) {
    return {
      id: crypto.randomUUID(),
      type: "file",
      name: file.name,
      media_type: mediaType || "text/plain",
      bytes: file.size,
      text: await file.text()
    };
  }

  throw new Error("当前前端只支持图片、音频和文本文件附件");
}

export function toAttachmentItem(attachment: ConversationAttachment) {
  const mediaType = attachment.media_type ?? "";
  const cardType: "image" | "audio" | "file" = mediaType.startsWith("image/")
    ? "image"
    : mediaType.startsWith("audio/")
      ? "audio"
      : "file";

  return {
    uid: attachment.id ?? attachment.name ?? crypto.randomUUID(),
    name: attachment.name ?? "attachment",
    status: "done" as const,
    size: attachment.bytes ?? undefined,
    byte: attachment.bytes ?? undefined,
    type: attachment.media_type ?? undefined,
    cardType,
    description: attachment.type ?? undefined
  };
}

function isTextAttachment(file: File, mediaType: string) {
  const extension = file.name.split(".").pop()?.toLowerCase();
  return (
    mediaType.startsWith("text/") ||
    ["md", "markdown", "json", "csv", "tsv", "txt", "log", "xml", "yaml", "yml"].includes(extension ?? "")
  );
}

function readFileAsDataUri(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("附件读取失败"));
    reader.onload = () => {
      if (typeof reader.result === "string") {
        resolve(reader.result);
        return;
      }

      reject(new Error("附件读取结果无效"));
    };
    reader.readAsDataURL(file);
  });
}

export function formatUserContent(content: string, attachments: ConversationAttachment[]) {
  if (attachments.length === 0) {
    return content;
  }

  const summary = summarizeAttachments(attachments);
  return content ? `${content}\n\n${summary}` : summary;
}

export function summarizeAttachments(attachments: ConversationAttachment[]) {
  if (attachments.length === 0) {
    return "";
  }

  return `附件：${attachments.map((item) => item.name ?? item.type ?? "attachment").join("、")}`;
}

export function buildComposerStatus(
  selectedModel: string | undefined,
  attachmentCount: number,
  speechEnabled: boolean,
  recording: boolean
) {
  if (recording) {
    return "正在录音，停止后会发送语音回合。";
  }

  const parts = [`使用 ${selectedModel} 会话`];
  if (attachmentCount > 0) {
    parts.push(`${attachmentCount} 个附件`);
  }
  if (speechEnabled) {
    parts.push("请求朗读");
  }

  return `${parts.join(" · ")}。`;
}

function resolveMediaTypeFromFileName(name: string) {
  const extension = name.split(".").pop()?.toLowerCase();
  switch (extension) {
    case "png":
      return "image/png";
    case "jpg":
    case "jpeg":
      return "image/jpeg";
    case "webp":
      return "image/webp";
    case "gif":
      return "image/gif";
    case "wav":
      return "audio/wav";
    case "mp3":
      return "audio/mpeg";
    case "ogg":
      return "audio/ogg";
    case "webm":
      return "audio/webm";
    case "json":
      return "application/json";
    case "csv":
      return "text/csv";
    case "md":
    case "markdown":
    case "txt":
    case "log":
      return "text/plain";
    default:
      return "application/octet-stream";
  }
}
