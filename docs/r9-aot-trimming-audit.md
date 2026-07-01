# R9 AOT / Trimming 审计记录

记录时间：2026-07-02

## 范围

R9 覆盖 Microsoft AI 抽象、Microsoft Agent Framework 文本编排、受控工具调用、本地文件检索、runtime 诊断/修复边界和 agent telemetry。当前审计目标是明确 AOT / trimming 风险点、可验证发布路径和后续限制，不把尚未验证的 Agent Framework Native AOT 能力写成承诺。

## 依赖与调用点

1. `Microsoft.Extensions.AI`：`app/Agents/LocalChatClient.cs`、`ToolFunctions.cs` 和 `ToolFactory.cs` 使用 `IChatClient`、`AIFunction` 与 `AITool`。
2. `Microsoft.Agents.AI`：`app/Agents/AgentRuntimeService.cs` 使用 `ChatClientAgent` 承载本地文本会话。
3. `Microsoft.Agents.AI.Workflows`：`AgentRuntimeService.RunWorkflowSummaryAsync` 使用 sequential workflow 托管只读工具摘要步骤。
4. `Microsoft.Data.Sqlite`：`app/Agents/FileIndexStore.cs` 使用 SQLite 表与 FTS5 虚拟表实现 `files.search`，不引入 PostgreSQL。
5. `OpenTelemetry.Extensions.Hosting` 与 `OpenTelemetry.Exporter.OpenTelemetryProtocol`：`app/Cli/ServeCommand.cs` 仅在 `TOMUR_AGENTS_OTEL_EXPORTER=otlp` 且 `TOMUR_AGENTS_OTEL_ENDPOINT` 存在时注册 OTLP exporter。

## JSON 与反射边界

R9 工具入参、出参、事件和 telemetry 状态均登记在 `AppJsonSerializerContext`：

1. `AgentChatRequest` / `AgentChatResponse`
2. `AgentToolInvokeRequest` / `AgentToolInvokeResponse`
3. `FileSearchToolArguments` / `FileSearchToolResult`
4. `AgentTelemetryStatus` / `AgentTelemetryExporterStatus`
5. `RuntimeDiagnoseToolResult` / `AgentToolExecutionResult`

`JsonSerializerIsReflectionEnabledByDefault=false` 保持开启。本轮新增工具没有依赖动态脚本或不透明插件。

## 发布边界

1. 已验证 `dotnet build app/Tomur.csproj`：0 warnings，0 errors。
2. R9 本地启动/API smoke 使用 `dotnet app/bin/Debug/net10.0/Tomur.dll serve --urls http://127.0.0.1:5149 --data-dir .tmp/r9-smoke-data`。
3. 已验证 `dotnet publish app/Tomur.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false`：0 warnings，0 errors。
4. 已尝试 `PublishAot=true`：当前阻塞点为 ASP.NET Core RequestDelegateGenerator 生成文件 `GeneratedRouteBuilderExtensions.g.cs` 的 `MapGet26(IEndpointRouteBuilder, string, Delegate)` 与 `EndpointRouteBuilderExtensions.MapGet(IEndpointRouteBuilder, string, RequestDelegate)` 签名不匹配，错误码 `CS9144`。
5. `ConfigurationStore` 中的反射式 `JsonSerializer.Serialize<T>(..., JsonSerializerOptions)` 已改为 source-generated `AppJsonSerializerContext.Default.LocalConfiguration`，Native AOT JSON warning 已清除。
6. 自包含单文件发布是 R12 之前的保底发布路径；Agent Framework、ASP.NET Core、OpenTelemetry 和 SQLite 相关 Native AOT 兼容性继续在 R12 矩阵中分项验证。
7. 如果 ASP.NET RequestDelegateGenerator、Agent Framework 或 OpenTelemetry 依赖暂时阻塞 Native AOT，Tomur 的承诺限定为核心 CLI、HTTP API、native loader、model catalog、download 和本地 runtime 的可审计 AOT 路径；Agent 编排保留在自包含单文件发布中。

## Smoke 结果

本轮 R9 smoke 结果：

1. `dotnet build app/Tomur.csproj`：0 warnings，0 errors。
2. self-contained single-file publish：0 warnings，0 errors。
3. `GET /health`：200。
4. `GET /v1/models`：200，兼容 API 不依赖 Agent Framework。
5. `GET /api/agents/tools`：10 个工具，`files.search` 为 ready，`runtime.repair` 要求确认。
6. `GET /api/agents/tool-bindings`：3 个 safe tools，9 个 declaration tools。
7. `POST /api/agents/tools/invoke` with `files.search`：200，命中 1 条本地测试文件片段。
8. `POST /api/agents/tools/invoke` with `runtime.repair` without `confirm=true`：409。
9. `POST /api/agents/tools/invoke` with `runtime.repair`, `confirm=true`, `action=session.unload`：200。
10. `POST /api/agents/workflows/read-only` with `respond=false`：200，执行 2 个只读步骤。
11. `GET /api/agents/telemetry`：`local_only`，exporter `disabled`。
