import React from "react";
import ReactDOM from "react-dom/client";
import { App as AntApp, ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";
import { XProvider } from "@ant-design/x";
import App from "./App";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ConfigProvider locale={zhCN}>
      <XProvider>
        <AntApp>
          <App />
        </AntApp>
      </XProvider>
    </ConfigProvider>
  </React.StrictMode>
);
