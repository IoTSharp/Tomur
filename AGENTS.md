# AGENTS.md

本文件记录 Tomur 仓库内 AI 智能体与自动化工具必须遵守的长期约束。任何修改 Tomur 文档、代码、前端、API、运行时或发布流程的任务，都必须先读取本文件。

## 产品定位

- Tomur 是面向本地 AI 工作负载的运行时与开发者工作台，基于 .NET 10 与 C# 构建。
- Tomur 将模型服务、兼容 API、模型资产管理、运行时诊断和 Web 工作台整合在同一个本地进程中。
- Tomur 的默认场景是离线优先、隐私敏感、低运维成本的个人与团队开发环境。
- Tomur 不是行业方案平台、数字员工平台、Mission Control 平台、复杂工作流治理平台或多租户服务器产品。
- 文档和 UI 只描述 Tomur 自身，不引入外部产品背景、迁移叙事或旧平台定位。

## 文档口径

- README 必须像正式开源项目首页，保持克制、清晰、专业。
- 不要使用口语化、内部备忘录式或营销式表达，例如“核心程序名就是”“目标体验接近”“当前状态很早期”“先怎样再怎样”等。
- README 聚焦项目定位、项目目标、核心能力、架构概览、运行时资产和路线图入口。
- ROADMAP 是唯一长期阶段计划文件，文件名固定为 `ROADMAP.md`。
- CHANGELOG 记录已完成历史，文件名固定为 `CHANGELOG.md`；ROADMAP 不再展开已完成阶段的历史细节。
- README 和 ROADMAP 可以使用少量表情作为视觉导航；表情应服务信息层级，不得写成娱乐化或营销化文案。
- ROADMAP 必须使用数字顺序与状态标记表达阶段进度。
- 不再创建或引用 `MIGRATION_ROADMAP.md`。
- 不要在 Tomur 文档中出现 `Camel.NET`、`CamelNET`、迁移来源、旧服务器模块或旧平台背景。
- 除非代码已经存在并可运行，否则不得把路线图能力写成已实现。
- 文档默认使用中文，除非文件已有明确英文风格或用户指定英文。

## 工程边界

- `Tomur.csproj` 继续承载 CLI、本地 HTTP API、服务模式启动、runtime 管理和 Web 静态资源托管。
- 允许在 `providers/` 下建立独立 C# 类库项目，用于隔离纯托管模型提供器；提供器不得拆成独立产品、服务进程或另一套 HTTP API。
- 现有 llama.cpp、Whisper、OCR、stable-diffusion.cpp 与 TTS native runtime 路径必须保留；新增托管提供器是并行选择，不得以替换或删除现有 native 能力为前提。
- `Program.cs` 必须承担进程入口、顶层命令分发和全局帮助；CLI 具体实现按类别放在 `app/Cli/`。
- 除模型提供器及其必要的稳定契约外，不因功能分类继续拆分 C# 项目。
- 不直接依赖外部服务器项目，不引入 PostgreSQL、RBAC、SSO、复杂审计、多租户治理或后台管理壳作为默认能力。
- 默认本地状态使用文件系统与 SQLite-first 设计。
- 用户面对的是 Tomur 本地程序，不要求理解底层 native backend、模型目录实现或内部适配层。

## 命名约束

- 不把项目名作为普通文件名、类名、record 名、接口名或枚举名的前缀。
- 避免把产品名直接拼到 `Configuration`、`RuntimeStatus`、`ApiKeyStore` 等语义名之前；优先使用 `LocalConfiguration`、`RuntimeStatusResponse`、`ApiKeyStore` 这类上下文明确的名称。
- 项目文件、命名空间、产品名、服务名、公开命令和文档中必要的产品称呼不受此约束影响。
- 新增代码前应检查同目录既有命名风格，保持名称短、明确，并避免重复表达所在项目或文件夹上下文。
- 托管模型提供器按模型架构或能力命名，不使用参考实现、上游项目或第三方运行时的项目名作为程序集名、命名空间、类型名、provider ID、配置键或诊断代码。

## API 优先级

- 第一阶段先实现 API 级访问能力。
- 必须优先提供：
  - `GET /health`
  - `GET /api/version`
  - `GET /v1/models`
  - `POST /v1/chat/completions`
  - `POST /api/chat`
- OpenAI / Ollama 兼容 API 是核心能力。
- R13 协议能力聚合包含 Claude Code 所需的 Anthropic Messages 兼容入口：`GET /v1/models?limit=1000`、`POST /v1/messages` 与 `POST /v1/messages/count_tokens`。
- Claude Code / Anthropic Messages 兼容入口必须映射到 Tomur 本地模型与本地 runtime；未下载模型、runtime 不可用或上下文超限时返回对应协议风格的清晰诊断，不得伪造推理结果。
- 未接通本地 runtime 时，API 必须返回清晰的未配置或不可用诊断，不得伪造推理结果。
- Streaming、错误响应、模型未下载、runtime 不可用、上下文超限等协议行为必须明确设计。

## 模型 Catalog 与下载

- `tomur pull`、`tomur list` 和 `tomur ps` 属于 Tomur 单体公开命令。
- 模型下载必须写入 Tomur 数据目录下的模型目录，并记录安装清单；不得把模型权重打包进程序二进制。
- 下载必须支持断点续传、checksum 校验、proxy 配置、license 提示和硬件档位推荐。
- `GET /api/models/catalog` 与 `GET /api/models/installed` 是本地模型资产管理 API；`/v1/models` 与 `/api/tags` 只暴露本地可见模型资产。
- 校验失败或 bundle 必需资产缺失时，不得把模型标记为可用。
- R6 只代表模型资产准备与可见性管理，不代表 R7/R8 推理能力已经完成。

