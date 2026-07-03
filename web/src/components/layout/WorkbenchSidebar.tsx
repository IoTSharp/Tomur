import { Badge, Button, Card, Flex, Space, Tooltip, Typography } from "antd";
import { Conversations } from "@ant-design/x";
import { MessageSquarePlus, Trash2 } from "lucide-react";

export interface ConversationListItem {
  key: string;
  label: string;
  group: string;
}

export function WorkbenchSidebar({
  versionLabel,
  runtimeOk,
  runtimeSeverity,
  loadingConversations,
  runtimeMessage,
  conversationItems,
  activeConversationId,
  onStartConversation,
  onOpenStatus,
  onOpenConversation,
  onRemoveConversation
}: {
  versionLabel: string;
  runtimeOk: boolean;
  runtimeSeverity: "success" | "warning";
  loadingConversations: boolean;
  runtimeMessage?: string;
  conversationItems: ConversationListItem[];
  activeConversationId: string;
  onStartConversation: () => void;
  onOpenStatus: () => void;
  onOpenConversation: (conversationId: string) => void;
  onRemoveConversation: (conversationId: string) => void;
}) {
  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-copy">
          <span className="brand-mark">
            <img src="/icons/app-icon.svg" alt="" aria-hidden="true" />
          </span>
          <div>
            <Typography.Title level={4}>Tomur</Typography.Title>
            <Typography.Text type="secondary">
              {versionLabel}
            </Typography.Text>
          </div>
        </div>
        <Tooltip title="新建会话">
          <Button icon={<MessageSquarePlus size={16} />} onClick={onStartConversation} />
        </Tooltip>
      </div>

      <Card className="sidebar-card" bordered={false}>
        <Space direction="vertical" size={8} style={{ width: "100%" }}>
          <Flex justify="space-between" align="center">
            <Space size={8}>
              <Badge status={runtimeSeverity} />
              <Typography.Text strong>
                {runtimeOk ? "本地工作台已就绪" : "需要完成本地准备"}
              </Typography.Text>
            </Space>
            <Button type="text" size="small" onClick={onOpenStatus}>
              详情
            </Button>
          </Flex>
          <Typography.Text type="secondary">
            {loadingConversations
              ? "正在加载本地会话历史"
              : runtimeMessage ?? "正在读取本地运行状态"}
          </Typography.Text>
        </Space>
      </Card>

      <Conversations
        className="conversation-list"
        items={conversationItems}
        activeKey={activeConversationId}
        groupable
        onActiveChange={(key) => onOpenConversation(String(key))}
        menu={(item) => ({
          items: [{ key: "delete", icon: <Trash2 size={14} />, label: "删除" }],
          onClick: ({ key }) => {
            if (key === "delete") {
              onRemoveConversation(String(item.key));
            }
          }
        })}
      />
    </aside>
  );
}

