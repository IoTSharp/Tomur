import { Alert, Button, Card, Descriptions, Empty, List, Space, Spin, Tag, Typography } from "antd";
import { RefreshCcw } from "lucide-react";
import type {
  AgentRuntimeStatus,
  AgentToolMapResponse,
  ModelCatalogResponse,
  MultimodalRuntimeStatus,
  OpenAiModel,
  RuntimeStatusResponse
} from "../../types";
import { formatBytes, formatRelativeTime, tagColor } from "../../app/format";
import type { SettingsSection } from "../../app/viewTypes";
import { StatusPill } from "./StatusPill";

export function StatusView({
  runtimeStatus,
  multimodalStatus,
  agentRuntime,
  models,
  catalog,
  agentTools,
  loading,
  onRefreshStatus,
  onOpenSettings
}: {
  runtimeStatus?: RuntimeStatusResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  models: OpenAiModel[];
  catalog?: ModelCatalogResponse;
  agentTools?: AgentToolMapResponse;
  loading: boolean;
  onRefreshStatus: () => void;
  onOpenSettings: (section: SettingsSection) => void;
}) {
  if (loading && !runtimeStatus) {
    return (
      <section className="page-view page-view-centered">
        <Spin />
      </section>
    );
  }

  if (!runtimeStatus) {
    return (
      <section className="page-view page-view-centered">
        <Empty description="暂无状态">
          <Button icon={<RefreshCcw size={16} />} onClick={onRefreshStatus}>
            刷新
          </Button>
        </Empty>
      </section>
    );
  }

  const system = runtimeStatus.system;
  const acceleration = runtimeStatus.acceleration;
  const selected = acceleration.selected_accelerator;
  const warningDiagnostics = runtimeStatus.diagnostics.filter(
    (item) => item.severity !== "info" || item.status !== "ok"
  );
  const multimodalReady = (multimodalStatus?.backends ?? []).filter(
    (item) => item.status === "ok"
  ).length;
  const agentTools_ = agentTools?.tools ?? [];

  return (
    <section className="page-view">
      <header className="page-header">
        <div>
          <Typography.Title level={3}>本地状态</Typography.Title>
          <Typography.Text type="secondary">
            系统、加速、运行时、多模态与 Agent 的实时快照 · 更新于 {formatRelativeTime(runtimeStatus.checked_at)}
          </Typography.Text>
        </div>
        <Button icon={<RefreshCcw size={16} />} loading={loading} onClick={onRefreshStatus}>
          刷新
        </Button>
      </header>

      <div className="status-pill-row">
        <StatusPill
          label="Runtime"
          value={runtimeStatus.status}
          tone={runtimeStatus.status === "ok" ? "success" : "warning"}
          onClick={() => onOpenSettings("runtime")}
        />
        <StatusPill
          label="模型"
          value={String(models.length)}
          tone={models.length > 0 ? "success" : "warning"}
          onClick={() => onOpenSettings("models")}
        />
        <StatusPill
          label="加速"
          value={acceleration.status}
          tone={acceleration.status === "accelerated" ? "success" : "default"}
          onClick={() => onOpenSettings("runtime")}
        />
        <StatusPill
          label="多模态"
          value={multimodalStatus ? `${multimodalReady}/${multimodalStatus.backends.length}` : "-"}
          tone={multimodalStatus?.status === "ok" ? "success" : "default"}
          onClick={() => onOpenSettings("advanced")}
        />
        <StatusPill
          label="Agent"
          value={agentRuntime?.status ?? "-"}
          tone={agentRuntime?.status === "ok" ? "success" : "default"}
          onClick={() => onOpenSettings("advanced")}
        />
        <StatusPill
          label="端口"
          value={runtimeStatus.port.status}
          tone={runtimeStatus.port.status === "ok" ? "success" : "warning"}
          onClick={() => onOpenSettings("api")}
        />
      </div>

      {warningDiagnostics.length > 0 && (
        <Alert
          type="warning"
          showIcon
          message={`${warningDiagnostics.length} 项诊断待处理`}
          description={warningDiagnostics[0]?.message}
        />
      )}

      <div className="status-grid">
        <Card size="small" title="系统">
          <Descriptions column={1} size="small">
            <Descriptions.Item label="操作系统">{system.os_description}</Descriptions.Item>
            <Descriptions.Item label="CPU">{system.cpu_name ?? "-"}</Descriptions.Item>
            <Descriptions.Item label="逻辑核心">{system.processor_count}</Descriptions.Item>
            <Descriptions.Item label="内存">{formatBytes(system.total_memory_bytes)}</Descriptions.Item>
            <Descriptions.Item label="架构">{system.process_architecture}</Descriptions.Item>
            <Descriptions.Item label="运行时">{system.framework_description}</Descriptions.Item>
            <Descriptions.Item label="磁盘可用">
              {formatBytes(runtimeStatus.disk.available_bytes)} / {formatBytes(runtimeStatus.disk.total_bytes)}
            </Descriptions.Item>
          </Descriptions>
        </Card>

        <Card
          size="small"
          title="加速"
          extra={<Tag color={tagColor(acceleration.status)}>{acceleration.status}</Tag>}
        >
          <Descriptions column={1} size="small">
            <Descriptions.Item label="后端">{acceleration.effective_backend}</Descriptions.Item>
            <Descriptions.Item label="设备">
              {selected ? `${selected.kind} / ${selected.name}` : "CPU"}
            </Descriptions.Item>
            <Descriptions.Item label="显存">
              {selected ? formatBytes(selected.memory_bytes) : "-"}
            </Descriptions.Item>
            <Descriptions.Item label="GPU layers">{acceleration.effective_gpu_layers}</Descriptions.Item>
            <Descriptions.Item label="OpenVINO">{acceleration.openvino_device ?? "-"}</Descriptions.Item>
          </Descriptions>
          {acceleration.fallback_reason && (
            <Typography.Text type="secondary">{acceleration.fallback_reason}</Typography.Text>
          )}
        </Card>

        <Card
          size="small"
          title="Native bundle"
          extra={<Tag color={tagColor(runtimeStatus.native_bundle.status)}>{runtimeStatus.native_bundle.status}</Tag>}
        >
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            <Typography.Text type="secondary">{runtimeStatus.native_bundle.message}</Typography.Text>
            <List
              size="small"
              dataSource={runtimeStatus.native_bundle.components}
              locale={{ emptyText: "暂无组件" }}
              renderItem={(item) => (
                <List.Item>
                  <List.Item.Meta
                    title={
                      <Space>
                        <Tag color={tagColor(item.status)}>{item.status}</Tag>
                        {item.display_name}
                      </Space>
                    }
                    description={item.message}
                  />
                </List.Item>
              )}
            />
          </Space>
        </Card>

        <Card size="small" title="能力概览">
          <Descriptions column={1} size="small">
            <Descriptions.Item label="可见模型">{models.length}</Descriptions.Item>
            <Descriptions.Item label="Catalog 包">{catalog?.packages.length ?? "-"}</Descriptions.Item>
            <Descriptions.Item label="多模态后端">
              {multimodalStatus ? `${multimodalReady}/${multimodalStatus.backends.length}` : "-"}
            </Descriptions.Item>
            <Descriptions.Item label="Agent 工具">{agentTools_.length}</Descriptions.Item>
            <Descriptions.Item label="硬件档位">{catalog?.hardware.tier ?? "-"}</Descriptions.Item>
          </Descriptions>
        </Card>

        <Card size="small" title="多模态后端">
          <List
            size="small"
            dataSource={multimodalStatus?.backends ?? []}
            locale={{ emptyText: "暂无多模态状态" }}
            renderItem={(backend) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(backend.status)}>{backend.status}</Tag>
                      {backend.display_name}
                    </Space>
                  }
                  description={backend.message}
                />
              </List.Item>
            )}
          />
        </Card>

        <Card size="small" title="Agent 工具">
          <List
            size="small"
            dataSource={agentRuntime?.tools ?? []}
            locale={{ emptyText: "暂无 Agent 工具状态" }}
            renderItem={(tool) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(tool.status)}>{tool.status}</Tag>
                      {tool.display_name}
                    </Space>
                  }
                  description={tool.message}
                />
              </List.Item>
            )}
          />
        </Card>

        <Card size="small" title="诊断" className="status-grid-wide">
          <List
            size="small"
            dataSource={warningDiagnostics}
            locale={{ emptyText: "没有需要处理的诊断" }}
            renderItem={(item) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(item.status)}>{item.status}</Tag>
                      {item.name}
                    </Space>
                  }
                  description={item.message}
                />
              </List.Item>
            )}
          />
        </Card>
      </div>
    </section>
  );
}
