import { Alert, Button, Empty, Flex, List, Space, Spin, Tag } from "antd";
import { Download, FolderOpen, Layers3, Settings, Wrench } from "lucide-react";
import type { AgentRuntimeStatus, MultimodalRuntimeStatus, RuntimeStatusResponse } from "../../types";
import type { SettingsSection } from "../../app/settings";
import { tagColor } from "../../app/format";

export function StatusPanel({
  runtimeStatus,
  multimodalStatus,
  agentRuntime,
  loading,
  onOpenSettings
}: {
  runtimeStatus?: RuntimeStatusResponse;
  multimodalStatus?: MultimodalRuntimeStatus;
  agentRuntime?: AgentRuntimeStatus;
  loading: boolean;
  onOpenSettings: (section: SettingsSection) => void;
}) {
  if (loading && !runtimeStatus) {
    return <Spin />;
  }

  if (!runtimeStatus) {
    return <Empty description="暂无状态" />;
  }

  const warningDiagnostics = runtimeStatus.diagnostics.filter(
    (item) => item.severity !== "info" || item.status !== "ok"
  );

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <Alert
        type={runtimeStatus.status === "ok" ? "success" : "warning"}
        showIcon
        message={runtimeStatus.runtime.message}
        description={runtimeStatus.native_bundle.message}
      />

      <Flex gap={8} wrap>
        <Button icon={<Layers3 size={16} />} onClick={() => onOpenSettings("models")}>
          Models
        </Button>
        <Button icon={<Download size={16} />} onClick={() => onOpenSettings("downloads")}>
          Downloads
        </Button>
        <Button icon={<Wrench size={16} />} onClick={() => onOpenSettings("runtime")}>
          Runtime
        </Button>
        <Button icon={<Settings size={16} />} onClick={() => onOpenSettings("agents")}>
          Agents
        </Button>
        <Button icon={<Layers3 size={16} />} onClick={() => onOpenSettings("capabilities")}>
          Capabilities
        </Button>
        <Button icon={<FolderOpen size={16} />} onClick={() => onOpenSettings("files")}>
          Files
        </Button>
        <Button icon={<Settings size={16} />} onClick={() => onOpenSettings("api")}>
          API
        </Button>
      </Flex>

      <List
        size="small"
        header="诊断"
        dataSource={warningDiagnostics.slice(0, 8)}
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

      <List
        size="small"
        header="多模态后端"
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

      <List
        size="small"
        header="Agent 工具"
        dataSource={agentRuntime?.tools ?? []}
        locale={{ emptyText: "暂无 Agent 工具状态" }}
        renderItem={(tool) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space>
                  <Tag color={tagColor(tool.status)}>{tool.status}</Tag>
                  {tool.name}
                </Space>
              }
              description={tool.message}
            />
          </List.Item>
        )}
      />
    </Space>
  );
}
