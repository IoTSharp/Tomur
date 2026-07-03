import {
  Alert,
  Button,
  Card,
  Checkbox,
  Collapse,
  Descriptions,
  Input,
  List,
  Select,
  Space,
  Tag,
  Typography
} from "antd";
import { Copy, Layers3, Wrench } from "lucide-react";
import type {
  AgentEventLogRecentResponse,
  AgentFrameworkToolBindingResponse,
  AgentRuntimeStatus,
  AgentTelemetryStatus,
  AgentToolInvokeResponse,
  AgentToolMapResponse
} from "../../types";
import {
  buildControlledToolInvokeSample,
  formatJsonPreview,
  isSideEffectAgentTool
} from "../../app/agentTools";
import { formatRelativeTime, tagColor } from "../../app/format";
import type { CopyTextHandler } from "../../app/viewTypes";

export function AgentSettingsPanel({
  agentRuntime,
  agentTools,
  agentToolBindings,
  agentEvents,
  agentTelemetry,
  agentToolInvokeAction,
  agentToolInvokeResult,
  fileSearchQuery,
  controlledToolName,
  controlledToolArguments,
  controlledToolConfirmed,
  onCopyText,
  onRunReadOnlyAgentTool,
  onFileSearchQueryChange,
  onRunFileSearch,
  onControlledToolChange,
  onControlledToolArgumentsChange,
  onControlledToolConfirmedChange,
  onRunControlledAgentTool
}: {
  agentRuntime?: AgentRuntimeStatus;
  agentTools?: AgentToolMapResponse;
  agentToolBindings?: AgentFrameworkToolBindingResponse;
  agentEvents?: AgentEventLogRecentResponse;
  agentTelemetry?: AgentTelemetryStatus;
  agentToolInvokeAction: string | null;
  agentToolInvokeResult?: AgentToolInvokeResponse;
  fileSearchQuery: string;
  controlledToolName: string;
  controlledToolArguments: string;
  controlledToolConfirmed: boolean;
  onCopyText: CopyTextHandler;
  onRunReadOnlyAgentTool: (tool: string, argumentsPayload?: Record<string, unknown>) => Promise<void>;
  onFileSearchQueryChange: (value: string) => void;
  onRunFileSearch: () => Promise<void>;
  onControlledToolChange: (tool: string) => void;
  onControlledToolArgumentsChange: (value: string) => void;
  onControlledToolConfirmedChange: (value: boolean) => void;
  onRunControlledAgentTool: () => Promise<void>;
}) {
  const tools = agentTools?.tools ?? [];
  const controlledTools = tools.filter(isSideEffectAgentTool);
  const selectedControlledTool = controlledTools.find((tool) => tool.name === controlledToolName);
  const safeBindings = agentToolBindings?.safe_tools ?? [];
  const declarationBindings = agentToolBindings?.declaration_tools ?? [];

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Alert
        type={agentRuntime?.status === "ok" ? "success" : "warning"}
        showIcon
        message={agentRuntime?.orchestration.message ?? "Agent runtime 状态尚未加载"}
        description={agentRuntime?.agent_framework.message ?? "刷新状态后可查看 Agent Framework、工具地图和 telemetry 边界。"}
      />

      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Runtime">{agentRuntime?.agent_framework.runtime ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Chat client">{agentRuntime?.chat_client.provider ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Default model">
          {agentRuntime?.chat_client.default_model ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="Agent endpoint">
          {agentRuntime?.orchestration.endpoint ?? "POST /api/agents/chat"}
        </Descriptions.Item>
        <Descriptions.Item label="Tool map">{tools.length}</Descriptions.Item>
        <Descriptions.Item label="Telemetry">{agentTelemetry?.status ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Event log">{agentTelemetry?.local_event_log ?? agentEvents?.path ?? "-"}</Descriptions.Item>
      </Descriptions>

      <Card
        size="small"
        title="只读工具调用"
        extra={<Tag color="green">read-only</Tag>}
      >
        <Space direction="vertical" size={12} className="drawer-stack">
          <Typography.Text type="secondary">
            这些工具只读取本地诊断和工具地图，不生成产物、不修改 runtime。
          </Typography.Text>
          <Space wrap>
            <Button
              icon={<Wrench size={14} />}
              loading={agentToolInvokeAction === "runtime.diagnose"}
              disabled={Boolean(agentToolInvokeAction)}
              onClick={() => void onRunReadOnlyAgentTool("runtime.diagnose")}
            >
              runtime.diagnose
            </Button>
            <Button
              icon={<Layers3 size={14} />}
              loading={agentToolInvokeAction === "tools.inspect"}
              disabled={Boolean(agentToolInvokeAction)}
              onClick={() => void onRunReadOnlyAgentTool("tools.inspect")}
            >
              tools.inspect
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() =>
                void onCopyText(
                  'POST /api/agents/tools/invoke {"tool":"runtime.diagnose","mode":"read_only"}',
                  "已复制只读工具调用示例"
                )
              }
            >
              复制示例
            </Button>
          </Space>
          <Input.Search
            value={fileSearchQuery}
            allowClear
            enterButton="检索文件"
            placeholder="检索 Tomur 管理的本地文本文件"
            loading={agentToolInvokeAction === "files.search"}
            disabled={Boolean(agentToolInvokeAction && agentToolInvokeAction !== "files.search")}
            onChange={(event) => onFileSearchQueryChange(event.target.value)}
            onSearch={() => void onRunFileSearch()}
          />
        </Space>
      </Card>

      <Card
        size="small"
        title="副作用工具确认"
        extra={<Tag color="gold">controlled</Tag>}
      >
        <Space direction="vertical" size={12} className="drawer-stack">
          <Typography.Text type="secondary">
            这里仅暴露会写入本地产物或修改 runtime 状态的工具；调用会发送 mode=controlled，需确认的工具会附带 confirm=true。
          </Typography.Text>
          {controlledTools.length === 0 ? (
            <Alert
              type="info"
              showIcon
              message="当前没有需要显式确认的受控工具"
              description="刷新 Agent 工具地图后可查看 image.generate、audio.speak 或 runtime.repair 的可用状态。"
            />
          ) : (
            <>
              <Select
                style={{ width: "100%" }}
                value={selectedControlledTool?.name ?? controlledToolName}
                options={controlledTools.map((tool) => ({
                  value: tool.name,
                  label: `${tool.name} · ${tool.status}`
                }))}
                onChange={onControlledToolChange}
              />

              {selectedControlledTool && (
                <Descriptions column={1} size="small" bordered>
                  <Descriptions.Item label="Status">
                    <Space wrap>
                      <Tag color={tagColor(selectedControlledTool.status)}>
                        {selectedControlledTool.status}
                      </Tag>
                      {selectedControlledTool.callable ? <Tag color="green">callable</Tag> : <Tag>blocked</Tag>}
                      {selectedControlledTool.requires_confirmation ? <Tag color="gold">confirm</Tag> : null}
                    </Space>
                  </Descriptions.Item>
                  <Descriptions.Item label="Backend">{selectedControlledTool.backend}</Descriptions.Item>
                  <Descriptions.Item label="Side effect">{selectedControlledTool.side_effect}</Descriptions.Item>
                  <Descriptions.Item label="Model">{selectedControlledTool.model ?? "-"}</Descriptions.Item>
                  <Descriptions.Item label="Message">{selectedControlledTool.message}</Descriptions.Item>
                </Descriptions>
              )}

              <Input.TextArea
                className="controlled-tool-json"
                value={controlledToolArguments}
                autoSize={{ minRows: 5, maxRows: 10 }}
                spellCheck={false}
                onChange={(event) => onControlledToolArgumentsChange(event.target.value)}
              />

              {selectedControlledTool?.requires_confirmation && (
                <Checkbox
                  checked={controlledToolConfirmed}
                  onChange={(event) => onControlledToolConfirmedChange(event.target.checked)}
                >
                  我确认执行 {selectedControlledTool.name}，允许 Tomur 写入本地产物或修改本地 runtime 状态。
                </Checkbox>
              )}

              <Space wrap>
                <Button
                  type="primary"
                  danger={Boolean(selectedControlledTool?.requires_confirmation)}
                  icon={<Wrench size={14} />}
                  loading={Boolean(
                    selectedControlledTool &&
                      agentToolInvokeAction === selectedControlledTool.name
                  )}
                  disabled={
                    Boolean(agentToolInvokeAction) ||
                    Boolean(selectedControlledTool?.requires_confirmation && !controlledToolConfirmed)
                  }
                  onClick={() => void onRunControlledAgentTool()}
                >
                  确认并调用
                </Button>
                <Button
                  icon={<Copy size={14} />}
                  onClick={() =>
                    void onCopyText(
                      buildControlledToolInvokeSample(
                        selectedControlledTool?.name ?? controlledToolName,
                        controlledToolArguments,
                        Boolean(selectedControlledTool?.requires_confirmation && controlledToolConfirmed)
                      ),
                      "已复制受控工具调用示例"
                    )
                  }
                >
                  复制请求
                </Button>
              </Space>
            </>
          )}
        </Space>
      </Card>

      {agentToolInvokeResult && (
        <Alert
          type={
            agentToolInvokeResult.status === "ok"
              ? "success"
              : agentToolInvokeResult.status === "blocked"
                ? "warning"
                : "error"
          }
          showIcon
          message={`${agentToolInvokeResult.tool} / ${agentToolInvokeResult.status}`}
          description={
            <Space direction="vertical" size={6}>
              <Typography.Text type="secondary">
                {agentToolInvokeResult.implementation} / {agentToolInvokeResult.elapsed_ms} ms / {agentToolInvokeResult.audit.side_effect}
              </Typography.Text>
              {agentToolInvokeResult.diagnostics.length > 0 && (
                <Typography.Text type="secondary">
                  {agentToolInvokeResult.diagnostics.join(" ")}
                </Typography.Text>
              )}
              {agentToolInvokeResult.audit.actions.length > 0 && (
                <Typography.Text type="secondary">
                  下一步：{agentToolInvokeResult.audit.actions.join(" ")}
                </Typography.Text>
              )}
            </Space>
          }
        />
      )}
      {agentToolInvokeResult?.result !== undefined && (
        <Collapse
          size="small"
          items={[
            {
              key: "result",
              label: "结构化结果",
              children: (
                <pre className="json-preview">
                  {formatJsonPreview(agentToolInvokeResult.result)}
                </pre>
              )
            }
          ]}
        />
      )}

      <Card
        size="small"
        title={`工具目录 ${tools.length}`}
        extra={
          <Button
            type="text"
            size="small"
            icon={<Copy size={14} />}
            onClick={() => void onCopyText("GET /api/agents/tools", "已复制工具地图 API")}
          >
            API
          </Button>
        }
      >
        <List
          dataSource={tools}
          locale={{ emptyText: "暂无 Agent 工具" }}
          renderItem={(tool) => (
            <List.Item
              actions={
                tool.route ? [
                  <Button
                    key={`${tool.name}-route`}
                    type="text"
                    size="small"
                    onClick={() => void onCopyText(tool.route ?? "", `已复制 ${tool.name} 路由`)}
                  >
                    复制路由
                  </Button>
                ] : undefined
              }
            >
              <List.Item.Meta
                title={
                  <Space wrap>
                    <Tag color={tagColor(tool.status)}>{tool.status}</Tag>
                    <span>{tool.name}</span>
                    {tool.callable ? <Tag color="green">callable</Tag> : <Tag>blocked</Tag>}
                    {tool.requires_confirmation ? <Tag color="gold">confirm</Tag> : null}
                  </Space>
                }
                description={
                  <Space direction="vertical" size={4}>
                    <Typography.Text type="secondary">{tool.message}</Typography.Text>
                    <Typography.Text type="secondary">
                      {tool.backend} / {tool.side_effect} / {tool.invocation_modes.join(", ")}
                    </Typography.Text>
                  </Space>
                }
              />
            </List.Item>
          )}
        />
      </Card>

      <Card
        size="small"
        title="Tool bindings"
        extra={
          <Button
            type="text"
            size="small"
            icon={<Copy size={14} />}
            onClick={() =>
              void onCopyText("GET /api/agents/tool-bindings", "已复制 tool bindings API")
            }
          >
            API
          </Button>
        }
      >
        <Collapse
          size="small"
          items={[
            {
              key: "safe",
              label: `只读 AITool ${safeBindings.length}`,
              children: <AgentBindingList bindings={safeBindings} />
            },
            {
              key: "declaration",
              label: `受控声明工具 ${declarationBindings.length}`,
              children: <AgentBindingList bindings={declarationBindings} />
            }
          ]}
        />
      </Card>

      <Card
        size="small"
        title={`最近事件 ${agentEvents?.count ?? 0}`}
        extra={
          <Button
            type="text"
            size="small"
            icon={<Copy size={14} />}
            onClick={() => void onCopyText("GET /api/agents/events?limit=20", "已复制 Agent events API")}
          >
            API
          </Button>
        }
      >
        <List
          size="small"
          dataSource={agentEvents?.events ?? []}
          locale={{ emptyText: "暂无 Agent 事件" }}
          renderItem={(event) => (
            <List.Item>
              <List.Item.Meta
                title={
                  <Space wrap>
                    <Tag color={tagColor(event.status)}>{event.status}</Tag>
                    <span>{event.event}</span>
                    {event.tool ? <Tag>{event.tool}</Tag> : null}
                  </Space>
                }
                description={`${formatRelativeTime(event.recorded_at)} / ${event.mode ?? "default"} / ${event.elapsed_ms} ms`}
              />
            </List.Item>
          )}
        />
      </Card>

      <Card
        size="small"
        title="Telemetry"
        extra={<Tag color={tagColor(agentTelemetry?.exporter.status ?? "disabled")}>{agentTelemetry?.exporter.status ?? "disabled"}</Tag>}
      >
        <Space direction="vertical" size={10} className="drawer-stack">
          <Typography.Text type="secondary">
            {agentTelemetry?.exporter.message ?? "Telemetry 状态尚未加载。"}
          </Typography.Text>
          <List
            size="small"
            dataSource={agentTelemetry?.spans ?? []}
            locale={{ emptyText: "暂无 span 描述" }}
            renderItem={(span) => (
              <List.Item>
                <List.Item.Meta
                  title={span.name}
                  description={`${span.event} / ${span.description}`}
                />
              </List.Item>
            )}
          />
        </Space>
      </Card>
    </Space>
  );
}

function AgentBindingList({
  bindings
}: {
  bindings: AgentFrameworkToolBindingResponse["safe_tools"];
}) {
  return (
    <List
      size="small"
      dataSource={bindings}
      locale={{ emptyText: "暂无 bindings" }}
      renderItem={(binding) => (
        <List.Item>
          <List.Item.Meta
            title={
              <Space wrap>
                <Tag color={tagColor(binding.status)}>{binding.status}</Tag>
                <span>{binding.name}</span>
                {binding.requires_confirmation ? <Tag color="gold">confirm</Tag> : null}
              </Space>
            }
            description={`${binding.implementation} / ${binding.side_effect}`}
          />
        </List.Item>
      )}
    />
  );
}

