import {
  Alert,
  Button,
  Card,
  Collapse,
  Descriptions,
  Flex,
  List,
  Segmented,
  Space,
  Tag,
  Typography
} from "antd";
import { Copy, Download, HardDrive, Layers3 } from "lucide-react";
import type {
  AgentEventLogRecentResponse,
  AgentFrameworkToolBindingResponse,
  AgentRuntimeStatus,
  AgentTelemetryStatus,
  AgentToolInvokeResponse,
  AgentToolMapResponse,
  InstalledModelsResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  NativeBundlePrepareResult,
  OpenAiModel,
  RuntimeStatusResponse
} from "../../types";
import { formatBytes, formatRelativeTime, tagColor } from "../../app/format";
import type { CopyTextHandler, SettingsSection } from "../../app/viewTypes";
import type { ThemeMode } from "../../app/theme";
import { AgentSettingsPanel } from "./AgentSettingsPanel";
import {
  DownloadPackageDetails,
  PackageDetails,
  renderCatalogSummary
} from "./ModelPackageDetails";
import { RuntimeSettingsPanel } from "./RuntimeSettingsPanel";

const SECTION_OPTIONS = [
  { label: "常规", value: "general" },
  { label: "模型", value: "models" },
  { label: "运行时", value: "runtime" },
  { label: "接口", value: "api" },
  { label: "高级", value: "advanced" }
];

