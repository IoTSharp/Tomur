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
7. llama.cpp、Whisper、OCR、stable-diffusion.cpp 与 Qwen TTS native runtime 支持。
8. 系统服务运行模式。
9. React + Ant Design X Web 工作台。

## 🚧 当前状态

当前实现处于 R1 单项目 API 骨架阶段。`Tomur.csproj` 承载 CLI 与本地 HTTP API，提供 `tomur --help`、`tomur serve`、`tomur doctor`、`GET /health`、`GET /api/version`、`GET /v1/models`、`POST /v1/chat/completions` 和 `POST /api/chat` 的基础协议面。

聊天端点尚未接入本地 runtime，会返回明确的 `runtime_not_configured` 诊断，不会伪造推理结果。

## 🏗️ 架构概览

首个实现阶段采用单一 .NET 项目 `Tomur.csproj`。同一个进程承载 CLI 命令、本地 HTTP API、服务启动、native runtime 管理与 Web 静态资源托管。

native backend 源码、CMake 工程和发布打包边界统一放在 `native/` 目录下；`app/Native/` 只保留 C# 动态库加载、P/Invoke 和托管适配代码。

Web 工作台采用 React、TypeScript、Vite、`antd` 与官方 `@ant-design/x` 技术栈。前端构建产物由 Tomur 本地 HTTP 服务托管。

## 📦 运行时资产

Tomur 发行产物应包含本地 AI backend 所需的 native dynamic libraries。运行时由 Tomur 准备、校验和维护受管理的 runtime 目录，并通过诊断能力报告缺失或损坏的运行时资产。

模型权重与用户数据独立于可执行程序管理，以便单独下载、校验、升级或删除。

## 🗺️ 路线图

项目阶段计划见 [ROADMAP.md](./ROADMAP.md)。
