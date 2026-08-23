import { Button, Select, Space, Tag, Tooltip, Typography } from "antd";
import { Activity, RefreshCcw } from "lucide-react";
import type { OpenAiModel } from "../../types";

export interface ModelOption {
  value: string;
  label: string;
}

export function WorkspaceHeader({
  selectedModel,
  selectedModelLabel,
  visibleChatModels,
  chatModelCount,
  loadingStatus,
  runtimeOk,
  onModelChange,
  onRefreshStatus,
  onOpenStatus
}: {
  selectedModel?: OpenAiModel;
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
          {selectedModel
            ? [selectedModel.family, selectedModel.format, selectedModel.quantization]
                .filter(Boolean)
                .join(" / ")
            : "选择本地模型后开始对话。"}
        </Typography.Text>
        {selectedModel?.capabilities?.length ? (
          <Space size={[4, 4]} wrap className="model-capabilities">
            {selectedModel.capabilities.map((capability) => (
              <Tag key={capability}>{capability}</Tag>
            ))}
          </Space>
        ) : null}
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
