import type { ReactNode } from "react";
import { Alert, Button, Card, Descriptions, List, Space, Tag, Typography } from "antd";
import { Copy, RefreshCcw, Trash2, Wrench } from "lucide-react";
import type {
  AccelerationPlan,
  DiagnosticItem,
  NativeBundlePrepareResult,
  RuntimeStatusResponse
} from "../../types";
import { formatBytes, tagColor } from "../../app/format";
import type { CopyTextHandler } from "../../app/viewTypes";

export function RuntimeSettingsPanel({
  runtimeStatus,
  prepareResult,
  runtimeAction,
  onPrepareNativeRuntime,
  onUnloadRuntimeSession,
  onCopyText
}: {
  runtimeStatus?: RuntimeStatusResponse;
  prepareResult?: NativeBundlePrepareResult;
  runtimeAction: "prepare" | "unload" | null;
  onPrepareNativeRuntime: () => Promise<void>;
  onUnloadRuntimeSession: () => Promise<void>;
  onCopyText: CopyTextHandler;
}) {
  const diagnostics = runtimeStatus?.diagnostics ?? [];
  const visibleDiagnostics = diagnostics.filter(
    (item) => item.severity !== "info" || item.status !== "ok"
  );
  const nativeReady = runtimeStatus?.native_bundle.status === "ok";
  const session = runtimeStatus?.session;
  const sessionLoaded = session?.loaded === true;
  const managedModels = runtimeStatus?.managed_models ?? [];
  const prepareChangedFiles =
    prepareResult?.files.filter((file) =>
      ["copied", "repaired", "aliased", "error"].includes(file.status)
    ) ?? [];
  const primaryDiagnostic = visibleDiagnostics.at(0);
  const runtimeHints = collectRuntimeHints(runtimeStatus, visibleDiagnostics);

  return (
    <Space direction="vertical" size={16} className="drawer-stack">
      <AccelerationSummary acceleration={runtimeStatus?.acceleration} />

      <Alert
        type={runtimeStatus?.status === "ok" ? "success" : "warning"}
        showIcon
        message={runtimeStatus?.runtime.message ?? "Runtime 状态尚未加载"}
        description={runtimeStatus?.native_bundle.message ?? "刷新状态后可以查看 native bundle、session 和诊断动作。"}
      />

      <Card
        size="small"
        title="Native runtime"
        extra={<Tag color={tagColor(runtimeStatus?.native_bundle.status ?? "checking")}>{runtimeStatus?.native_bundle.status ?? "checking"}</Tag>}
      >
        <ActionBlock
          title={nativeReady ? "Bundle 已准备" : "准备或修复 native bundle"}
          description={
            nativeReady
              ? "托管 runtime 目录中的 native library 已通过当前探测。需要重新释放或修复时可以再次执行 prepare。"
              : "执行后端 prepare API，从随包 native bundle 释放或修复 Tomur 管理目录中的 runtime 文件。"
          }
          nextStep={
            nativeReady
              ? "下一步：发送一次 Chat 请求会按需加载 llama.cpp session。"
              : "下一步：prepare 完成后重新查看组件状态；若仍失败，运行 doctor 查看缺失或校验错误。"
          }
          action={
            <Space wrap>
              <Button
                type={nativeReady ? "default" : "primary"}
                icon={<Wrench size={14} />}
                loading={runtimeAction === "prepare"}
                disabled={runtimeAction === "unload"}
                onClick={() => void onPrepareNativeRuntime()}
              >
                {nativeReady ? "重新准备" : "准备 runtime"}
              </Button>
              <Button
                icon={<Copy size={14} />}
                onClick={() => void onCopyText("tomur native prepare", "已复制 native prepare 命令")}
              >
                复制 CLI
              </Button>
            </Space>
          }
        />

        {prepareResult && (
          <Alert
            className="runtime-result"
            type={prepareResult.status === "error" ? "warning" : "success"}
            showIcon
            message={prepareResult.message}
            description={`${prepareChangedFiles.length} 个文件在最近一次 prepare 中发生复制、修复或错误。`}
          />
        )}
        {prepareChangedFiles.length > 0 && (
          <List
            className="runtime-result-list"
            size="small"
            dataSource={prepareChangedFiles.slice(0, 5)}
            renderItem={(file) => (
              <List.Item>
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag color={tagColor(file.status)}>{file.status}</Tag>
                      {file.destination_path}
                    </Space>
                  }
                  description={file.message}
                />
              </List.Item>
            )}
          />
        )}
      </Card>

      <Card
        size="small"
        title="显卡与加速"
        extra={<Tag color={tagColor(runtimeStatus?.acceleration.status ?? "checking")}>{runtimeStatus?.acceleration.status ?? "checking"}</Tag>}
      >
        <AccelerationStatus acceleration={runtimeStatus?.acceleration} />
      </Card>

      <Card
        size="small"
        title="Session"
        extra={<Tag color={tagColor(runtimeStatus?.runtime.status ?? "checking")}>{runtimeStatus?.runtime.status ?? "checking"}</Tag>}
      >
        <ActionBlock
          title={sessionLoaded ? "当前已有加载的 session" : "当前没有加载的 session"}
          description={
            sessionLoaded
              ? "卸载会取消当前生成并释放模型、KV、文件句柄、工作区与 expert cache。"
              : "Tomur 采用首个兼容请求按需加载 session。没有加载时无需手动 unload。"
          }
          nextStep={
            sessionLoaded
              ? "下一步：如果要切换模型或释放内存，先卸载 session，再发起新的请求。"
              : "下一步：选择一个 ready 模型并发送 Chat 请求，runtime 会加载首个 session。"
          }
          action={
            <Space wrap>
              <Button
                danger={sessionLoaded}
                icon={<Trash2 size={14} />}
                loading={runtimeAction === "unload"}
                disabled={!sessionLoaded || runtimeAction === "prepare"}
                onClick={() => void onUnloadRuntimeSession()}
              >
                卸载 session
              </Button>
              <Button
                icon={<Copy size={14} />}
                onClick={() =>
                  void onCopyText(
                    "POST /api/runtime/session/unload",
                    "已复制 session unload API"
                  )
                }
              >
                复制 API
              </Button>
            </Space>
          }
        />
        {sessionLoaded && session && (
          <Descriptions className="runtime-result" column={{ xs: 1, sm: 2 }} size="small" bordered>
            <Descriptions.Item label="Provider">{session.provider_id ?? session.mode ?? "-"}</Descriptions.Item>
            <Descriptions.Item label="Model">{session.model_id ?? "-"}</Descriptions.Item>
            <Descriptions.Item label="State">{session.busy ? "generating" : "idle"}</Descriptions.Item>
            <Descriptions.Item label="Context">{session.context_size ?? "-"}</Descriptions.Item>
            <Descriptions.Item label="Resident">{formatBytes(session.resident_bytes)}</Descriptions.Item>
            <Descriptions.Item label="KV">{formatBytes(session.kv_bytes)}</Descriptions.Item>
            <Descriptions.Item label="Scratch">{formatBytes(session.scratch_bytes)}</Descriptions.Item>
            <Descriptions.Item label="Expert cache">{formatBytes(session.expert_cache_bytes)}</Descriptions.Item>
            <Descriptions.Item label="Requests">{session.request_count}</Descriptions.Item>
            <Descriptions.Item label="Tokens">{session.prompt_tokens} / {session.completion_tokens}</Descriptions.Item>
            <Descriptions.Item label="Cache hit/miss/evict">
              {session.expert_cache_hits ?? 0} / {session.expert_cache_misses ?? 0} / {session.expert_cache_evictions ?? 0}
            </Descriptions.Item>
            <Descriptions.Item label="Expert I/O">
              {session.expert_disk_reads ?? 0} / {formatBytes(session.expert_disk_bytes)}
            </Descriptions.Item>
          </Descriptions>
        )}
      </Card>

      <Card
        size="small"
        title="Managed models"
        extra={<Tag>{managedModels.length}</Tag>}
      >
        <List
          size="small"
          dataSource={managedModels}
          locale={{ emptyText: "暂无 managed model" }}
          renderItem={(model) => (
            <List.Item>
              <List.Item.Meta
                title={
                  <Space wrap>
                    <Tag color={tagColor(model.status)}>{model.status}</Tag>
                    <Typography.Text ellipsis={{ tooltip: model.model_id }} style={{ maxWidth: 240 }}>
                      {model.model_id}
                    </Typography.Text>
                    <Typography.Text type="secondary">{model.provider_id ?? "provider unavailable"}</Typography.Text>
                  </Space>
                }
                description={
                  <Space direction="vertical" size={4}>
                    <Typography.Text type="secondary">
                      {model.architecture} / {model.quantization} / resident {formatBytes(model.resident_bytes)} / KV {formatBytes(model.kv_bytes)} / expert cache {formatBytes(model.expert_cache_bytes)}
                    </Typography.Text>
                    <Space wrap size={4}>
                      <ReadinessTag label="provider" ready={model.provider_discovered} />
                      <ReadinessTag label="metadata" ready={model.metadata_valid} />
                      <ReadinessTag label="assets" ready={model.assets_complete} />
                      <ReadinessTag label="forward" ready={model.forward_verified} />
                      <ReadinessTag label="session" ready={model.session_loaded} />
                    </Space>
                    {model.diagnostics[0] && (
                      <Typography.Text type="secondary">{model.diagnostics[0].message}</Typography.Text>
                    )}
                  </Space>
                }
              />
            </List.Item>
          )}
        />
      </Card>

      <Card
        size="small"
        title="诊断与后端提示"
        extra={
          primaryDiagnostic ? (
            <Tag color={tagColor(primaryDiagnostic.status)}>{primaryDiagnostic.status}</Tag>
          ) : (
            <Tag color="green">ok</Tag>
          )
        }
      >
        <Space direction="vertical" size={12} className="drawer-stack">
          <Typography.Text type="secondary">
            {primaryDiagnostic?.message ?? "当前没有需要处理的 runtime 诊断。"}
          </Typography.Text>
          <List
            size="small"
            dataSource={runtimeHints}
            locale={{ emptyText: "暂无后端提示" }}
            renderItem={(item) => <List.Item>{item}</List.Item>}
          />
          <Space wrap>
            <Button
              icon={<RefreshCcw size={14} />}
              onClick={() =>
                void onCopyText("GET /api/runtime/status", "已复制 runtime status API")
              }
            >
              复制状态 API
            </Button>
            <Button
              icon={<Copy size={14} />}
              onClick={() => void onCopyText("tomur doctor", "已复制 doctor 命令")}
            >
              复制 doctor
            </Button>
          </Space>
        </Space>
      </Card>

      <List
        dataSource={runtimeStatus?.native_bundle.components ?? []}
        locale={{ emptyText: "暂无 native component 状态" }}
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
  );
}

