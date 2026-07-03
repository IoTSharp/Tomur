import { Button, Select, Tooltip, Typography } from "antd";
import { PanelRightOpen, RefreshCcw, Settings } from "lucide-react";

export interface ModelOption {
  value: string;
  label: string;
}

export function WorkspaceHeader({
  selectedModelLabel,
  visibleChatModels,
  chatModelCount,
  loadingStatus,
  onModelChange,
  onRefreshStatus,
  onOpenStatus,
  onOpenSettings
}: {
  selectedModelLabel?: string;
  visibleChatModels: ModelOption[];
  chatModelCount: number;
  loadingStatus: boolean;
  onModelChange: (value: string) => void;
  onRefreshStatus: () => void;
  onOpenStatus: () => void;
  onOpenSettings: () => void;
}) {
  return (
    <header className="topbar">
      <div className="topbar-copy">
        <Typography.Title level={3}>Chat-first 本地工作台</Typography.Title>
        <Typography.Text type="secondary">
          默认入口直接进入对话区，模型、下载、Runtime 和文件能力通过状态抽屉与 Settings 收敛。
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
        />
        <Tooltip title="刷新状态">
          <Button
            icon={<RefreshCcw size={16} />}
            loading={loadingStatus}
            onClick={onRefreshStatus}
          />
        </Tooltip>
        <Tooltip title="状态抽屉">
          <Button icon={<PanelRightOpen size={16} />} onClick={onOpenStatus} />
        </Tooltip>
        <Tooltip title="Settings">
          <Button icon={<Settings size={16} />} onClick={onOpenSettings} />
        </Tooltip>
      </div>
    </header>
  );
}

