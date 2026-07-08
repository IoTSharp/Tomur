import { useEffect, useMemo, useRef, useState } from "react";
import { App as AntApp, Badge, Button, Input, Segmented, Space, Tooltip, Typography } from "antd";
import { Copy, Pause, Play, Trash2 } from "lucide-react";
import { useLogStream } from "../../app/useLogStream";
import type { LogStreamEntry } from "../../types";

const LEVEL_OPTIONS = [
  { label: "全部", value: "all" },
  { label: "调试", value: "Debug" },
  { label: "信息", value: "Information" },
  { label: "警告", value: "Warning" },
  { label: "错误", value: "Error" }
];

function levelTone(level: string): string {
  switch (level) {
    case "Critical":
    case "Error":
      return "log-level-error";
    case "Warning":
      return "log-level-warning";
    case "Information":
      return "log-level-info";
    default:
      return "log-level-debug";
  }
}

function formatTime(timestamp: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) {
    return timestamp;
  }
  const pad = (value: number, size = 2) => String(value).padStart(size, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

function entriesToText(entries: LogStreamEntry[]): string {
  return entries
    .map((entry) => {
      const base = `${entry.timestamp} [${entry.level}] ${entry.category} — ${entry.message}`;
      return entry.exception ? `${base}\n${entry.exception}` : base;
    })
    .join("\n");
}

export function LogsView() {
  const { message } = AntApp.useApp();
  const [level, setLevel] = useState("all");
  const [categoryInput, setCategoryInput] = useState("");
  const [category, setCategory] = useState("");
  const [autoScroll, setAutoScroll] = useState(true);

  const { entries, connected, paused, pause, resume, clear } = useLogStream({
    level: level === "all" ? undefined : level,
    category: category || undefined
  });

  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (autoScroll && !paused && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [entries, autoScroll, paused]);

  const connectionBadge = useMemo(() => {
    if (paused) {
      return <Badge status="warning" text="已暂停" />;
    }
    return connected ? (
      <Badge status="processing" text="实时" />
    ) : (
      <Badge status="default" text="连接中" />
    );
  }, [connected, paused]);

  const copyAll = async () => {
    if (entries.length === 0) {
      message.warning("当前没有日志可复制");
      return;
    }
    try {
      await navigator.clipboard.writeText(entriesToText(entries));
      message.success(`已复制 ${entries.length} 条日志`);
    } catch {
      message.error("复制失败");
    }
  };

  return (
    <section className="page-view logs-view">
      <header className="page-header">
        <div>
          <Typography.Title level={3}>后台日志</Typography.Title>
          <Typography.Text type="secondary">
            实时观察本地服务日志 · {entries.length} 条 · {connectionBadge}
          </Typography.Text>
        </div>
      </header>

      <div className="logs-toolbar">
        <Segmented
          value={level}
          onChange={(value) => setLevel(String(value))}
          options={LEVEL_OPTIONS}
        />
        <Input
          className="logs-category"
          placeholder="按类别过滤，如 Tomur.Inference"
          value={categoryInput}
          allowClear
          onChange={(event) => setCategoryInput(event.target.value)}
          onPressEnter={() => setCategory(categoryInput.trim())}
          onBlur={() => setCategory(categoryInput.trim())}
        />
        <Space size={8} wrap>
          <Tooltip title={autoScroll ? "关闭自动滚动" : "开启自动滚动"}>
            <Button
              type={autoScroll ? "primary" : "default"}
              onClick={() => setAutoScroll((value) => !value)}
            >
              自动滚动
            </Button>
          </Tooltip>
          <Tooltip title={paused ? "继续" : "暂停"}>
            <Button
              icon={paused ? <Play size={16} /> : <Pause size={16} />}
              onClick={paused ? resume : pause}
            />
          </Tooltip>
          <Tooltip title="复制全部">
            <Button icon={<Copy size={16} />} onClick={() => void copyAll()} />
          </Tooltip>
          <Tooltip title="清空缓冲">
            <Button danger icon={<Trash2 size={16} />} onClick={clear} />
          </Tooltip>
        </Space>
      </div>

      <div className="logs-scroll" ref={scrollRef}>
        {entries.length === 0 ? (
          <div className="logs-empty">
            {connected ? "暂无日志，触发一次请求即可看到实时输出。" : "正在连接日志流…"}
          </div>
        ) : (
          entries.map((entry) => (
            <div className="log-row" key={entry.seq}>
              <span className="log-time">{formatTime(entry.timestamp)}</span>
              <span className={`log-level ${levelTone(entry.level)}`}>{entry.level}</span>
              <span className="log-category" title={entry.category}>
                {entry.category}
              </span>
              <span className="log-message">
                {entry.message}
                {entry.exception && <pre className="log-exception">{entry.exception}</pre>}
              </span>
            </div>
          ))
        )}
      </div>
    </section>
  );
}
