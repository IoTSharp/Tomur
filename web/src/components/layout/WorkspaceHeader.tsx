import { Button, Select, Tooltip, Typography } from "antd";
import { Activity, RefreshCcw } from "lucide-react";

export interface ModelOption {
  value: string;
  label: string;
}

export function WorkspaceHeader({
  selectedModelLabel,
  visibleChatModels,
  chatModelCount,
  loadingStatus,
  runtimeOk,
  onModelChange,
  onRefreshStatus,
  onOpenStatus
}: {
  selectedModelLabel?: string;
  visibleChatModels: ModelOption[];
  chatModelCount: number;
  loadingStatus: boolean;
  runtimeOk: boolean;
  onModelChange: (value: string) => void;
  onRefreshStatus: () => void;
  onOpenStatus: () => void;
}) {
  return (
    <header className="topbar">
      <div className="topbar-copy">
        <Typography.Title level={4}>本地对话</Typography.Title>
        <Typography.Text type="secondary">
          直接与本地模型对话，模型与运行时状态在左侧导航切换查看。
        </Typography.Text>
      </div>

      <div className="topbar-actions">
        <Select
          className="model-select"
          placeholder="选择本地模型"
          value={selectedModelLabel}
          options={visibleChatModels}
          onChange={onModelChange}
          disabled={chatModelCount === 0}
          showSearch
          optionFilterProp="label"
        />
        <Tooltip title="刷新状态">
          <Button
            icon={<RefreshCcw size={16} />}
            loading={loadingStatus}
            onClick={onRefreshStatus}
          />
        </Tooltip>
        <Tooltip title={runtimeOk ? "本地 runtime 就绪，查看状态" : "runtime 待处理，查看状态"}>
          <button
            type="button"
            className={`runtime-chip ${runtimeOk ? "is-ok" : "is-warn"}`}
            onClick={onOpenStatus}
          >
            <Activity size={14} />
            <span>{runtimeOk ? "就绪" : "待处理"}</span>
          </button>
        </Tooltip>
      </div>
    </header>
  );
}
