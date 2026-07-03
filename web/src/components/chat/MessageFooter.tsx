import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { Actions } from "@ant-design/x";
import { Space, Tag, Tooltip, Typography } from "antd";
import {
  Copy,
  Download,
  ExternalLink,
  File as FileIcon,
  FileText,
  Image as ImageIcon,
  Music
} from "lucide-react";
import { getConversationArtifactContentUrl } from "../../api";
import type {
  ChatMessage,
  ConversationArtifactRecord,
  ConversationAttachment
} from "../../types";
import { formatBytes, tagColor } from "../../app/format";
import type { CopyTextHandler, DiagnosticOpenHandler } from "../../app/viewTypes";

export function MessageFooter({
  message,
  onOpenDiagnostic,
  onCopyMessage,
  onCopyText,
  showCopyAction
}: {
  message: ChatMessage;
  onOpenDiagnostic: DiagnosticOpenHandler;
  onCopyMessage: () => void;
  onCopyText: CopyTextHandler;
  showCopyAction: boolean;
}) {
  const diagnostics = message.diagnostics ?? [];
  const artifacts = message.artifacts ?? [];
  const attachments = message.attachments ?? [];
  const hasAudioArtifact = artifacts.some(isAudioArtifact);

  return (
    <div className="message-footer">
      {message.audioUrl && !hasAudioArtifact && (
        <audio className="message-audio" controls src={message.audioUrl} aria-label="助手语音回复" />
      )}
      {(attachments.length > 0 || artifacts.length > 0) && (
        <div className="asset-panel">
          {attachments.map((attachment, index) => (
            <AttachmentPreviewCard
              key={attachment.id ?? `${attachment.name ?? "attachment"}-${index}`}
              attachment={attachment}
              onCopyText={onCopyText}
            />
          ))}
          {artifacts.map((artifact) => (
            <ArtifactPreviewCard
              key={artifact.id}
              artifact={artifact}
              onCopyText={onCopyText}
            />
          ))}
        </div>
      )}
      {diagnostics.length > 0 && (
        <Space size={6} wrap className="message-meta">
          {diagnostics.map((diagnostic) => (
            <Tooltip key={diagnostic.id} title={diagnostic.message}>
              <Tag
                className="clickable-tag"
                color={tagColor(diagnostic.status)}
                onClick={() => onOpenDiagnostic(diagnostic)}
              >
                {diagnostic.code}
              </Tag>
            </Tooltip>
          ))}
        </Space>
      )}
      {showCopyAction && (
        <Actions
          items={[
            {
              key: "copy",
              icon: <Copy size={14} />,
              label: "复制",
              onItemClick: onCopyMessage
            }
          ]}
        />
      )}
    </div>
  );
}

export function hasMessageFooterContent(message: ChatMessage) {
  return Boolean(
    message.audioUrl ||
    message.attachments?.length ||
    message.artifacts?.length ||
    message.diagnostics?.length
  );
}

type AssetKind = "image" | "audio" | "text" | "file";

function AttachmentPreviewCard({
  attachment,
  onCopyText
}: {
  attachment: ConversationAttachment;
  onCopyText: CopyTextHandler;
}) {
  const href = resolveAttachmentHref(attachment);
  const name = getAttachmentName(attachment);
  const mediaType = attachment.media_type ?? "";
  const kind = resolveAssetKind(mediaType, attachment.type, name);
  const text = attachment.text ?? attachment.content;
  const details = formatAssetDetails(
    attachment.type ?? "attachment",
    mediaType,
    attachment.bytes,
    attachment.path ? "local path" : "inline"
  );

  return (
    <div className={`asset-card asset-card-${kind}`}>
      <AssetPreviewFrame kind={kind}>
        {renderAttachmentPreview(kind, href, text, name)}
      </AssetPreviewFrame>
      <div className="asset-body">
        <Space size={4} wrap>
          <Tag color="default">附件</Tag>
          <Tag color={kind === "file" ? "default" : "green"}>{kind}</Tag>
        </Space>
        <Typography.Text className="asset-title" title={attachment.path ?? name}>
          {name}
        </Typography.Text>
        <Typography.Text className="asset-subtitle" type="secondary" title={details}>
          {details}
        </Typography.Text>
      </div>
      <AssetActions
        openHref={href}
        downloadHref={href}
        downloadName={name}
        copyPath={attachment.path}
        onCopyText={onCopyText}
      />
    </div>
  );
}

