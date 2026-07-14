export function formatJsonPreview(value: unknown) {
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

export function formatBytes(value?: number | null) {
  if (!value || value <= 0) {
    return "-";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = value;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }

  return `${size >= 10 || unitIndex === 0 ? size.toFixed(0) : size.toFixed(1)} ${units[unitIndex]}`;
}

export function formatRelativeTime(value: string) {
  const time = Date.parse(value);
  if (Number.isNaN(time)) {
    return value;
  }

  const diff = Date.now() - time;
  const minute = 60_000;
  const hour = 60 * minute;
  const day = 24 * hour;

  if (diff < hour) {
    return `${Math.max(1, Math.floor(diff / minute))} 分钟前`;
  }

  if (diff < day) {
    return `${Math.floor(diff / hour)} 小时前`;
  }

  return `${Math.floor(diff / day)} 天前`;
}

export function createTitle(content: string) {
  return content.length > 20 ? `${content.slice(0, 20)}...` : content;
}


export function tagColor(status: string) {
  if (
    status === "ok" ||
    status === "ready" ||
    status === "installed" ||
    status === "available" ||
    status === "accelerated" ||
    status === "prepared" ||
    status === "unchanged"
  ) {
    return "green";
  }

  if (status === "error" || status === "blocked" || status === "invalid") {
    return "red";
  }

  if (
    status === "warning" ||
    status === "partial" ||
    status === "not_configured" ||
    status === "provider_unavailable" ||
    status === "memory_limited" ||
    status === "copied" ||
    status === "repaired" ||
    status === "aliased"
  ) {
    return "gold";
  }

  return "default";
}
