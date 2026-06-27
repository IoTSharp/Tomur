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

当前实现已完成 R1 单项目 API 骨架与 R2 配置、数据目录、SQLite、API key 和诊断基础。R3 已完成 native source bundle 迁移、manifest、版本化 runtime extraction、checksum probe、损坏修复、resolver、loader、Windows/Linux wrapper 构建验证和 load smoke 边界。

`Tomur.csproj` 承载 CLI 与本地 HTTP API，提供 `tomur --help`、`tomur serve`、`tomur doctor`、`tomur native prepare`、`tomur api-key create/list`、`GET /health`、`GET /api/version`、`GET /api/runtime/status`、`GET /api/runtime/native`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}`、`POST /api/runtime/native/{componentId}/{libraryName}/load`、`GET /v1/models`、`POST /v1/chat/completions` 和 `POST /api/chat` 的基础协议面。

聊天端点尚未接入本地 runtime，会返回明确的 `runtime_not_configured` 诊断，不会伪造推理结果。

## 📁 本地状态

Tomur 使用稳定的数据目录保存配置、SQLite 数据库、模型资产、runtime 缓存和日志。

1. Windows 数据目录：`%LOCALAPPDATA%\Tomur`
2. Linux 数据目录：`~/.local/share/tomur`
3. 配置文件：`<data>/config/tomur.json`
4. SQLite 数据库：`<data>/tomur.db`
5. runtime 缓存：`<data>/runtime`
6. 模型目录：`<data>/models`
7. 日志目录：`<data>/logs`

可通过 `--data-dir <path>` 或 `TOMUR_DATA_DIR` 覆盖本地数据目录。配置文件损坏时，诊断流程会将损坏文件移到同目录的 `.damaged-<timestamp>` 文件，并写入默认配置。

## 🏗️ 架构概览

首个实现阶段采用单一 .NET 项目 `Tomur.csproj`。同一个进程承载 CLI 命令、本地 HTTP API、服务启动、native runtime 管理与 Web 静态资源托管。

native backend 源码、CMake 工程和发布打包边界统一放在 `native/` 目录下；`app/Native/` 只保留 C# 动态库加载、P/Invoke 和托管适配代码。

Web 工作台采用 React、TypeScript、Vite、`antd` 与官方 `@ant-design/x` 技术栈。前端构建产物由 Tomur 本地 HTTP 服务托管。

## 📦 运行时资产

Tomur 发行产物应包含本地 AI backend 所需的 native dynamic libraries。当前 R3 能通过 manifest 将发布包中的 `native/runtimes/<rid>/native` 准备到 `<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native`，并通过 checksum、路径解析和显式 load smoke 报告或修复缺失、陈旧、损坏和无法加载的 native library。

模型权重与用户数据独立于可执行程序管理，以便单独下载、校验、升级或删除。

## 🗺️ 路线图

项目阶段计划见 [ROADMAP.md](./ROADMAP.md)。