function ArtifactPreviewCard({
  artifact,
  onCopyText
}: {
  artifact: ConversationArtifactRecord;
  onCopyText: CopyTextHandler;
}) {
  const url = getConversationArtifactContentUrl(artifact.conversation_id, artifact.id);
  const name = getArtifactName(artifact);
  const mediaType = artifact.media_type ?? "";
  const kind = resolveAssetKind(mediaType, artifact.type, name);
  const details = formatAssetDetails(artifact.type, mediaType, artifact.bytes, artifact.source);

  return (
    <div className={`asset-card asset-card-${kind}`}>
      <AssetPreviewFrame kind={kind}>
        {renderArtifactPreview(kind, artifact, url, name)}
      </AssetPreviewFrame>
      <div className="asset-body">
        <Space size={4} wrap>
          <Tag color={tagColor(artifact.status)}>{artifact.status}</Tag>
          <Tag color={kind === "file" ? "default" : "green"}>{artifact.type}</Tag>
        </Space>
        <Typography.Text className="asset-title" title={artifact.path ?? artifact.source ?? name}>
          {name}
        </Typography.Text>
        <Typography.Text className="asset-subtitle" type="secondary" title={details}>
          {details}
        </Typography.Text>
      </div>
      <AssetActions
        openHref={url}
        downloadHref={url}
        downloadName={name}
        copyPath={artifact.path ?? url}
        copyPathMessage={artifact.path ? "已复制产物路径" : "已复制产物链接"}
        onCopyText={onCopyText}
      />
    </div>
  );
}

function AssetPreviewFrame({
  kind,
  children
}: {
  kind: AssetKind;
  children: ReactNode;
}) {
  return (
    <div className={`asset-preview-frame asset-preview-${kind}`}>
      {children}
    </div>
  );
}

function AssetActions({
  openHref,
  downloadHref,
  downloadName,
  copyPath,
  copyPathMessage = "已复制路径",
  onCopyText
}: {
  openHref?: string;
  downloadHref?: string;
  downloadName: string;
  copyPath?: string | null;
  copyPathMessage?: string;
  onCopyText: CopyTextHandler;
}) {
  const copyLabel = copyPathMessage.includes("链接") ? "复制链接" : "复制路径";

  return (
    <div className="asset-actions">
      {openHref && (
        <Tooltip title="打开">
          <a
            className="asset-action-button"
            href={openHref}
            target="_blank"
            rel="noreferrer"
            aria-label="打开"
          >
            <ExternalLink size={14} />
          </a>
        </Tooltip>
      )}
      {copyPath && (
        <Tooltip title={copyLabel}>
          <button
            className="asset-action-button"
            type="button"
            aria-label={copyLabel}
            onClick={() => void onCopyText(copyPath, copyPathMessage)}
          >
            <Copy size={14} />
          </button>
        </Tooltip>
      )}
      {downloadHref && (
        <Tooltip title="下载">
          <a
            className="asset-action-button"
            href={downloadHref}
            download={downloadName}
            aria-label="下载"
          >
            <Download size={14} />
          </a>
        </Tooltip>
      )}
    </div>
  );
}

function renderAttachmentPreview(
  kind: AssetKind,
  href: string | undefined,
  text: string | null | undefined,
  name: string
) {
  if (kind === "image" && href) {
    return <img src={href} alt={name} loading="lazy" />;
  }

  if (kind === "audio" && href) {
    return <audio controls src={href} aria-label={name} />;
  }

  if (kind === "text") {
    return <TextPreviewBlock text={text} />;
  }

  return <AssetIcon kind={kind} />;
}

function renderArtifactPreview(
  kind: AssetKind,
  artifact: ConversationArtifactRecord,
  url: string,
  name: string
) {
  if (kind === "image") {
    return <img src={url} alt={name} loading="lazy" />;
  }

  if (kind === "audio") {
    return <audio controls src={url} aria-label={name} />;
  }

  if (kind === "text") {
    return <ArtifactTextPreview artifact={artifact} url={url} />;
  }

  return <AssetIcon kind={kind} />;
}

