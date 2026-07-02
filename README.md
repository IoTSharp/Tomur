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

当前实现已完成 R1 单项目 API 骨架、R2 配置与本地状态基础、R3 native library bundle 边界、R4 OpenAI / Ollama 首批兼容协议面、R5 系统服务与 Windows 托盘代码路径、R6 模型 Catalog 与下载、R7 本地推理首通、R8 多模态能力闭环、R9 Microsoft AI 抽象与 Agent Framework 编排、R10 会话后端闭环和 R11 Chat-first Web 工作台闭环。R4 已补齐首批协议端点、请求校验、OpenAI / Ollama 风格错误响应、streaming 错误帧和轻量本地模型文件发现；R5 已接入系统服务命令、双击启动代码路径和 Windows 原生托盘图标，Windows/Linux/macOS 真机 smoke 后移到后期测试阶段；R6 已接入内置模型 Catalog、硬件档位推荐、`tomur pull/list/ps`、断点续传、checksum 校验、proxy 支持和模型安装清单；R7 已接入 llama.cpp 文本推理托管边界、首个进程内 session manager、chat/completion/embedding API 成功响应、基础 token usage、CUDA backend 探测与 GPU layer offload 参数；R8 已完成公开多模态接口真实模型 smoke，Whisper ASR、OuteTTS 语音合成、VLM、OCR 与 FLUX.2 klein 图像生成均留下通过证据，`/v1/images/generations` 通过 stable-diffusion.cpp image worker 返回真实 PNG；R9 已接入 Microsoft.Extensions.AI 与 Microsoft Agent Framework 的本地文本编排、只读工具上下文、SQLite 本地文件检索、显式 controlled R8 工具调用、确认式 runtime 修复动作和 opt-in OpenTelemetry exporter 边界；R10 已接入会话状态 API、SQLite 存储边界、文本回合编排、多模态附件入口、语音回合入口、会话产物读取和会话软删除，可记录会话、消息、附件引用、工具调用摘要、音频/图像/文件产物、诊断和文本/语音回合结果；R11 已接入基于 React + Ant Design X 的 Chat-first 工作台外壳、M1 Settings 信息架构、Runtime 操作区、Chat 上下文诊断入口、会话历史同步、附件回合、按钮式录音语音回合、TTS 产物播放和会话诊断展示路径，并通过 `app/wwwroot` 由 Tomur 本地 HTTP 服务托管。可视化下载队列、Settings 写入编辑、VAD/打断、流式语音回合和 R12 Native AOT / 自包含发布仍属后续工作。

