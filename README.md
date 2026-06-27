# Tomur

Tomur 是一个面向本地 AI 工作负载的运行时与开发者工作台，基于 .NET 10 与 C# 构建。它将模型服务、兼容 API、模型资产管理、运行时诊断和 Web 工作台整合在同一个本地进程中，适用于离线优先、隐私敏感和低运维成本的个人与团队开发环境。

## 🎯 项目目标

1. 以单一入口提供本地模型服务、运行时诊断、模型下载与工作台访问能力。
2. 提供稳定的 OpenAI / Ollama 兼容接口，便于现有客户端和工具接入。
3. 支持 CLI、本地 HTTP 服务以及系统服务形态，覆盖交互式使用和后台常驻运行。
4. 采用自包含、单文件和 Native AOT 友好的工程路线。
5. 由发行包携带必要的 native dynamic libraries，降低本地运行环境准备成本。
6. 将模型权重、用户数据、日志与生成结果作为可管理的本地资产处理。

## 🧩 目标能力

1. 本地文本生成。
2. 本地 embeddings 与 reranking。
3. OpenAI 兼容 HTTP API。
4. Ollama 兼容 HTTP API。
5. 模型目录、下载、校验与本地资产管理。
6. CPU、内存、磁盘、代理、端口、模型与 native libraries 运行时诊断。
7. llama.cpp、Whisper、PaddleOCR、stable-diffusion.cpp 与 llama.cpp TTS / GGUF TTS native runtime 支持。
8. 系统服务运行模式。
9. React + Ant Design X Web 工作台。

## 🚧 当前状态

当前实现已完成 R1 单项目 API 骨架、R2 配置与本地状态基础、R3 native library bundle 边界，并进入 R4/R5/R6 接线阶段。R4 已补齐首批协议端点、请求校验、OpenAI / Ollama 风格错误响应、streaming 错误帧和轻量本地模型文件发现；R5 已接入系统服务命令和双击启动代码路径，仍待 Windows、Linux 与 macOS 实机验证；R6 已接入内置模型 Catalog、硬件档位推荐、`tomur pull/list/ps`、断点续传、checksum 校验、proxy 支持和模型安装清单。

`Tomur.csproj` 承载 CLI 与本地 HTTP API，提供 `tomur`/`tomur open` 双击启动路径、`tomur serve`、`tomur service install/uninstall/start/stop/status`、`tomur doctor`、`tomur native prepare`、`tomur pull`、`tomur list`、`tomur ps`、`tomur api-key create/list`、`GET /health`、`GET /api/version`、`GET /api/runtime/status`、`GET /api/runtime/native`、`GET /api/models/catalog`、`GET /api/models/installed`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}`、`POST /api/runtime/native/{componentId}/{libraryName}/load`、`GET /v1/models`、`POST /v1/chat/completions`、`POST /v1/completions`、`POST /v1/embeddings`、`POST /v1/images/generations`、`GET /api/tags`、`POST /api/show`、`POST /api/generate` 和 `POST /api/chat` 的协议面。

模型推理尚未接入本地 runtime。未发现请求模型时，兼容 API 返回 `model_not_downloaded`；发现模型但 runtime 未接通时，返回 `runtime_not_configured`；请求过大时返回 `context_length_exceeded`。这些响应用于协议和诊断闭环；`tomur pull` 只准备模型资产，不代表 R7 文本推理或 R8 图像生成已经完成。

## 📁 本地状态

Tomur 使用稳定的数据目录保存配置、SQLite 数据库、模型资产、runtime 缓存和日志。

1. Windows 数据目录：`%LOCALAPPDATA%\Tomur`
2. Linux 数据目录：`~/.local/share/tomur`
3. macOS 数据目录：`~/Library/Application Support/Tomur`
4. 配置文件：`<data>/config/tomur.json`
5. SQLite 数据库：`<data>/tomur.db`
6. runtime 缓存：`<data>/runtime`
7. 模型目录：`<data>/models`
8. 日志目录：`<data>/logs`

可通过 `--data-dir <path>` 或 `TOMUR_DATA_DIR` 覆盖本地数据目录。配置文件损坏时，诊断流程会将损坏文件移到同目录的 `.damaged-<timestamp>` 文件，并写入默认配置。

## 🏗️ 架构概览

首个实现阶段采用单一 .NET 项目 `Tomur.csproj`。`Program.cs` 承担进程入口、顶层命令分发和全局帮助；`app/Cli/` 按命令类别承载 serve、service、doctor、native 和 api-key 的具体实现。同一个进程承载 CLI 命令、本地 HTTP API、服务启动、native runtime 管理与 Web 静态资源托管。

系统服务集成通过同一个 `tomur` 可执行文件完成：Windows 使用服务名 `Tomur`，Linux 使用 `tomur.service` systemd unit，macOS 使用 `dev.tomur.service` launchd user agent。无参数启动和 `tomur open` 会启动本地服务并打开浏览器工作台；原生托盘图标仍属于后续 Tomur 桌面外壳能力。macOS 的 launchd 与发布 RID 已接线，但本地 native runtime 仍需要后续补齐并验证 `osx-x64` / `osx-arm64` bundle 资产。

native backend 源码、CMake 工程和发布打包边界统一放在 `native/` 目录下；`app/Native/` 只保留 C# 动态库加载、P/Invoke 和托管适配代码。

Web 工作台采用 React、TypeScript、Vite、`antd` 与官方 `@ant-design/x` 技术栈。前端构建产物由 Tomur 本地 HTTP 服务托管。

## 📦 运行时资产

Tomur 发行产物应包含本地 AI backend 所需的 native dynamic libraries。当前 R3 能通过 manifest 将发布包中的 `native/runtimes/<rid>/native` 准备到 `<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native`，并通过 checksum、路径解析和显式 load smoke 报告或修复缺失、陈旧、损坏和无法加载的 native library。

模型权重与用户数据独立于可执行程序管理，以便单独下载、校验、升级或删除。R6 会把下载后的模型包登记到 `<data>/models/models.manifest.json`，并由 `/v1/models`、`/api/tags` 和 `/api/models/installed` 读取可见模型资产。

## 🗺️ 路线图

项目阶段计划见 [ROADMAP.md](./ROADMAP.md)。