function AccelerationSummary({ acceleration }: { acceleration?: AccelerationPlan }) {
  const selected = acceleration?.selected_accelerator;
  const activeLabel =
    acceleration?.status === "accelerated" && selected
      ? selected.kind
      : acceleration?.status === "cpu"
        ? "CPU"
        : acceleration?.status ?? "checking";
  const deviceLabel = selected
    ? `${selected.name}${selected.integrated ? " (integrated)" : ""}`
    : acceleration?.effective_backend?.toLowerCase() === "cpu"
      ? "CPU"
      : acceleration?.fallback_reason
        ? "CPU fallback"
        : "-";

  return (
    <Card
      size="small"
      title="当前加速"
      extra={<Tag color={tagColor(acceleration?.status ?? "checking")}>{acceleration?.status ?? "checking"}</Tag>}
    >
      <div className="acceleration-summary-grid">
        <SummaryMetric label="加速" value={activeLabel} />
        <SummaryMetric label="后端" value={acceleration?.effective_backend ?? "-"} />
        <SummaryMetric label="显卡 / 设备" value={deviceLabel} />
        <SummaryMetric label="OpenVINO device" value={acceleration?.openvino_device ?? "-"} />
        <SummaryMetric label="GPU layers" value={String(acceleration?.effective_gpu_layers ?? 0)} />
        <SummaryMetric label="选择键" value={acceleration?.selected_accelerator_key ?? "-"} />
      </div>
      {acceleration?.fallback_reason && (
        <Typography.Text className="acceleration-summary-fallback" type="secondary">
          {acceleration.fallback_reason}
        </Typography.Text>
      )}
    </Card>
  );
}

function SummaryMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="acceleration-summary-item">
      <span className="acceleration-summary-label">{label}</span>
      <span className="acceleration-summary-value">{value}</span>
    </div>
  );
}

function AccelerationStatus({ acceleration }: { acceleration?: AccelerationPlan }) {
  const selected = acceleration?.selected_accelerator;
  const devices = acceleration?.devices ?? [];
  const backends = acceleration?.backends ?? [];

  return (
    <Space direction="vertical" size={12} className="drawer-stack">
      <Descriptions column={1} size="small" bordered>
        <Descriptions.Item label="当前后端">
          {acceleration?.effective_backend ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="偏好">
          {acceleration?.preferred_backend ?? "auto"}
        </Descriptions.Item>
        <Descriptions.Item label="当前设备">
          {selected ? `${selected.kind} / ${selected.name}` : "CPU"}
        </Descriptions.Item>
        <Descriptions.Item label="显存">
          {selected ? formatBytes(selected.memory_bytes) : "-"}
        </Descriptions.Item>
        <Descriptions.Item label="GPU layers">
          {acceleration?.effective_gpu_layers ?? 0}
        </Descriptions.Item>
        <Descriptions.Item label="OpenVINO device">
          {acceleration?.openvino_device ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="NPU opt-in">
          {acceleration?.allow_npu ? "enabled" : "disabled"}
        </Descriptions.Item>
        <Descriptions.Item label="NPU prefill">
          {acceleration?.npu_prefill_chunk ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="选择键">
          {acceleration?.selected_accelerator_key ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="配置选择键">
          {acceleration?.configured_accelerator_key ?? "-"}
        </Descriptions.Item>
        <Descriptions.Item label="Fallback">
          {acceleration?.fallback_reason ?? "-"}
        </Descriptions.Item>
      </Descriptions>

      <List
        size="small"
        header={`探测到的设备 ${devices.length}`}
        dataSource={devices}
        locale={{ emptyText: "当前未探测到可用于 llama.cpp 的 GPU 或 NPU 设备" }}
        renderItem={(device) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space wrap>
                  <Tag color={device.selection_key === acceleration?.selected_accelerator_key ? "green" : "default"}>
                    {device.selection_key === acceleration?.selected_accelerator_key ? "selected" : device.backend}
                  </Tag>
                  <Typography.Text>{device.name}</Typography.Text>
                  {device.integrated && <Tag>integrated</Tag>}
                </Space>
              }
              description={`${device.kind} / ${device.backend} / ${formatBytes(device.memory_bytes)} / ${device.selection_key}`}
            />
          </List.Item>
        )}
      />

      <List
        size="small"
        header="加速后端"
        dataSource={backends}
        locale={{ emptyText: "暂无加速后端状态" }}
        renderItem={(backend) => (
          <List.Item>
            <List.Item.Meta
              title={
                <Space wrap>
                  <Tag color={tagColor(backend.status)}>{backend.status}</Tag>
                  {backend.display_name}
                </Space>
              }
              description={
                backend.actions?.length > 0
                  ? `${backend.library_name} / ${backend.message} / ${backend.actions[0]}`
                  : `${backend.library_name} / ${backend.message}`
              }
            />
          </List.Item>
        )}
      />
    </Space>
  );
}

function ReadinessTag({ label, ready }: { label: string; ready: boolean }) {
  return <Tag color={ready ? "green" : "default"}>{label}: {ready ? "yes" : "no"}</Tag>;
}

function ActionBlock({
  title,
  description,
  nextStep,
  action
}: {
  title: string;
  description: string;
  nextStep: string;
  action: ReactNode;
}) {
  return (
    <div className="runtime-action">
      <div className="runtime-action-copy">
        <Typography.Text strong>{title}</Typography.Text>
        <Typography.Text type="secondary">{description}</Typography.Text>
        <Typography.Text className="runtime-next-step">{nextStep}</Typography.Text>
      </div>
      <div className="runtime-action-controls">{action}</div>
    </div>
  );
}

function collectRuntimeHints(
  runtimeStatus: RuntimeStatusResponse | undefined,
  diagnostics: DiagnosticItem[]
) {
  const actions = [
    ...(runtimeStatus?.runtime.actions ?? []),
    ...diagnostics.flatMap((diagnostic) => diagnostic.actions)
  ];

  return Array.from(new Set(actions)).slice(0, 6);
}