function ArtifactTextPreview({
  artifact,
  url
}: {
  artifact: ConversationArtifactRecord;
  url: string;
}) {
  const tooLargeForPreview = (artifact.bytes ?? 0) > 256 * 1024;
  const [preview, setPreview] = useState<{
    status: "loading" | "ready" | "error";
    text?: string;
  }>({ status: tooLargeForPreview ? "error" : "loading" });

  useEffect(() => {
    if (tooLargeForPreview) {
      setPreview({ status: "error" });
      return;
    }

    const controller = new AbortController();
    setPreview({ status: "loading" });

    fetch(url, { signal: controller.signal })
      .then((response) => {
        if (!response.ok) {
          throw new Error(`Artifact preview returned ${response.status}`);
        }

        return response.text();
      })
      .then((text) => setPreview({ status: "ready", text }))
      .catch((error) => {
        if (!(error instanceof DOMException && error.name === "AbortError")) {
          setPreview({ status: "error" });
        }
      });

    return () => controller.abort();
  }, [tooLargeForPreview, url]);

  if (tooLargeForPreview) {
    return <div className="asset-text-placeholder">文本文件较大，请打开或下载查看。</div>;
  }

  if (preview.status === "loading") {
    return <div className="asset-text-placeholder">正在读取文本预览...</div>;
  }

  if (preview.status === "error") {
    return <div className="asset-text-placeholder">无法直接预览，请打开或复制路径查看。</div>;
  }

  return <TextPreviewBlock text={preview.text} />;
}

function TextPreviewBlock({ text }: { text?: string | null }) {
  if (!text?.trim()) {
    return <div className="asset-text-placeholder">没有可预览的文本内容。</div>;
  }

  return <pre className="asset-text-preview">{limitPreviewText(text)}</pre>;
}

function AssetIcon({ kind }: { kind: AssetKind }) {
  const icon =
    kind === "image"
      ? <ImageIcon size={22} />
      : kind === "audio"
        ? <Music size={22} />
        : kind === "text"
          ? <FileText size={22} />
          : <FileIcon size={22} />;

  return <div className="asset-icon-frame">{icon}</div>;
}

function isAudioArtifact(artifact: ConversationArtifactRecord) {
  return resolveAssetKind(
    artifact.media_type,
    artifact.type,
    artifact.path ?? artifact.source ?? artifact.id
  ) === "audio";
}

function resolveAssetKind(
  mediaType?: string | null,
  type?: string | null,
  name?: string | null
): AssetKind {
  const normalizedMediaType = (mediaType ?? "").toLowerCase();
  const normalizedType = (type ?? "").toLowerCase();

  if (normalizedMediaType.startsWith("image/") || normalizedType === "image") {
    return "image";
  }

  if (normalizedMediaType.startsWith("audio/") || normalizedType === "audio") {
    return "audio";
  }

  if (
    normalizedMediaType.startsWith("text/") ||
    normalizedMediaType.includes("json") ||
    normalizedMediaType.includes("xml") ||
    normalizedMediaType.includes("yaml") ||
    normalizedMediaType.includes("csv") ||
    normalizedType === "text" ||
    isTextLikeFileName(name)
  ) {
    return "text";
  }

  return "file";
}

function resolveAttachmentHref(attachment: ConversationAttachment) {
  if (attachment.data_uri) {
    return attachment.data_uri;
  }

  if (attachment.base64) {
    return `data:${attachment.media_type || "application/octet-stream"};base64,${attachment.base64}`;
  }

  const text = attachment.text ?? attachment.content;
  if (text !== undefined && text !== null) {
    return `data:${attachment.media_type || "text/plain"};charset=utf-8,${encodeURIComponent(text)}`;
  }

  return undefined;
}

function getAttachmentName(attachment: ConversationAttachment) {
  return attachment.name ?? getFileNameFromPath(attachment.path) ?? attachment.id ?? "attachment";
}

function getArtifactName(artifact: ConversationArtifactRecord) {
  return getFileNameFromPath(artifact.path) ?? artifact.source ?? artifact.id;
}

function getFileNameFromPath(path?: string | null) {
  if (!path) {
    return undefined;
  }

  return path.split(/[\\/]/).filter(Boolean).at(-1);
}

function isTextLikeFileName(name?: string | null) {
  const extension = name?.split(".").pop()?.toLowerCase();
  return [
    "csv",
    "json",
    "log",
    "md",
    "markdown",
    "txt",
    "tsv",
    "xml",
    "yaml",
    "yml"
  ].includes(extension ?? "");
}

function formatAssetDetails(
  type: string,
  mediaType?: string | null,
  bytes?: number | null,
  source?: string | null
) {
  return [
    mediaType || type,
    bytes ? formatBytes(bytes) : null,
    source
  ].filter(Boolean).join(" / ");
}

function limitPreviewText(text: string) {
  const limit = 1600;
  return text.length > limit ? `${text.slice(0, limit)}\n...` : text;
}