`Tomur.csproj` 承载 CLI 与本地 HTTP API，提供 `tomur`/`tomur open` 双击启动路径、`tomur serve`、`tomur service install/uninstall/start/stop/status`、`tomur doctor`、`tomur native prepare`、`tomur native build`、`tomur pull`、`tomur list`、`tomur ps`、`tomur api-key create/list`、`GET /health`、`GET /api/version`、`GET /api/runtime/status`、`POST /api/runtime/session/unload`、`GET /api/runtime/native`、`GET /api/runtime/multimodal`、`GET /api/agents/runtime`、`GET /api/agents/tools`、`GET /api/agents/tool-bindings`、`GET /api/agents/events`、`GET /api/agents/telemetry`、`POST /api/agents/chat`、`POST /api/agents/workflows/read-only`、`POST /api/agents/tools/invoke`、`GET /api/conversations`、`POST /api/conversations`、`GET /api/conversations/{conversationId}`、`DELETE /api/conversations/{conversationId}`、`POST /api/conversations/{conversationId}/turns`、`POST /api/conversations/{conversationId}/voice-turns`、`POST /api/conversations/{conversationId}/messages`、`POST /api/conversations/{conversationId}/artifacts`、`GET /api/conversations/{conversationId}/artifacts/{artifactId}/content`、`POST /api/conversations/{conversationId}/diagnostics`、`GET /api/models/catalog`、`GET /api/models/installed`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}`、`POST /api/runtime/native/{componentId}/{libraryName}/load`、`GET /v1/models`、`POST /v1/chat/completions`、`POST /v1/completions`、`POST /v1/embeddings`、`POST /v1/images/generations`、`POST /v1/audio/transcriptions`、`POST /v1/audio/speech`、`POST /api/vision/analyze`、`POST /api/ocr/analyze`、`GET /api/tags`、`POST /api/show`、`POST /api/generate` 和 `POST /api/chat` 的协议面。

文本模型推理通过 Tomur 管理的 llama.cpp native runtime 按需加载一个本地 session，并可通过 `POST /api/runtime/session/unload` 显式卸载。未发现请求模型时，兼容 API 返回 `model_not_downloaded`；native runtime 缺失、模型损坏、能力不匹配或内存不足时返回可诊断的 runtime 错误；请求过大时返回 `context_length_exceeded`。OpenAI 文本接口的 streaming 成功路径会随本地生成回调输出文本增量；Tomur 会在 llama.cpp backend catalog 中探测 CUDA、NPU、Metal、Vulkan、SYCL、OpenVINO 等加速设备并优先请求 GPU/NPU offload，未发现可用 accelerator backend 时回退 CPU。多模型常驻仍属后续工作。

R8 多模态能力已完成当前 backend/API 范围内的闭环。`GET /api/runtime/multimodal` 会报告 Whisper ASR、llama.cpp GGUF TTS、PaddleOCR-VL、stable-diffusion.cpp 和 llama.cpp VLM 的 native component 与本地模型资产可见性；`/v1/audio/transcriptions` 已通过 Whisper large-v3 turbo q5_0 真实模型 smoke；`/v1/audio/speech` 已通过 OuteTTS GGUF bundle 与 WavTokenizer sidecar 返回真实 WAV；`/api/vision/analyze`、`/api/ocr/analyze` 以及包含 data URI / base64 图片的 `/v1/chat/completions` 已通过 SmolVLM 快速 smoke；`/v1/images/generations` 已通过 FLUX.2 klein 与 stable-diffusion.cpp worker 返回真实 PNG。Whisper、PaddleOCR-VL、stable-diffusion.cpp 与 llama.cpp GGUF TTS 已按 native manifest 支持 `cpu` / `cuda13` 变体目录；Tomur 会先通过 llama.cpp backend catalog 探测 CUDA 设备和显存，再决定是否解析 CUDA13 变体、设置 GPU layer offload、启用 `use_gpu` / flash attention，并在缺少 CUDA13 变体或 `ggml-cuda` 时回退 CPU。`/v1/images/generations` 的 OpenAI 请求会解析 `size`、`n`、`response_format`、`steps`、`cfg_scale`、`distilled_guidance`、`flow_shift`、`seed`、`sample_method` 与 `scheduler`；FLUX.2 klein 默认使用 `steps=4`、`cfg_scale=1.0` 和 `sample_method=euler`。native assert、超时或 worker 崩溃会回到主进程的结构化诊断；本轮完成记录包含 Windows CUDA13 native 构建、managed runtime prepare、完整 R8 公开接口 smoke 和 WAV/PNG 产物签名检查，证据见 [docs/r8-smoke-report.md](docs/r8-smoke-report.md)。

R9 已接入 Microsoft AI 抽象与 Agent Framework 编排。Tomur 本地文本 runtime 已暴露为 `Microsoft.Extensions.AI.IChatClient`，`POST /api/agents/chat` 通过 `Microsoft.Agents.AI.ChatClientAgent` 执行本地文本会话，默认 `tool_mode=none`；`tool_mode=read_only` 会按调用方提供的 `tools[]` 显式调用 `runtime.diagnose`、`tools.inspect` 与 `files.search`，`tool_mode=auto_read_only` 会由 Tomur 根据当前消息选择只读工具，并把结果作为同一轮上下文交给本地 agent；`files.search` 只检索 Tomur 管理的 `<data>/files` 文本文件，使用 SQLite 表与 FTS5 建立基础 RAG 片段，不引入 PostgreSQL。`tool_mode=controlled` 可按调用方显式 `tools[]` 调用 ready 的 R8 工具或 `runtime.repair`，`image.generate`、`audio.speak` 与 runtime 修复动作必须带 `confirm=true`，生成产物写入 `<data>/files/agents`；`tool_mode=auto_controlled` 在没有显式 `tools[]` 时仍只做 Tomur 侧只读规划。`POST /api/agents/workflows/read-only` 提供受控只读编排入口：先由 Tomur 执行只读工具计划，存在本地 chat 模型且未设置 `respond=false` 时，再通过 Microsoft Agent Framework sequential workflow 托管本地 `ChatClientAgent` 摘要结果。`GET /api/agents/runtime` 与 `GET /api/agents/tools` 提供 chat、image、vision、OCR、ASR、TTS、files 和 runtime diagnostics 的工具状态映射，并标记 route、schema、side effect、callable、requires confirmation 与 invocation modes；`GET /api/agents/tool-bindings` 会暴露只读 `AIFunction`、ready R8 controlled declaration 和受控/阻塞工具声明；`POST /api/agents/tools/invoke` 可调用只读工具，也可在 `mode=controlled` 时调用 ready 的 R8 工具或确认式 runtime 修复动作，边界阻塞返回 409，适配器诊断失败返回 503；`GET /api/agents/events` 可读取 `<data>/logs/agents.jsonl` 中最近的 agent 事件摘要，记录事件名、工具名（如适用）、状态、模式、耗时和阻塞状态，不记录用户消息正文或完整工具结果；`GET /api/agents/telemetry` 暴露当前 `Tomur.Agents` `ActivitySource` 的 span、属性和 exporter 状态，默认 `local_only`，仅在设置 `TOMUR_AGENTS_OTEL_EXPORTER=otlp` 与 `TOMUR_AGENTS_OTEL_ENDPOINT` 后接入 OTLP exporter；`/api/agents/chat`、`/api/agents/workflows/read-only` 与 `/api/agents/tools/invoke` 失败时会返回统一的 agent error JSON，便于后续 UI 状态抽屉和本地调试。R9 smoke 与 AOT / trimming 边界记录见 [docs/r9-aot-trimming-audit.md](docs/r9-aot-trimming-audit.md)。当前 R9 不默认开放模型自动选择多模态 tool-calling；多模态真实模型能力以 R8 报告为完成证据。

R10 已建立会话状态层、文本回合编排、多模态附件入口和语音回合入口。`GET /api/conversations`、`POST /api/conversations`、`GET /api/conversations/{conversationId}`、`DELETE /api/conversations/{conversationId}`、`POST /api/conversations/{conversationId}/messages`、`POST /api/conversations/{conversationId}/artifacts`、`GET /api/conversations/{conversationId}/artifacts/{artifactId}/content` 与 `POST /api/conversations/{conversationId}/diagnostics` 会把会话、消息、附件引用、工具调用摘要、生成产物和诊断记录写入本地 SQLite，并从 Tomur 数据目录读取会话产物内容。`POST /api/conversations/{conversationId}/turns` 会追加用户文本消息，处理 data URI / base64 图片、音频和本地文件附件，按明确输入选择 VLM、OCR、ASR、图像生成、文件检索或 TTS 受控工具，并把 assistant 回复、tool 摘要、产物引用和失败诊断写回同一会话；图像生成、TTS 和 runtime 修复仍需要 `confirm=true`。`POST /api/conversations/{conversationId}/voice-turns` 接受 multipart 音频上传或 JSON 内联音频，按 ASR -> 文本回合 -> 可选 TTS 的顺序运行，并把输入音频、转写文本、assistant 回复、TTS 音频产物和失败诊断写回同一会话。语音回合是否成功执行取决于本地 ASR / TTS 模型、native runtime 与对应 R8 适配器是否可用；当前不代表模型自主多模态 tool-calling、流式语音回合或 VAD/打断已完成。

R10/R11 回归维护清单见 [docs/r10-r11-smoke-maintenance.md](docs/r10-r11-smoke-maintenance.md)。R12 AOT / 自包含发布审计入口见 [docs/r12-aot-release-audit.md](docs/r12-aot-release-audit.md)。

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

Web 工作台采用 React + TypeScript，使用 Vite 作为构建工具，并结合 `antd` 与官方 `@ant-design/x` 技术栈。当前已接入 Chat-first 工作台外壳与 M1 Settings 信息架构：默认入口直接进入对话区，顶部提供模型选择器，右侧提供状态抽屉与 Settings 入口；模型、下载、运行时和文件相关管理能力收敛在 Settings 分组、状态条、状态抽屉或 Chat 上下文诊断入口中。Runtime 分组已接入 native prepare、session unload、组件状态、诊断提示和明确下一步；General、Models、Downloads、API、Files 与 Advanced 分组读取现有配置、模型、下载建议、API key、路径、SQLite、agent telemetry 和多模态状态，并提供可复制 CLI/API 动作。Chat 输入区已接入图片、音频和文本文件附件选择，带附件或请求朗读的回合会调用 `/api/conversations/{conversationId}/turns`，按钮式录音会在浏览器内转为 16 kHz mono PCM WAV 后调用 `/api/conversations/{conversationId}/voice-turns`；会话返回的诊断、产物摘要和 TTS 音频可在消息下方展示。工作台启动时会读取本地会话列表，历史详情按需加载，纯文本 streaming 回复成功后会补写到 `/api/conversations`，会话菜单通过软删除移除本地历史。前端构建产物输出到 `app/wwwroot`，由 Tomur 本地 HTTP 服务托管。

## 📦 运行时资产

Tomur 发行产物应包含本地 AI backend 所需的 native dynamic libraries。当前 R3 能通过 manifest 将发布包中的 `native/runtimes/<rid>/native` 准备到 `<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native`，并通过 checksum、路径解析和显式 load smoke 报告或修复缺失、陈旧、损坏和无法加载的 native library。Windows x64 native 构建入口为 `tomur native build --rid win-x64 --backend all`，可生成顶层 llama.cpp / ggml 共享 runtime 以及 Whisper、PaddleOCR-VL、stable-diffusion.cpp、llama.cpp GGUF TTS 的 `cpu` / `cuda13` 消费者变体；`--backend cpu` 或 `--backend cuda13` 可只构建单一变体。`ggml-cuda`、`ggml-cann`、`ggml-metal`、`ggml-vulkan`、`ggml-sycl`、`ggml-openvino` 与 `ggml-opencl` 作为可选 accelerator backend 管理；缺失时不影响 CPU 推理，存在且被 llama.cpp 枚举到设备时会进入优先 offload 策略。

模型权重与用户数据独立于可执行程序管理，以便单独下载、校验、升级或删除。R6 会把下载后的模型包登记到 `<data>/models/models.manifest.json`，并由 `/v1/models`、`/api/tags` 和 `/api/models/installed` 读取可见模型资产。

## 🗺️ 路线图

项目阶段计划见 [ROADMAP.md](./ROADMAP.md)。
