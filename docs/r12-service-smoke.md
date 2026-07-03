# R12 服务形态 Smoke 清单

记录时间：2026-07-03

本文记录 R12 发布产物的服务形态 smoke 路径。当前文件是发布验证清单，不表示所有平台 smoke 已执行；执行结果应在每个平台完成后补入“结果记录”。

## 共同前置条件

1. 使用发布产物执行，不使用 `dotnet run`。
2. 使用临时或隔离数据目录，避免污染用户真实数据：`--data-dir <smoke-data>`。
3. 设置稳定本地监听地址，例如 `--urls http://127.0.0.1:5149`。
4. 服务安装后确认 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指向 `<data>/bundle-cache`。
5. smoke 至少覆盖 CLI、HTTP API、Web 静态托管和 native bundle prepare。

## 最小 HTTP Smoke

服务启动后执行：

```powershell
Invoke-RestMethod http://127.0.0.1:5149/health
Invoke-RestMethod http://127.0.0.1:5149/api/version
Invoke-RestMethod http://127.0.0.1:5149/v1/models
Invoke-RestMethod http://127.0.0.1:5149/api/runtime/native
Invoke-RestMethod http://127.0.0.1:5149/api/runtime/multimodal
```

通过条件：

1. `/health` 返回 healthy。
2. `/api/version` 返回当前 Tomur version。
3. `/v1/models` 返回模型列表或空列表，不返回未处理异常。
4. `/api/runtime/native` 能报告 bundle id、version、RID、source runtime root 和 managed runtime root。
5. `/api/runtime/multimodal` 在 backend 缺失时返回结构化诊断，不影响服务进程。

## Windows Service

服务名固定为 `Tomur`。

安装与启动：

```powershell
.\tomur.exe service install --data-dir C:\tmp\tomur-r12-service --urls http://127.0.0.1:5149
.\tomur.exe service start
.\tomur.exe service status
```

补充检查：

```powershell
sc.exe qc Tomur
sc.exe query Tomur
```

卸载：

```powershell
.\tomur.exe service stop
.\tomur.exe service uninstall --data-dir C:\tmp\tomur-r12-service
```

结果记录：

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 安装 | not run | 待补充 |
| 启动 | not run | 待补充 |
| HTTP smoke | not run | 待补充 |
| native prepare | not run | 待补充 |
| 停止/卸载 | not run | 待补充 |

## Linux systemd

unit 名固定为 `tomur.service`。

用户级安装与启动：

```bash
./tomur service install --user --data-dir /tmp/tomur-r12-service --urls http://127.0.0.1:5149
./tomur service start --user
./tomur service status --user
```

系统级安装与启动：

```bash
sudo ./tomur service install --data-dir /var/lib/tomur-smoke --urls http://127.0.0.1:5149
sudo ./tomur service start
sudo ./tomur service status
```

补充检查：

```bash
systemctl --user cat tomur.service
systemctl --user status tomur.service --no-pager
```

卸载：

```bash
./tomur service stop --user
./tomur service uninstall --user --data-dir /tmp/tomur-r12-service
```

结果记录：

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 安装 | not run | 待补充 |
| 启动 | not run | 待补充 |
| HTTP smoke | not run | 待补充 |
| native prepare | not run | 待补充 |
| 停止/卸载 | not run | 待补充 |

## macOS launchd

launchd label 固定为 `dev.tomur.service`。

安装与启动：

```bash
./tomur service install --data-dir /tmp/tomur-r12-service --urls http://127.0.0.1:5149
./tomur service status
```

补充检查：

```bash
launchctl print gui/$(id -u)/dev.tomur.service
cat ~/Library/LaunchAgents/dev.tomur.service.plist
```

卸载：

```bash
./tomur service stop
./tomur service uninstall --data-dir /tmp/tomur-r12-service
```

结果记录：

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 安装 | not run | 待补充 |
| 启动 | not run | 待补充 |
| HTTP smoke | not run | 待补充 |
| native prepare | not run | 待补充 |
| 停止/卸载 | not run | 待补充 |

## 通过标准

1. 服务安装脚本由 Tomur C# CLI 生成，不依赖外部 PowerShell、Python 或 shell 脚本作为主流程。
2. 服务形态和 `tomur serve` 使用同一 host 逻辑。
3. 服务启动后 HTTP smoke 全部通过。
4. `tomur native prepare` 或 `POST /api/runtime/native/prepare` 能准备当前 RID 的 native runtime。
5. 日志、工作目录、数据目录和 bundle extract 目录可从服务配置中定位。