export function SettingsPanel({
  section,
  onSectionChange,
  themeMode,
  onToggleTheme,
  runtimeStatus,
  models,
  installedModels,
  catalog,
  multimodalStatus,
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
  runtimeAction,
  prepareResult,
  onCopyText,
  onPrepareNativeRuntime,
  onUnloadRuntimeSession,
  onRunReadOnlyAgentTool,
  onFileSearchQueryChange,
  onRunFileSearch,
  onControlledToolChange,
  onControlledToolArgumentsChange,
  onControlledToolConfirmedChange,
  onRunControlledAgentTool
}: {
  section: SettingsSection;
  onSectionChange: (section: SettingsSection) => void;
  themeMode: ThemeMode;
  onToggleTheme: () => void;
  runtimeStatus?: RuntimeStatusResponse;
  models: OpenAiModel[];
  installedModels?: InstalledModelsResponse;
  catalog?: ModelCatalogResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
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
  runtimeAction: "prepare" | "unload" | null;
  prepareResult?: NativeBundlePrepareResult;
  onCopyText: CopyTextHandler;
  onPrepareNativeRuntime: () => Promise<void>;
  onUnloadRuntimeSession: () => Promise<void>;
  onRunReadOnlyAgentTool: (tool: string, argumentsPayload?: Record<string, unknown>) => Promise<void>;
  onFileSearchQueryChange: (value: string) => void;
  onRunFileSearch: () => Promise<void>;
  onControlledToolChange: (tool: string) => void;
  onControlledToolArgumentsChange: (value: string) => void;
  onControlledToolConfirmedChange: (value: boolean) => void;
  onRunControlledAgentTool: () => Promise<void>;
}) {
  const visibleModels = installedModels?.visible_models ?? [];
  const installedPackages = installedModels?.packages ?? [];
  const recommendedPackages = (catalog?.packages ?? []).filter((item) => item.recommended);
  const downloadablePackages = (catalog?.packages ?? []).filter((item) => !item.installed);
  const selectedAccelerator = runtimeStatus?.acceleration.selected_accelerator;
  const currentDeviceName = selectedAccelerator
    ? `${selectedAccelerator.name}${selectedAccelerator.integrated ? " (integrated)" : ""}`
    : runtimeStatus?.acceleration.effective_backend?.toLowerCase() === "cpu"
      ? "CPU"
      : "-";

  return (
    <section className="page-view settings-view">
      <header className="page-header">
        <div>
          <Typography.Title level={3}>设置</Typography.Title>
          <Typography.Text type="secondary">本地运行时、模型、接口与高级能力配置。</Typography.Text>
        </div>
      </header>

      <Segmented
        value={section}
        onChange={(value) => onSectionChange(value as SettingsSection)}
        options={SECTION_OPTIONS}
      />

      <div className="settings-body">
        {section === "general" && (
          <Space direction="vertical" size={16} className="drawer-stack">
            <Card size="small" title="外观">
              <Flex justify="space-between" align="center" gap={12} wrap>
                <Typography.Text type="secondary">切换工作台的浅色 / 深色主题（本地保存）。</Typography.Text>
                <Segmented
                  value={themeMode}
                  onChange={(value) => {
                    if (value !== themeMode) {
                      onToggleTheme();
                    }
                  }}
                  options={[
                    { label: "浅色", value: "light" },
                    { label: "深色", value: "dark" }
                  ]}
                />
              </Flex>
            </Card>

            <Alert
              type={runtimeStatus?.configuration.status === "error" ? "warning" : "info"}
              showIcon
              message={runtimeStatus?.configuration.message ?? "配置状态尚未加载"}
              description={runtimeStatus?.configuration.path ?? "刷新状态后可查看当前配置文件。"}
            />
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label="Version">{runtimeStatus?.version ?? "-"}</Descriptions.Item>
              <Descriptions.Item label="Schema">
                {runtimeStatus?.configuration.configuration.schema_version ?? "-"}
              </Descriptions.Item>
              <Descriptions.Item label="Server URLs">
                {runtimeStatus?.configuration.configuration.server.urls ?? runtimeStatus?.port.url ?? "-"}
              </Descriptions.Item>
              <Descriptions.Item label="Default backend">
                {runtimeStatus?.configuration.configuration.runtime.default_backend ?? "-"}
              </Descriptions.Item>
              <Descriptions.Item label="Current backend">
                {runtimeStatus?.acceleration.effective_backend ?? "-"}
              </Descriptions.Item>
              <Descriptions.Item label="Current device">{currentDeviceName}</Descriptions.Item>
              <Descriptions.Item label="Data directory">
                {runtimeStatus?.paths.data_directory ?? "-"}
              </Descriptions.Item>
            </Descriptions>
            <Space wrap>
              <Button
                icon={<Copy size={14} />}
                disabled={!runtimeStatus?.paths.data_directory}
                onClick={() =>
                  void onCopyText(runtimeStatus?.paths.data_directory ?? "", "已复制数据目录")
                }
              >
                复制数据目录
              </Button>
              <Button
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur doctor", "已复制 doctor 命令")}
              >
                复制 doctor
              </Button>
            </Space>
          </Space>
        )}

        {section === "models" && (
          <Space direction="vertical" size={16} className="drawer-stack">
            <Card size="small" title="当前机器建议" extra={<HardDrive size={16} />}>
              <Space direction="vertical" size={8} className="drawer-stack">
                <Typography.Text>
                  硬件档位：<Tag color="blue">{catalog?.hardware.tier ?? "-"}</Tag>
                </Typography.Text>
                <Typography.Text type="secondary">
                  {(catalog?.hardware.recommendations ?? []).join(" ")}
                </Typography.Text>
              </Space>
            </Card>

            <Card
              size="small"
              title={`可见模型 ${visibleModels.length}`}
              extra={
                <Button
                  type="text"
                  size="small"
                  icon={<Copy size={14} />}
                  onClick={() => void onCopyText("tomur list", "已复制查看模型命令")}
                >
                  CLI
                </Button>
              }
            >
              <List
                dataSource={visibleModels}
                locale={{ emptyText: "当前没有可见模型" }}
                renderItem={(model) => (
                  <List.Item>
                    <List.Item.Meta
                      title={
                        <Space wrap>
                          <span>{model.id}</span>
                          {model.verified ? <Tag color="green">verified</Tag> : <Tag>local</Tag>}
                        </Space>
                      }
                      description={`${model.family} / ${model.format} / ${model.quantization_level}`}
                    />
                  </List.Item>
                )}
              />
            </Card>

            <Card
              size="small"
              title={`已安装包 ${installedPackages.length}`}
            >
              <Collapse
                size="small"
                items={installedPackages.slice(0, 8).map((item) => ({
                  key: item.id,
                  label: (
                    <Space wrap>
                      <span>{item.display_name}</span>
                      <Tag color={tagColor(item.status)}>{item.status}</Tag>
                      <Typography.Text type="secondary">{formatRelativeTime(item.updated_at_utc)}</Typography.Text>
                    </Space>
                  ),
                  children: <PackageDetails packageItem={item} assets={item.assets} />
                }))}
              />
            </Card>

            <Card
              size="small"
              title={`推荐包 ${recommendedPackages.length}`}
              extra={
                <Button
                  type="primary"
                  size="small"
                  icon={<Download size={14} />}
                  onClick={() => void onCopyText("tomur pull recommended", "已复制推荐下载命令")}
                >
                  复制推荐命令
                </Button>
              }
            >
              <List
                dataSource={recommendedPackages}
                locale={{ emptyText: "暂无推荐包" }}
                renderItem={(item) => (
                  <List.Item
                    actions={[
                      <Button
                        key={`${item.id}-copy`}
                        type="text"
                        size="small"
                        onClick={() =>
                          void onCopyText(`tomur pull ${item.id}`, `已复制 ${item.id} 下载命令`)
                        }
                      >
                        复制命令
                      </Button>
                    ]}
                  >
                    <List.Item.Meta
                      title={
                        <Space wrap>
                          <span>{item.display_name}</span>
                          <Tag color="blue">recommended</Tag>
                          {item.installed ? <Tag color="green">installed</Tag> : null}
                        </Space>
                      }
                      description={renderCatalogSummary(item)}
                    />
                  </List.Item>
                )}
              />
            </Card>

            <Card
              size="small"
              title={`可下载包 ${downloadablePackages.length}`}
              extra={
                <Button
                  type="text"
                  size="small"
                  icon={<Layers3 size={14} />}
                  onClick={() => void onCopyText("tomur list --catalog", "已复制 catalog 查看命令")}
                >
                  查看完整 Catalog
                </Button>
              }
            >
              <Collapse
                size="small"
                items={downloadablePackages.slice(0, 12).map((item) => ({
                  key: item.id,
                  label: (
                    <Flex justify="space-between" align="center" gap={12} style={{ width: "100%" }}>
                      <Space wrap>
                        <span>{item.display_name}</span>
                        {item.optional ? <Tag>optional</Tag> : null}
                        {item.research ? <Tag color="purple">research</Tag> : null}
                      </Space>
                      <Tag color={tagColor(item.install_status)}>{item.install_status}</Tag>
                    </Flex>
                  ),
                  children: <DownloadPackageDetails item={item} onCopyText={onCopyText} />
                }))}
              />
            </Card>

            <Alert
              type="info"
              showIcon
              message="Web UI 当前不直接执行下载"
              description="下载、断点续传、校验和修复仍通过 CLI 完成。这里提供状态与可复制的 pull 命令。"
            />
          </Space>
        )}

        {section === "runtime" && (
          <RuntimeSettingsPanel
            runtimeStatus={runtimeStatus}
            prepareResult={prepareResult}
            runtimeAction={runtimeAction}
            onPrepareNativeRuntime={onPrepareNativeRuntime}
            onUnloadRuntimeSession={onUnloadRuntimeSession}
            onCopyText={onCopyText}
          />
        )}

        {section === "api" && (
          <Space direction="vertical" size={16} className="drawer-stack">
            <Alert
              type={runtimeStatus?.api_keys.status === "ok" ? "success" : "warning"}
              showIcon
              message={runtimeStatus?.api_keys.message ?? "API key 状态尚未加载"}
              description="本地浏览器工作台使用同源 API；对外暴露兼容接口前应创建本地 API key。"
            />
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label="URL">{runtimeStatus?.port.url ?? "-"}</Descriptions.Item>
              <Descriptions.Item label="Port status">{runtimeStatus?.port.status ?? "-"}</Descriptions.Item>
              <Descriptions.Item label="Active API keys">
                {runtimeStatus?.api_keys.active_key_count ?? 0}
              </Descriptions.Item>
              <Descriptions.Item label="OpenAI">GET /v1/models · POST /v1/chat/completions</Descriptions.Item>
              <Descriptions.Item label="Claude Code">POST /v1/messages</Descriptions.Item>
              <Descriptions.Item label="Ollama">POST /api/chat</Descriptions.Item>
            </Descriptions>
            <List
              size="small"
              header="本地 API key"
              dataSource={runtimeStatus?.api_keys.keys ?? []}
              locale={{ emptyText: "当前没有本地 API key 记录" }}
              renderItem={(key) => (
                <List.Item>
                  <List.Item.Meta
                    title={key.name}
                    description={`${key.prefix}... / ${formatRelativeTime(key.created_at)}`}
                  />
                </List.Item>
              )}
            />
            <Button
              icon={<Copy size={14} />}
              onClick={() => void onCopyText("tomur api-key create local", "已复制 API key 创建命令")}
            >
              复制创建命令
            </Button>
          </Space>
        )}

        {section === "advanced" && (
          <Space direction="vertical" size={16} className="drawer-stack">
            <Collapse
              size="small"
              defaultActiveKey={["agent"]}
              items={[
                {
                  key: "agent",
                  label: "Agent 工具与遥测",
                  children: (
                    <AgentSettingsPanel
                      agentRuntime={agentRuntime}
                      agentTools={agentTools}
                      agentToolBindings={agentToolBindings}
                      agentEvents={agentEvents}
                      agentTelemetry={agentTelemetry}
                      agentToolInvokeAction={agentToolInvokeAction}
                      agentToolInvokeResult={agentToolInvokeResult}
                      fileSearchQuery={fileSearchQuery}
                      controlledToolName={controlledToolName}
                      controlledToolArguments={controlledToolArguments}
                      controlledToolConfirmed={controlledToolConfirmed}
                      onCopyText={onCopyText}
                      onRunReadOnlyAgentTool={onRunReadOnlyAgentTool}
                      onFileSearchQueryChange={onFileSearchQueryChange}
                      onRunFileSearch={onRunFileSearch}
                      onControlledToolChange={onControlledToolChange}
                      onControlledToolArgumentsChange={onControlledToolArgumentsChange}
                      onControlledToolConfirmedChange={onControlledToolConfirmedChange}
                      onRunControlledAgentTool={onRunControlledAgentTool}
                    />
                  )
                },
                {
                  key: "files",
                  label: "文件与目录",
                  children: (
                    <Space direction="vertical" size={12} className="drawer-stack">
                      <Descriptions column={1} size="small" bordered>
                        <Descriptions.Item label="Data">
                          {runtimeStatus?.paths.data_directory ?? "-"}
                        </Descriptions.Item>
                        <Descriptions.Item label="Models">
                          {runtimeStatus?.paths.models_directory ?? "-"}
                        </Descriptions.Item>
                        <Descriptions.Item label="Runtime">
                          {runtimeStatus?.paths.runtime_directory ?? "-"}
                        </Descriptions.Item>
                        <Descriptions.Item label="Logs">
                          {runtimeStatus?.paths.logs_directory ?? "-"}
                        </Descriptions.Item>
                        <Descriptions.Item label="Database">
                          {runtimeStatus?.paths.database_path ?? "-"} ({runtimeStatus?.database.status ?? "-"})
                        </Descriptions.Item>
                      </Descriptions>
                      <List
                        size="small"
                        header="目录状态"
                        dataSource={runtimeStatus?.directories ?? []}
                        locale={{ emptyText: "暂无目录状态" }}
                        renderItem={(directory) => (
                          <List.Item>
                            <List.Item.Meta
                              title={
                                <Space>
                                  <Tag color={tagColor(directory.status)}>{directory.status}</Tag>
                                  {directory.name}
                                </Space>
                              }
                              description={directory.path}
                            />
                          </List.Item>
                        )}
                      />
                    </Space>
                  )
                },
                {
                  key: "system",
                  label: "系统与代理",
                  children: (
                    <Descriptions column={1} size="small" bordered>
                      <Descriptions.Item label="Acceleration">
                        {runtimeStatus?.acceleration.effective_backend ?? "-"}
                      </Descriptions.Item>
                      <Descriptions.Item label="GPU layers">
                        {runtimeStatus?.acceleration.effective_gpu_layers ?? 0}
                      </Descriptions.Item>
                      <Descriptions.Item label="Device memory">
                        {formatBytes(selectedAccelerator?.memory_bytes)}
                      </Descriptions.Item>
                      <Descriptions.Item label="Proxy">
                        {runtimeStatus?.proxy.message ?? "-"}
                      </Descriptions.Item>
                    </Descriptions>
                  )
                }
              ]}
            />
          </Space>
        )}
      </div>
    </section>
  );
}
