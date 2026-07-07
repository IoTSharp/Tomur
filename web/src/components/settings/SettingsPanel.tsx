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
import { AgentSettingsPanel } from "./AgentSettingsPanel";
import { CapabilitiesPanel } from "./CapabilitiesPanel";
import {
  DownloadPackageDetails,
  PackageDetails,
  renderCatalogSummary
} from "./ModelPackageDetails";
import { RuntimeSettingsPanel } from "./RuntimeSettingsPanel";

export function SettingsPanel({
  section,
  onSectionChange,
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
    <Space direction="vertical" size={16} className="drawer-stack">
      <Segmented
        block
        value={section}
        onChange={(value) => onSectionChange(value as SettingsSection)}
        options={[
          { label: "General", value: "general" },
          { label: "Models", value: "models" },
          { label: "Downloads", value: "downloads" },
          { label: "Runtime", value: "runtime" },
          { label: "Agent", value: "agents" },
          { label: "能力", value: "capabilities" },
          { label: "API", value: "api" },
          { label: "Files", value: "files" },
          { label: "Advanced", value: "advanced" }
        ]}
      />

      {section === "general" && (
        <Space direction="vertical" size={16} className="drawer-stack">
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
            <Descriptions.Item label="Current acceleration">
              <Space wrap>
                <Tag color={tagColor(runtimeStatus?.acceleration.status ?? "checking")}>
                  {runtimeStatus?.acceleration.status ?? "checking"}
                </Tag>
                {runtimeStatus?.acceleration.effective_gpu_layers
                  ? `${runtimeStatus.acceleration.effective_gpu_layers} layers`
                  : "0 layers"}
              </Space>
            </Descriptions.Item>
            <Descriptions.Item label="Current backend">
              {runtimeStatus?.acceleration.effective_backend ?? "-"}
            </Descriptions.Item>
            <Descriptions.Item label="Current device">
              {currentDeviceName}
            </Descriptions.Item>
            <Descriptions.Item label="Device memory">
              {formatBytes(selectedAccelerator?.memory_bytes)}
            </Descriptions.Item>
            <Descriptions.Item label="Accelerator preference">
              {runtimeStatus?.configuration.configuration.runtime.accelerator?.preference ?? "auto"}
            </Descriptions.Item>
            <Descriptions.Item label="Data directory">
              {runtimeStatus?.paths.data_directory ?? "-"}
            </Descriptions.Item>
            <Descriptions.Item label="Theme">跟随当前工作台样式</Descriptions.Item>
            <Descriptions.Item label="Startup">tomur open / tomur serve</Descriptions.Item>
          </Descriptions>
          <Space wrap>
            <Button
              icon={<Copy size={14} />}
              onClick={() => void onCopyText("tomur open", "已复制打开工作台命令")}
            >
              复制 open
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() => void onCopyText("tomur serve", "已复制 serve 命令")}
            >
              复制 serve
            </Button>
          </Space>
        </Space>
      )}

      {section === "models" && (
        <Space direction="vertical" size={16} className="drawer-stack">
          <Card
            size="small"
            title={`可见模型 ${visibleModels.length}`}
            extra={
              <Button
                type="text"
                size="small"
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur list --data-dir <path>", "已复制查看模型命令")}
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
            extra={
              <Button
                type="text"
                size="small"
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur ps --data-dir <path>", "已复制模型可见性命令")}
              >
                PS
              </Button>
            }
          >
            <List
              dataSource={installedPackages}
              locale={{ emptyText: "当前没有安装包记录" }}
              renderItem={(item) => (
                <List.Item>
                  <List.Item.Meta
                    title={
                      <Space wrap>
                        <span>{item.display_name}</span>
                        <Tag color={tagColor(item.status)}>{item.status}</Tag>
                      </Space>
                    }
                    description={`${item.id} / ${item.segment} / ${formatRelativeTime(item.updated_at_utc)}`}
                  />
                </List.Item>
              )}
            />
          </Card>

          <Card
            size="small"
            title="起步建议"
            extra={
              <Button
                type="primary"
                size="small"
                icon={<Download size={14} />}
                onClick={() =>
                  void onCopyText(
                    "tomur pull recommended",
                    "已复制推荐模型下载命令"
                  )
                }
              >
                复制推荐命令
              </Button>
            }
          >
            <Space direction="vertical" size={10} className="drawer-stack">
              <Typography.Text type="secondary">
                浏览器工作台当前只提供模型状态和下一步指引；模型下载、导入和删除仍通过 Tomur CLI 完成。
              </Typography.Text>
              <Collapse
                size="small"
                items={installedPackages.slice(0, 6).map((item) => ({
                  key: item.id,
                  label: item.display_name,
                  children: (
                    <PackageDetails packageItem={item} assets={item.assets} />
                  )
                }))}
              />
            </Space>
          </Card>
        </Space>
      )}

      {section === "downloads" && (
        <Space direction="vertical" size={16} className="drawer-stack">
          <Card
            size="small"
            title="当前机器建议"
            extra={<HardDrive size={16} />}
          >
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
            title={`推荐包 ${recommendedPackages.length}`}
            extra={
              <Button
                type="primary"
                size="small"
                icon={<Download size={14} />}
                onClick={() =>
                  void onCopyText(
                    "tomur pull recommended",
                    "已复制推荐下载命令"
                  )
                }
              >
                推荐下载
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
                onClick={() =>
                  void onCopyText("tomur list --catalog", "已复制 catalog 查看命令")
                }
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
                children: (
                  <DownloadPackageDetails
                    item={item}
                    onCopyText={onCopyText}
                  />
                )
              }))}
            />
          </Card>

          <Alert
            type="info"
            showIcon
            message="Web UI 当前不直接执行下载"
            description="Tomur 现阶段仍通过 CLI 执行下载、断点续传、校验和修复。这里提供推荐、状态和可复制命令，避免把未接通的动作伪装成可用功能。"
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

      {section === "agents" && (
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
      )}

      {section === "capabilities" && (
        <CapabilitiesPanel
          runtimeStatus={runtimeStatus}
          models={models}
          catalog={catalog}
          multimodalStatus={multimodalStatus}
          agentRuntime={agentRuntime}
          agentTools={agentTools}
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
            <Descriptions.Item label="OpenAI models">GET /v1/models</Descriptions.Item>
            <Descriptions.Item label="OpenAI chat">POST /v1/chat/completions</Descriptions.Item>
            <Descriptions.Item label="Claude Code models">
              GET /v1/models?limit=1000
            </Descriptions.Item>
            <Descriptions.Item label="Claude Code messages">POST /v1/messages</Descriptions.Item>
            <Descriptions.Item label="Ollama chat">POST /api/chat</Descriptions.Item>
            <Descriptions.Item label="Conversations">GET /api/conversations</Descriptions.Item>
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
          <Space wrap>
            <Button
              icon={<Copy size={14} />}
              onClick={() =>
                void onCopyText("tomur api-key create local", "已复制 API key 创建命令")
              }
            >
              复制创建命令
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() =>
                void onCopyText("GET /api/version", "已复制 version API")
              }
            >
              复制检查 API
            </Button>
          </Space>
        </Space>
      )}

      {section === "files" && (
        <Space direction="vertical" size={16} className="drawer-stack">
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
              {runtimeStatus?.paths.database_path ?? "-"}
            </Descriptions.Item>
            <Descriptions.Item label="Database status">
              {runtimeStatus?.database.status ?? "-"}
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
              onClick={() =>
                void onCopyText("tomur doctor", "已复制 doctor 命令")
              }
            >
              复制 doctor
            </Button>
          </Space>
        </Space>
      )}

      {section === "advanced" && (
        <Space direction="vertical" size={16} className="drawer-stack">
          <Alert
            type="info"
            showIcon
            message="高级能力保持本地受控边界"
            description="Agent 工具、OpenTelemetry exporter、AOT 审计和 smoke 套件仍以项目内 API 与文档记录为准。"
          />
          <Descriptions column={1} size="small" bordered>
            <Descriptions.Item label="Acceleration">
              {runtimeStatus?.acceleration.effective_backend ?? "-"}
            </Descriptions.Item>
            <Descriptions.Item label="GPU layers">
              {runtimeStatus?.acceleration.effective_gpu_layers ?? 0}
            </Descriptions.Item>
            <Descriptions.Item label="Proxy">
              {runtimeStatus?.proxy.message ?? "-"}
            </Descriptions.Item>
            <Descriptions.Item label="Telemetry">
              GET /api/agents/telemetry
            </Descriptions.Item>
            <Descriptions.Item label="Events">
              GET /api/agents/events
            </Descriptions.Item>
          </Descriptions>
          <List
            dataSource={multimodalStatus?.backends ?? []}
            locale={{ emptyText: "暂无高级能力状态" }}
            renderItem={(backend) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(backend.status)}>{backend.status}</Tag>
                      {backend.capability}
                    </Space>
                  }
                  description={backend.message}
                />
              </List.Item>
            )}
          />
          <Space wrap>
            <Button
              icon={<Copy size={14} />}
              onClick={() =>
                void onCopyText("GET /api/agents/telemetry", "已复制 telemetry API")
              }
            >
              复制 telemetry API
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() =>
                void onCopyText("dotnet publish app/Tomur.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false", "已复制自包含发布命令")
              }
            >
              复制发布命令
            </Button>
          </Space>
        </Space>
      )}
    </Space>
  );
}
