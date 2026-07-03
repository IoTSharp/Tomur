import { Alert, Button } from "antd";
import type { DiagnosticItem } from "../../types";
import type { SettingsSection } from "../../app/viewTypes";

export function InlineDiagnostics({
  chatReady,
  runtimeOk,
  warningDiagnostics,
  onOpenSettings,
  onOpenStatus
}: {
  chatReady: boolean;
  runtimeOk: boolean;
  warningDiagnostics: DiagnosticItem[];
  onOpenSettings: (section: SettingsSection) => void;
  onOpenStatus: () => void;
}) {
  return (
    <>
      {!chatReady && (
        <Alert
          className="inline-diagnostic"
          type="warning"
          showIcon
          message="Tomur 当前没有可见 Chat 模型"
          description="工作台已经连接本地 API，但不会在模型缺失时伪造回复。请先下载或导入模型。"
          action={
            <Button size="small" onClick={() => onOpenSettings("models")}>
              打开 Models
            </Button>
          }
        />
      )}

      {!runtimeOk && warningDiagnostics.length > 0 && (
        <Alert
          className="inline-diagnostic"
          type="info"
          showIcon
          message="当前存在待处理的本地诊断"
          description={warningDiagnostics[0]?.message}
          action={
            <Button size="small" onClick={onOpenStatus}>
              查看诊断
            </Button>
          }
        />
      )}
    </>
  );
}

