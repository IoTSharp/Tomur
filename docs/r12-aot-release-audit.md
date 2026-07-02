# R12 AOT / 自包含发布审计入口

记录时间：2026-07-02

本文是 R12 的审计入口，承接 `docs/r9-aot-trimming-audit.md` 中的依赖风险记录。R12 仍处于计划阶段；本文不把 Native AOT 发布写成已完成能力。

## 当前发布边界

1. `Tomur.csproj` 保持唯一 .NET 项目，承载 CLI、本地 HTTP API、服务模式、native runtime 管理和 Web 静态资源托管。
2. RID 发布默认保留 self-contained single-file 路径：`SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`。
3. 模型权重、SQLite 数据库、日志、用户文件和生成结果不进入程序二进制。
4. native runtime 继续由 bundle manifest 准备到 Tomur 数据目录下的 runtime 缓存。
5. `JsonSerializerIsReflectionEnabledByDefault=false` 已开启，API DTO、配置、catalog、会话、agent 和 runtime 响应继续登记在 `AppJsonSerializerContext`。

## R12 审计矩阵

| Area | AOT expectation | Current note |
| --- | --- | --- |
| CLI dispatch | Required | `Program.cs` 保持顶层命令分发，具体命令在 `app/Cli/` |
| HTTP API | Required | ASP.NET RequestDelegateGenerator 阻塞点见 R9 审计 |
| Native loader | Required | P/Invoke、resolver、bundle probe/prepare 需保持 source-generated JSON |
| Model catalog/download | Required | 下载仍使用文件系统与 manifest，不引入外部服务依赖 |
| Text runtime | Required | llama.cpp native session manager 作为核心本地推理路径 |
| Multimodal adapters | Supported or diagnosed | 每个 backend 缺失时必须返回结构化诊断 |
| Agent Framework | Fallback allowed | 若依赖阻塞 AOT，保留 self-contained fallback |
| OpenTelemetry | Fallback allowed | exporter 仍 opt-in，默认 local-only |
| Web static hosting | Required | 构建产物由 Tomur 本地 HTTP 服务托管 |

## 后续验证命令

以下命令只在用户明确要求验证时执行：

```powershell
dotnet build app/Tomur.csproj
dotnet publish app/Tomur.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false
dotnet publish app/Tomur.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=true
```

前端静态资源同步只在明确要求前端验证或发布准备时执行：

```powershell
Push-Location web
npm run build
Pop-Location
```

## 不计入当前完成口径

1. Native AOT 通过全矩阵发布。
2. macOS `osx-x64` / `osx-arm64` native bundle 实机 smoke。
3. Windows Service、Linux systemd、macOS launchd 和 Windows 托盘实机 smoke。
4. Agent Framework 与 OpenTelemetry 在 Native AOT 下的完整兼容承诺。

