# R12 AOT / 自包含发布记录

记录时间：2026-07-03

本文是 R12 的发布记录入口，承接 `docs/r9-aot-trimming-audit.md` 中的依赖风险记录。R12 已从计划阶段推进到发布收敛阶段；当前项目 Native AOT 发布已确认可通过且无警告。

## 当前结论

1. `native-aot-audit` profile 已作为 Native AOT 发布入口，设置 `PublishAot=true`、`SelfContained=true`、`PublishSingleFile=true` 与 `SuppressTrimAnalysisWarnings=false`。
2. 当前项目 Native AOT 发布已确认 0 warnings、0 errors；R9 记录的 ASP.NET RequestDelegateGenerator 与 JSON 反射 warning 阻塞已由 R12 承接并清除。
3. `self-contained-single-file` profile 保留为非 AOT 自包含单文件发布路径，使用同一 `tomur` 入口和同一公开命令/API。
4. R12 后续不再以 AOT warning 清理为主线，重点转为 Linux/macOS 发布记录、native bundle 随包资产校验、服务形态 smoke 和发布包结构说明。
5. native bundle 随包清单见 `docs/r12-native-bundle-inventory.md`。
6. 服务形态 smoke 清单见 `docs/r12-service-smoke.md`。
7. 发布包结构说明见 `docs/r12-release-package-structure.md`。

## 当前发布边界

1. `Tomur.csproj` 保持唯一 .NET 项目，承载 CLI、本地 HTTP API、服务模式、native runtime 管理和 Web 静态资源托管。
2. RID 发布默认启用 self-contained single-file 路径：`SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`。
3. `IncludeAllContentForSelfExtract=false` 保持不变；模型权重、SQLite 数据库、日志、用户文件和生成结果不进入程序二进制。
4. native runtime 继续由 bundle manifest 准备到 Tomur 数据目录下的版本化 runtime 缓存。
5. `JsonSerializerIsReflectionEnabledByDefault=false` 已开启，API DTO、配置、catalog、会话、agent、多模态、native 和 runtime 响应继续登记在 `AppJsonSerializerContext`。

## R12 矩阵

| Area | Status | Current note |
| --- | --- | --- |
| CLI dispatch | supported | `Program.cs` 保持顶层命令分发，具体命令在 `app/Cli/` |
| HTTP API | supported | Minimal API 路由保留在同一进程内；R9 的 RequestDelegateGenerator 阻塞已清除 |
| Native loader | supported | P/Invoke、resolver、bundle probe/prepare 保持 source-generated JSON 边界 |
| Model catalog/download | supported | 下载仍使用文件系统与 manifest，不引入外部服务依赖 |
| Text runtime | supported | llama.cpp native session manager 作为核心本地推理路径 |
| Multimodal adapters | diagnosed | backend 缺失时返回结构化诊断；真实能力沿用 R8 smoke 证据 |
| Agent Framework | supported | Agent Framework chat、tools 与 read-only workflow 进入 Native AOT build surface，不再作为 fallback-only 能力记录 |
| OpenTelemetry | supported | exporter 仍 opt-in，默认 local-only |
| Web static hosting | supported | 构建产物由 Tomur 本地 HTTP 服务托管 |
| Native bundle manifest | documented | `win-x64` / `linux-x64` 随包文件与 checksum 已记录；macOS RID 仍需补齐 |

## 发布命令

以下命令只在用户明确要求验证时执行：

```powershell
dotnet build app/Tomur.csproj
dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=native-aot-audit
dotnet publish app/Tomur.csproj -c Release -r linux-x64 -p:PublishProfile=native-aot-audit
dotnet publish app/Tomur.csproj -c Release -r osx-x64 -p:PublishProfile=native-aot-audit
dotnet publish app/Tomur.csproj -c Release -r osx-arm64 -p:PublishProfile=native-aot-audit
dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=self-contained-single-file
```

前端静态资源同步只在明确要求前端验证或发布准备时执行：

```powershell
Push-Location web
npm run build
Pop-Location
```

## 记录入口

| 主题 | 文件 | 状态 |
| --- | --- | --- |
| Native bundle 随包清单和 checksum | `docs/r12-native-bundle-inventory.md` | 已记录当前 `win-x64` / `linux-x64`，`osx-x64` / `osx-arm64` 待补齐 |
| 服务形态 smoke | `docs/r12-service-smoke.md` | 已建立 Windows Service、Linux systemd、macOS launchd 清单，待实机执行 |
| 发布包结构说明 | `docs/r12-release-package-structure.md` | 已记录单文件边界、native runtime 随包方式和最小回归 |

## 仍需执行

1. Linux x64 Native AOT 发布日志和最小 smoke 记录。
2. macOS `osx-x64` / `osx-arm64` 自包含与 Native AOT 发布日志、native bundle prepare 和 smoke 记录。
3. Windows Service、Linux systemd、macOS launchd 与 Windows 托盘使用发布产物的实机 smoke。
4. 缺失/损坏 native 资产的 doctor / UI 修复记录。
5. 发布包最小回归执行记录，覆盖 CLI、HTTP API、Web 静态托管、native prepare 与模型可见性。

## 不计入 AOT 阻塞

1. 可视化下载队列。
2. Settings 写入编辑。
3. VAD、唤醒词、barge-in 打断和流式语音回合。
4. 模型自主多模态 tool-calling、checkpoint 与更完整文件附件 RAG。
