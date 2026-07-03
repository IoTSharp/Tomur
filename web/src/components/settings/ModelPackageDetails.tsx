import { Button, Descriptions, List, Space, Tag, Typography } from "antd";
import { Download } from "lucide-react";
import type {
  InstalledModelAsset,
  InstalledModelPackage,
  ModelCatalogAsset,
  ModelCatalogBundleAsset,
  ModelCatalogPackage
} from "../../types";
import { formatBytes } from "../../app/format";
import type { CopyTextHandler } from "../../app/viewTypes";

export function PackageDetails({
  packageItem,
  assets
}: {
  packageItem: InstalledModelPackage;
  assets: InstalledModelAsset[];
}) {
  return (
    <Space direction="vertical" size={10} className="drawer-stack">
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Package ID">{packageItem.id}</Descriptions.Item>
        <Descriptions.Item label="Directory">{packageItem.directory}</Descriptions.Item>
        <Descriptions.Item label="Primary path">{packageItem.primary_path}</Descriptions.Item>
        <Descriptions.Item label="License notice">{packageItem.license_notice}</Descriptions.Item>
      </Descriptions>
      <List
        size="small"
        dataSource={assets}
        locale={{ emptyText: "当前包没有资产记录" }}
        renderItem={(asset) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space wrap>
                  <span>{asset.path}</span>
                  {asset.sha256_verified ? <Tag color="green">sha256</Tag> : <Tag>unchecked</Tag>}
                </Space>
              }
              description={`${asset.source_repository_id} / ${formatBytes(asset.size_bytes)}`}
            />
          </List.Item>
        )}
      />
    </Space>
  );
}

export function DownloadPackageDetails({
  item,
  onCopyText
}: {
  item: ModelCatalogPackage;
  onCopyText: CopyTextHandler;
}) {
  return (
    <Space direction="vertical" size={12} className="drawer-stack">
      <Typography.Text type="secondary">{item.description}</Typography.Text>
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="Package ID">{item.id}</Descriptions.Item>
        <Descriptions.Item label="Task">{item.task}</Descriptions.Item>
        <Descriptions.Item label="Runtime">{item.runtime}</Descriptions.Item>
        <Descriptions.Item label="Primary file">{item.primary_file_name ?? "-"}</Descriptions.Item>
        <Descriptions.Item label="Recommended tier">{item.hardware_tier}</Descriptions.Item>
        <Descriptions.Item label="Minimum memory">
          {item.minimum_memory_bytes ? formatBytes(item.minimum_memory_bytes) : "-"}
        </Descriptions.Item>
        <Descriptions.Item label="License">{item.license ?? "-"}</Descriptions.Item>
      </Descriptions>

      <Space wrap>
        <Button
          size="small"
          type="primary"
          icon={<Download size={14} />}
          onClick={() => void onCopyText(`tomur pull ${item.id}`, `已复制 ${item.id} 下载命令`)}
        >
          复制下载命令
        </Button>
        <Button
          size="small"
          onClick={() =>
            void onCopyText(`tomur pull ${item.id} --force`, `已复制 ${item.id} 强制重装命令`)
          }
        >
          复制重装命令
        </Button>
      </Space>

      <List
        size="small"
        header="远端资产"
        dataSource={item.assets}
        locale={{ emptyText: "暂无远端资产明细" }}
        renderItem={(asset) => <List.Item>{renderAssetSummary(asset)}</List.Item>}
      />

      <List
        size="small"
        header="Bundle sidecar"
        dataSource={item.bundle_assets}
        locale={{ emptyText: "当前包没有 sidecar bundle 资产" }}
        renderItem={(asset) => <List.Item>{renderBundleAssetSummary(asset)}</List.Item>}
      />
    </Space>
  );
}

export function renderCatalogSummary(item: ModelCatalogPackage) {
  const parts = [
    item.task,
    item.runtime,
    item.quantization,
    item.size_bytes ? formatBytes(item.size_bytes) : null
  ].filter(Boolean);

  return parts.join(" / ");
}

function renderAssetSummary(asset: ModelCatalogAsset) {
  return `${asset.repository_id} / ${asset.relative_path} -> ${asset.target_relative_path}`;
}

function renderBundleAssetSummary(asset: ModelCatalogBundleAsset) {
  const required = asset.is_required ? "required" : "optional";
  const details = [asset.role, required, asset.file_name, asset.size_bytes ? formatBytes(asset.size_bytes) : null]
    .filter(Boolean)
    .join(" / ");

  return `${details} - ${asset.description}`;
}

