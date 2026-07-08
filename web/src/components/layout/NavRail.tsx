import { Tooltip } from "antd";
import {
  Activity,
  MessageSquare,
  Moon,
  ScrollText,
  Settings,
  Sun
} from "lucide-react";
import type { AppView } from "../../app/viewTypes";
import type { ThemeMode } from "../../app/theme";

interface NavItem {
  key: AppView;
  label: string;
  icon: typeof MessageSquare;
}

const NAV_ITEMS: NavItem[] = [
  { key: "chat", label: "对话", icon: MessageSquare },
  { key: "status", label: "状态", icon: Activity },
  { key: "logs", label: "日志", icon: ScrollText },
  { key: "settings", label: "设置", icon: Settings }
];

export function NavRail({
  activeView,
  onChangeView,
  themeMode,
  onToggleTheme,
  version,
  runtimeOk
}: {
  activeView: AppView;
  onChangeView: (view: AppView) => void;
  themeMode: ThemeMode;
  onToggleTheme: () => void;
  version?: string;
  runtimeOk: boolean;
}) {
  return (
    <nav className="nav-rail">
      <div className="nav-rail-brand">
        <img src="/icons/app-icon.svg" alt="Tomur" />
      </div>

      <div className="nav-rail-items">
        {NAV_ITEMS.map((item) => {
          const Icon = item.icon;
          const active = activeView === item.key;
          return (
            <Tooltip key={item.key} title={item.label} placement="right">
              <button
                type="button"
                className={`nav-rail-item${active ? " nav-rail-item-active" : ""}`}
                aria-label={item.label}
                aria-current={active}
                onClick={() => onChangeView(item.key)}
              >
                <Icon size={20} strokeWidth={active ? 2.4 : 1.8} />
              </button>
            </Tooltip>
          );
        })}
      </div>

      <div className="nav-rail-footer">
        <Tooltip title={runtimeOk ? "本地 runtime 就绪" : "runtime 待处理"} placement="right">
          <button
            type="button"
            className="nav-rail-status"
            aria-label="查看状态"
            onClick={() => onChangeView("status")}
          >
            <span className={`nav-rail-dot ${runtimeOk ? "is-ok" : "is-warn"}`} />
          </button>
        </Tooltip>
        <Tooltip title={themeMode === "dark" ? "切换到浅色" : "切换到深色"} placement="right">
          <button
            type="button"
            className="nav-rail-item"
            aria-label="切换主题"
            onClick={onToggleTheme}
          >
            {themeMode === "dark" ? <Sun size={18} /> : <Moon size={18} />}
          </button>
        </Tooltip>
        {version && <span className="nav-rail-version">{version}</span>}
      </div>
    </nav>
  );
}
