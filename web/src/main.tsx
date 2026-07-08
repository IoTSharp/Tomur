import React, { useCallback, useEffect, useMemo, useState } from "react";
import ReactDOM from "react-dom/client";
import { App as AntApp, ConfigProvider, theme as antdTheme } from "antd";
import zhCN from "antd/locale/zh_CN";
import { XProvider } from "@ant-design/x";
import App from "./App";
import {
  applyThemeToDocument,
  persistTheme,
  readStoredTheme,
  type ThemeMode
} from "./app/theme";
import "./styles.css";

function Root() {
  const [themeMode, setThemeMode] = useState<ThemeMode>(() => readStoredTheme());

  useEffect(() => {
    applyThemeToDocument(themeMode);
    persistTheme(themeMode);
  }, [themeMode]);

  const toggleTheme = useCallback(() => {
    setThemeMode((current) => (current === "dark" ? "light" : "dark"));
  }, []);

  const themeConfig = useMemo(
    () => ({
      algorithm: themeMode === "dark" ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
      token: {
        colorPrimary: "#0d6b5f",
        borderRadius: 10,
        fontFamily:
          '"Noto Sans SC", "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif'
      }
    }),
    [themeMode]
  );

  return (
    <ConfigProvider locale={zhCN} theme={themeConfig}>
      <XProvider>
        <AntApp>
          <App themeMode={themeMode} onToggleTheme={toggleTheme} />
        </AntApp>
      </XProvider>
    </ConfigProvider>
  );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>
);
