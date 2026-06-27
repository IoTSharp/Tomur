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

当前实现已完成 R1 单项目 API 骨架、R2 配置与本地状态基础、R3 native library bundle 边界、R4 OpenAI / Ollama 首批兼容协议面、R5 系统服务与 Windows 托盘代码路径、R6 模型 Catalog 与下载、R7 本地推理首通，并进入 R8 接线阶段。R4 已补齐首批协议端点、请求校验、OpenAI / Ollama 风格错误响应、streaming 错误帧和轻量本地模型文件发现；R5 已接入系统服务命令、双击启动代码路径和 Windows 原生托盘图标，Windows/Linux/macOS 真机 smoke 后移到后期测试阶段；R6 已接入内置模型 Catalog、硬件档位推荐、`tomur pull/list/ps`、断点续传、checksum 校验、proxy 支持和模型安装清单，真实下载、代理环境和低内存实机建议 smoke 后移到后期测试阶段；R7 已接入 llama.cpp 文本推理托管边界、首个进程内 session manager、chat/completion/embedding API 成功响应和基础 token usage；R8-B 已开始接通 VLM 与 OCR 的托管执行适配器。

`Tomur.csproj` 承载 CLI 与本地 HTTP API，提供 `tomur`/`tomur open` 双击启动路径、`tomur serve`、`tomur service install/uninstall/start/stop/status`、`tomur doctor`、`tomur native prepare`、`tomur pull`、`tomur list`、`tomur ps`、`tomur api-key create/list`、`GET /health`、`GET /api/version`、`GET /api/runtime/status`、`POST /api/runtime/session/unload`、`GET /api/runtime/native`、`GET /api/runtime/multimodal`、`GET /api/models/catalog`、`GET /api/models/installed`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}`、`POST /api/runtime/native/{componentId}/{libraryName}/load`、`GET /v1/models`、`POST /v1/chat/completions`、`POST /v1/completions`、`POST /v1/embeddings`、`POST /v1/images/generations`、`POST /v1/audio/transcriptions`、`POST /v1/audio/speech`、`POST /api/vision/analyze`、`POST /api/ocr/analyze`、`GET /api/tags`、`POST /api/show`、`POST /api/generate` 和 `POST /api/chat` 的协议面。

文本模型推理通过 Tomur 管理的 llama.cpp native runtime 按需加载一个本地 session，并可通过 `POST /api/runtime/session/unload` 显式卸载。未发现请求模型时，兼容 API 返回 `model_not_downloaded`；native runtime 缺失、模型损坏、能力不匹配或内存不足时返回可诊断的 runtime 错误；请求过大时返回 `context_length_exceeded`。当前 streaming 成功路径先以兼容帧返回整段结果，逐 token streaming、GPU 策略和多模型常驻仍属后续工作。

多模态能力处于 R8 接线阶段。`GET /api/runtime/multimodal` 会报告 Whisper ASR、llama.cpp GGUF TTS、PaddleOCR-VL、stable-diffusion.cpp 和 llama.cpp VLM 的 native component 与本地模型资产可见性；`/api/vision/analyze`、`/api/ocr/analyze`、`/v1/images/generations` 以及包含 data URI / base64 图片的 `/v1/chat/completions` 会在对应 native library、主模型与 sidecar ready 时尝试本地执行，失败时返回可诊断 runtime 错误。`/v1/audio/transcriptions` 和 `/v1/audio/speech` 当前仍返回对应 backend 的专门诊断，不伪造音频结果；真实 ASR 与 TTS 执行适配器仍需后续逐项接通。

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

系统服务集成通过同一个 `tomur` 可执行文件完成：Windows 使用服务名 `Tomur`，Linux 使用 `tomur.service` systemd unit，macOS 使用 `dev.tomur.service` launchd user agent。无参数启动和 `tomur open` 会启动本地服务并打开浏览器工作台；Windows 下会创建原生托盘图标，可打开工作台、打开 runtime 状态或退出 Tomur，`--no-tray` 可关闭托盘。macOS 的 launchd 与发布 RID 已接线，但本地 native runtime 仍需要后续补齐并验证 `osx-x64` / `osx-arm64` bundle 资产。

native backend 源码、CMake 工程和发布打包边界统一放在 `native/` 目录下；`app/Native/` 只保留 C# 动态库加载、P/Invoke 和托管适配代码。

Web 工作台采用 React、TypeScript、Vite、`antd` 与官方 `@ant-design/x` 技术栈。前端构建产物由 Tomur 本地 HTTP 服务托管。

## 📦 运行时资产

Tomur 发行产物应包含本地 AI backend 所需的 native dynamic libraries。当前 R3 能通过 manifest 将发布包中的 `native/runtimes/<rid>/native` 准备到 `<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native`，并通过 checksum、路径解析和显式 load smoke 报告或修复缺失、陈旧、损坏和无法加载的 native library。

模型权重与用户数据独立于可执行程序管理，以便单独下载、校验、升级或删除。R6 会把下载后的模型包登记到 `<data>/models/models.manifest.json`，并由 `/v1/models`、`/api/tags` 和 `/api/models/installed` 读取可见模型资产。

## 🗺️ 路线图

项目阶段计划见 [ROADMAP.md](./ROADMAP.md)。
