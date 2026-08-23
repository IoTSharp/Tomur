import {
  Button,
  Divider,
  Input,
  InputNumber,
  Popover,
  Select,
  Slider,
  Tag,
  Tooltip,
  Typography
} from "antd";
import { RotateCcw, SlidersHorizontal } from "lucide-react";
import type { ReactNode } from "react";
import {
  defaultChatOptions,
  normalizeChatOptions,
  type ChatOptions
} from "../../app/chatOptions";

interface TtsModelOption {
  value: string;
  label: string;
}

export function ChatOptionsPopover({
  value,
  disabled,
  speechEnabled,
  ttsModelOptions,
  onChange
}: {
  value: ChatOptions;
  disabled: boolean;
  speechEnabled: boolean;
  ttsModelOptions: TtsModelOption[];
  onChange: (value: ChatOptions) => void;
}) {
  const update = <K extends keyof ChatOptions>(key: K, nextValue: ChatOptions[K]) => {
    onChange(normalizeChatOptions({ ...value, [key]: nextValue }));
  };

  const content = (
    <div className="chat-options-panel">
      <div className="chat-options-header">
        <Typography.Text strong>生成参数</Typography.Text>
        <Tooltip title="恢复默认参数">
          <Button
            type="text"
            size="small"
            icon={<RotateCcw size={14} />}
            onClick={() => onChange(defaultChatOptions)}
          />
        </Tooltip>
      </div>

      <OptionRow label="最大输出">
        <Slider
          min={1}
          max={4096}
          step={1}
          value={value.maxTokens}
          onChange={(nextValue) => update("maxTokens", nextValue)}
        />
        <InputNumber
          min={1}
          max={4096}
          step={1}
          value={value.maxTokens}
          onChange={(nextValue) => update("maxTokens", nextValue ?? defaultChatOptions.maxTokens)}
        />
      </OptionRow>

      <OptionRow label="Temperature">
        <Slider
          min={0}
          max={2}
          step={0.1}
          value={value.temperature}
          onChange={(nextValue) => update("temperature", nextValue)}
        />
        <InputNumber
          min={0}
          max={2}
          step={0.1}
          value={value.temperature}
          onChange={(nextValue) => update("temperature", nextValue ?? defaultChatOptions.temperature)}
        />
      </OptionRow>

      <OptionRow label="Top P">
        <Slider
          min={0.01}
          max={1}
          step={0.01}
          value={value.topP}
          onChange={(nextValue) => update("topP", nextValue)}
        />
        <InputNumber
          min={0.01}
          max={1}
          step={0.01}
          value={value.topP}
          onChange={(nextValue) => update("topP", nextValue ?? defaultChatOptions.topP)}
        />
      </OptionRow>

      <OptionRow label="历史消息">
        <Slider
          min={1}
          max={1000}
          step={1}
          value={value.historyLimit}
          onChange={(nextValue) => update("historyLimit", nextValue)}
        />
        <InputNumber
          min={1}
          max={1000}
          step={1}
          value={value.historyLimit}
          onChange={(nextValue) => update("historyLimit", nextValue ?? defaultChatOptions.historyLimit)}
        />
      </OptionRow>

      <Divider />

      <div className="chat-options-header">
        <Typography.Text strong>语音输出</Typography.Text>
        <Tag color={speechEnabled ? "green" : "default"}>{speechEnabled ? "启用" : "关闭"}</Tag>
      </div>

      <label className="chat-option-field">
        <Typography.Text type="secondary">TTS 模型</Typography.Text>
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          placeholder="自动选择"
          value={value.ttsModel || undefined}
          options={ttsModelOptions}
          disabled={ttsModelOptions.length === 0}
          onChange={(nextValue) => update("ttsModel", nextValue ?? "")}
        />
      </label>

      <div className="chat-option-field-grid">
        <label className="chat-option-field">
          <Typography.Text type="secondary">Voice</Typography.Text>
          <Input
            value={value.voice}
            placeholder="默认"
            onChange={(event) => update("voice", event.target.value)}
          />
        </label>
        <label className="chat-option-field">
          <Typography.Text type="secondary">Language</Typography.Text>
          <Input
            value={value.language}
            placeholder="自动"
            onChange={(event) => update("language", event.target.value)}
          />
        </label>
      </div>

      <OptionRow label="语速">
        <Slider
          min={0.25}
          max={4}
          step={0.05}
          value={value.speed}
          onChange={(nextValue) => update("speed", nextValue)}
        />
        <InputNumber
          min={0.25}
          max={4}
          step={0.05}
          value={value.speed}
          onChange={(nextValue) => update("speed", nextValue ?? defaultChatOptions.speed)}
        />
      </OptionRow>
    </div>
  );

  return (
    <Popover content={content} trigger="click" placement="topLeft" destroyOnHidden>
      <Tooltip title="生成与语音参数">
        <Button
          icon={<SlidersHorizontal size={16} />}
          disabled={disabled}
          aria-label="生成与语音参数"
        />
      </Tooltip>
    </Popover>
  );
}

function OptionRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="chat-option-row">
      <Typography.Text type="secondary">{label}</Typography.Text>
      {children}
    </div>
  );
}