## 运行时与发布

- Tomur 目标是自包含、单文件、Native AOT 友好。
- Tomur 同时支持 native runtime 与纯 C# 托管模型提供器；模型格式、架构和 provider 匹配必须显式、可诊断，native 路径继续作为已验证的默认路径。
- `tomur` 发布产物应携带必要的 C++ native dynamic libraries，降低用户手工准备运行时的成本。
- 模型权重、SQLite 数据库、日志、用户文件和生成结果不硬编码进程序二进制，由 Tomur 管理在本地数据目录中。
- native libraries 可以作为 bundle 资产携带，并在首次运行或版本变化时释放到 Tomur 管理的 runtime 目录。
- 缺失或损坏的 native library 必须通过 `tomur doctor`、API 和 UI 返回可诊断错误。
- native 推理继续通过 C# 调用动态库实现；纯托管提供器可以使用 C# 实现模型加载、张量算子、缓存和推理核心，但不得在内部回退到未声明的 P/Invoke 或第三方 native dynamic library。
- 纯托管性能路径可以使用 `unsafe`、`Span<T>`、`MemoryMarshal`、`RandomAccess`、内存映射和 `System.Runtime.Intrinsics`，同时必须保留边界检查、模型元数据校验、资源上限和取消响应。
- 独立托管 provider DLL 只属于非 AOT 自包含发布面；Native AOT 发布若不支持动态托管程序集加载，必须静态引用兼容提供器或明确报告该 provider 不可用，不得伪装为已加载。
- AOT / trimming 警告必须逐项处理，不得用 blanket suppression 掩盖。

## Native 能力范围

- 默认 native runtime 方向包括：
  - llama.cpp
  - Whisper
  - OCR native
  - stable-diffusion.cpp
  - llama.cpp TTS / GGUF TTS
- 必须保持 ggml 相关 native 资产的隔离与可诊断加载。
- 不把 DeepSeek 本地 GGUF 写入默认 catalog 或默认配置。
- 不重新引入 Kokoro 作为默认 TTS 路线。

## 前端技术约束

- Tomur Web UI 采用 React + TypeScript + Vite。
- UI 组件体系直接使用官方 Ant Design X 技术栈：
  - `antd`
  - `@ant-design/x`
  - `@ant-design/x-markdown`
  - `@ant-design/x-sdk`
- 不使用 Vue、Blazor、Svelte 或自研 AI 对话基础组件作为 Tomur Web UI 默认路线。
- 不自研 Bubble、Sender、Conversations、Attachments、Prompts 等基础 AI 对话组件，除非官方组件无法满足本地运行约束。
- Web UI 始终由 Tomur 本地 HTTP 服务托管，不拆成独立产品。
- Web UI 默认采用 Chat-first 信息架构；首屏不把 Models、Downloads、Runtime、Files 作为一级导航。
- Models、Downloads、Runtime、Files 默认收敛在 Settings 分组、状态抽屉或 Chat 上下文诊断入口中。
- 默认入口应提供可直接使用的 Chat 工作台，不做营销 landing，也不做管理后台式首页。

## 服务运行

- Tomur 必须支持交互式 CLI、本地 HTTP 服务和系统服务形态。
- Windows 服务名保持为 `Tomur`。
- Linux unit 名保持为 `tomur.service`。
- macOS 使用 launchd user agent，label 保持为 `dev.tomur.service`。
- 服务模式与 `tomur serve` 使用同一套 host 逻辑。
- 服务安装、卸载、启动、停止和状态查询必须通过 C# CLI 实现，不新增 PowerShell、Python 或 shell 脚本作为主流程。
- 无参数启动与 `tomur open` 是 Tomur 单体的双击启动路径；原生托盘图标必须在 Tomur 自身外壳中实现，不接线外部旧桌面项目。

## 本地目录约定

- Windows 数据目录：`%LOCALAPPDATA%\Tomur`
- Linux 数据目录：`~/.local/share/tomur`
- macOS 数据目录：`~/Library/Application Support/Tomur`
- runtime 缓存：`<data>/runtime`
- 模型目录：`<data>/models`
- SQLite 数据库：`<data>/tomur.db`
- 日志目录：`<data>/logs`

## 验证规则

- 当前默认协作方式下，除非用户明确要求验证，不主动执行构建、测试或启动。
- Tomur 作为独立本地程序，可以规划本机 `dotnet build`、`dotnet test`、`dotnet run`、`dotnet publish` 验证路径；但执行前必须遵守用户是否要求验证的约束。
- 前端验证在用户明确要求时执行，优先使用项目内 package scripts。
- 不对外部父仓库的服务器项目执行本机 `dotnet build`、`dotnet test`、`dotnet run`、`dotnet ef`。

## 变更纪律

- 保持变更紧贴当前阶段，不提前引入复杂后台、团队治理、多机调度或企业部署能力。
- 修改 README、ROADMAP、AGENTS.md 时，必须保持三者口径一致。
- 新增路线图能力时，先写入 `ROADMAP.md` 的对应阶段，再实现代码。
- 删除或改名公开命令、API 路径、默认目录、服务名、unit 名前，必须同步更新 README、ROADMAP 和本文件。
