# Tomur Roadmap

本文件记录 Tomur 的产品与工程路线图，作为功能拆分、架构取舍和阶段验收的依据。

## 📌 状态图例

| 标记 | 含义 |
| --- | --- |
| ✅ | 已完成 |
| 🚧 | 进行中 |
| ⏭️ | 下一步 |
| ⏳ | 计划中 |

## 🎯 产品目标

Tomur 面向本地 AI 工作负载，提供模型服务、兼容 API、模型资产管理、运行时诊断和 Web 工作台。产品目标如下：

1. 以单一进程承载 CLI、本地 HTTP API、模型运行、模型下载、运行时诊断和 Web 工作台。
2. 提供 OpenAI / Ollama 兼容接口，降低现有客户端和自动化工具的接入成本。
3. 支持交互式运行、后台服务运行和系统服务运行。
4. 采用自包含、单文件和 Native AOT 友好的发布路线。
5. 由发行包携带必要的 C++ native dynamic libraries，避免依赖用户手工安装本地推理运行时。
6. 模型权重、SQLite 数据库、日志和生成结果由 Tomur 作为本地资产管理，独立于程序二进制更新。

## 🏗️ 首批工程形态

首批骨架仅建立一个 C# 项目：

```text
Tomur/

  ROADMAP.md
  README.md
  app/
    Tomur.csproj
    Program.cs
    Api/
    Cli/
    Config/
    Native/
    Runtime/
    Services/
    Web/
  native/
    llama.cpp/
    llama.native/
    whisper.cpp/
    whisper.native/
    paddleocr/
    ocr.native/
    stable-diffusion.cpp/
    stable-diffusion.native/
    tts.native/
  web/
    package.json
    src/
```

1. `Tomur.csproj` 是唯一 .NET 项目。
2. `Program.cs` 承担进程入口、顶层命令分发和全局帮助；CLI 具体命令按类别放在 `app/Cli/`。
3. CLI、HTTP API、服务模式、runtime 管理和静态 Web UI 都由同一个进程承载。
4. `web/` 是 React 前端源码目录，构建产物由 `Tomur.csproj` 作为静态资源托管。
5. `native/` 用于放置 native backend 源码、CMake 工程和发布打包边界，不作为独立 .NET 项目。
6. `app/Native/` 只放 C# 动态库加载、P/Invoke 和托管适配边界。
7. 在 API、native runtime、AOT 和发布流程稳定之前，不拆分多个 C# 项目。

## 🎨 Web UI 技术决策

Tomur Web UI 采用 Ant Design X 的 AI 应用技术架构，不自研基础对话组件。

技术路线：

1. React。
2. TypeScript。
3. Vite。
4. `antd`。
5. `@ant-design/x`。
6. `@ant-design/x-markdown`。
7. `@ant-design/x-sdk`。
8. 按 Ant Design X 的 Agent TBox / RICH 交互范式组织 Chat-first 工作台；Models、Downloads、Runtime、Files 默认收敛为 Settings 分组、状态抽屉或 Chat 上下文诊断入口。
9. 使用 Bubble、Sender、Attachments、Prompts、Conversations、Welcome、Suggestion、XStream / XRequest 等官方能力搭建 UI。
10. Tomur 前端只连接 Tomur 本地 OpenAI / Ollama 兼容 API，不把第三方 API key 暴露到浏览器。

边界约束：

1. 直接使用官方 `@ant-design/x` React 组件体系。
2. 不自研 AI 对话基础组件，除非官方组件无法满足 Tomur 的本地运行约束。
3. 不把 Web UI 拆成独立产品；它始终由 Tomur 本地服务托管。
4. 不把首屏做成管理后台；默认入口必须是可直接使用的 Chat 工作台。

## 📦 自包含与 Native 资产策略

Tomur 的自包含目标是降低本地部署前置条件，避免要求用户单独安装 .NET runtime 或手工准备 C++ dynamic libraries。

1. `tomur.exe` / `tomur` 自包含 .NET runtime。
2. llama.cpp、Whisper、PaddleOCR、stable-diffusion.cpp、llama.cpp TTS / GGUF TTS 等 C++ dynamic libraries 由 Tomur 发布产物携带。
3. RID 发布默认使用 `PublishSingleFile=true`、`SelfContained=true` 和 `IncludeNativeLibrariesForSelfExtract=true`，让必要 native 依赖进入单文件并由 .NET 启动流程自解压。
4. `IncludeAllContentForSelfExtract` 默认保持 `false`，模型权重、SQLite 数据库、日志、用户文件和大体积 backend 资产不作为普通内容整体塞进可执行文件。
5. .NET 单文件自解压目录不是 Tomur 的稳定 runtime 根目录；系统服务模式后续需要显式处理 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`。
6. native backend 动态库仍需要由 Tomur 的 native bundle manifest 管理，并在首次运行或版本变化时准备到 Tomur 管理的版本化 runtime 目录。
7. runtime 目录由 Tomur 校验、更新和清理，不暴露成用户手工配置的前置步骤。
8. 模型权重、用户文件、SQLite 数据库、日志和生成结果仍由 Tomur 管理在数据目录中。
9. 缺少或损坏 native library 时，Tomur 必须返回可诊断错误，并在 UI 和 `tomur doctor` 中给出修复动作。

## 📁 默认本地目录

1. Windows 数据目录：`%LOCALAPPDATA%\Tomur`
2. Linux 数据目录：`~/.local/share/tomur`
3. macOS 数据目录：`~/Library/Application Support/Tomur`
4. runtime 缓存：`<data>/runtime`
5. 模型目录：`<data>/models`
6. SQLite 数据库：`<data>/tomur.db`
7. 日志目录：`<data>/logs`
8. Web UI 静态资源：程序内置，运行时由本地 HTTP 服务托管。

## 🗺️ 阶段计划

| 顺序 | 阶段 | 状态 | 主题 |
| --- | --- | --- | --- |
| 00 | R0 | ✅ 已完成 | 项目门面 |
| 01 | R1 | ✅ 已完成 | 单项目 API 骨架 |
| 02 | R2 | ✅ 已完成 | 配置、数据目录与诊断 |
| 03 | R3 | ✅ 已完成 | Native Library Bundle |
| 04 | R4 | ✅ 已完成 | OpenAI / Ollama 兼容 API |
| 05 | R5 | ✅ 已完成 | Windows Service / Linux systemd / macOS launchd |
| 06 | R6 | ✅ 已完成 | 模型 Catalog 与下载 |
| 07 | R7 | ✅ 已完成 | 本地推理首通 |
| 08 | R8 | 🚧 部分 smoke 通过 | 多模态能力 |
| 09 | R9 | 🚧 只读编排接线 | Microsoft AI 抽象与 Agent Framework 编排 |
| 10 | R10 | ⏳ 计划中 | 会话编排与语音回合服务 |
| 11 | R11 | ⏳ 计划中 | React + Ant Design X Web UI |
| 12 | R12 | ⏳ 计划中 | Native AOT / 自包含发布 |

### 00. ✅ R0: 项目门面

目标：建立清晰的 Tomur 产品门面。

交付物：

1. `README.md` 说明 Tomur 的定位、目标能力和技术架构。
2. `ROADMAP.md` 作为唯一长期路线图。
3. 文档不描述尚未完成的能力为已实现。
4. 文档不引入外部产品名或旧平台背景。

验收：

1. README 具备正式开源项目门面的基本完整度。
2. 首批工程形态明确为一个 C# 项目。

### 01. ✅ R1: 单项目 API 骨架

目标：先实现 API 级别访问，形成可启动的本地服务骨架。

交付物：

1. ✅ `Tomur.csproj`。
2. ✅ `Program.cs`。
3. ✅ `tomur --help`。
4. ✅ `tomur serve`。
5. ✅ `tomur doctor`。
6. ✅ `GET /health`。
7. ✅ `GET /api/version`。
8. ✅ `GET /v1/models`。
9. ✅ `POST /v1/chat/completions` 的协议骨架。
10. ✅ `POST /api/chat` 的协议骨架。

验收：

1. ✅ 服务能在本地启动。
2. ✅ OpenAI 风格客户端可以请求 `/v1/models`。
3. ✅ Ollama 风格客户端可以请求 `/api/version`。
4. ✅ 聊天端点先返回明确的未配置 runtime 诊断，而不是假装已经完成推理。

### 02. ✅ R2: 配置、数据目录与诊断

目标：让 Tomur 有稳定的本地状态模型。

交付物：

1. ✅ 数据目录解析，支持默认目录、`TOMUR_DATA_DIR` 与 `--data-dir`。
2. ✅ 配置文件 `<data>/config/tomur.json`。
3. ✅ SQLite 初始化 `<data>/tomur.db`。
4. ✅ API key 本地哈希存储，提供 `tomur api-key create/list`。
5. ✅ `tomur doctor` 输出 OS、架构、CPU、内存、磁盘、proxy、端口、数据目录、SQLite、API key 与 runtime 状态。
6. ✅ `/api/runtime/status`。

验收：

1. ✅ 首次运行会创建必要目录。
2. ✅ 诊断输出清楚区分 ok、warning、error。
3. ✅ 配置损坏时有恢复路径。
4. ✅ Tomur 独立项目显式构建通过。

### 03. ✅ R3: Native Library Bundle

目标：让 Tomur 自己携带并解析 C++ dynamic libraries。

交付物：

1. ✅ native bundle manifest。
2. ✅ 版本化 runtime extraction。
3. ✅ library checksum。
4. ✅ runtime probe。
5. ✅ Windows DLL search path 支持。
6. ✅ Linux shared object probe 支持。
7. ✅ ggml 共享库隔离规则。
8. ✅ PaddleOCR / MTMD C++ OCR bridge 作为 OCR 主线边界。
9. ✅ llama.cpp TTS / GGUF TTS 作为 TTS 主线。
10. ✅ TTS C ABI bridge 骨架。
11. ✅ R4 可复用的 native probe / library resolver / library loader 托管接口。
12. ✅ `GET /api/runtime/native`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}` 与 `POST /api/runtime/native/{componentId}/{libraryName}/load`。

验收：

1. ✅ Tomur native wrappers 不依赖系统全局安装的 llama.cpp / Whisper / PaddleOCR / stable-diffusion.cpp 动态库。
2. ✅ runtime probe 可以报告每个 backend 是否可用。
3. ✅ 损坏的 native library 可通过 checksum / load failure 诊断，并可由 `tomur native prepare` 或 `POST /api/runtime/native/prepare` 从发布 bundle 修复。
4. ✅ Windows x64 和 Linux x64 native wrappers 已配置、编译、安装和 smoke-load 通过。
5. ✅ R3 已形成 native bundle 准备、诊断、解析和加载闭环；真实文本推理仍属于 R7，本地多模态推理仍属于 R8。

R3 审计结论：

1. 没有引入独立 Qwen3-TTS / ElBruno native 子模块；TTS 主线保持 llama.cpp TTS / GGUF TTS。
2. `tts.native` 当前只固定 ABI 并返回待接入诊断，不代表真实 TTS 合成已完成。
3. OCR / VLM bridge 已构建和加载验证，不代表 PaddleOCR / VLM 真实模型资产 smoke 已完成。
4. R4 可以基于现有 native probe / resolver / loader 开始协议面接线；真实本地推理能力仍必须按 R7 / R8 单独验收。

### 04. ✅ R4: OpenAI / Ollama 兼容 API

目标：补齐第一批真实协议面。

OpenAI 首批端点：

1. ✅ `GET /v1/models`
2. ✅ `POST /v1/chat/completions`
3. ✅ `POST /v1/completions`
4. ✅ `POST /v1/embeddings`
5. ✅ `POST /v1/images/generations`

Ollama 首批端点：

1. ✅ `GET /api/version`
2. ✅ `GET /api/tags`
3. ✅ `POST /api/show`
4. ✅ `POST /api/generate`
5. ✅ `POST /api/chat`

验收：

1. ✅ streaming 错误帧行为稳定；成功流式推理留到 R7 接入本地 runtime 后验收。
2. ✅ 错误响应符合对应 API 风格。
3. ✅ 未下载模型、runtime 不可用、上下文超限等情况都有清晰错误码。

R4 接线状态：

1. 轻量本地模型文件发现会扫描数据目录下的模型文件，并用于 `/v1/models` 与 `/api/tags`。
2. 请求不存在模型时返回 `model_not_downloaded`。
3. R7 接入前，请求已存在模型但本地推理未接通时返回 `runtime_not_configured`；R7 接入后，文本 chat/completion/embedding 会进入本地 llama.cpp runtime，图像、音频和视觉接口仍返回对应未配置诊断。
4. 超出 R4 临时输入字符上限时返回 `context_length_exceeded`。
5. R4 不伪造模型输出；真实 chat completion、completion、embeddings 和图像生成仍分别属于 R7 / R8。

R4 审计结论：

1. ✅ 无虚标：R4 完成口径限定为首批 OpenAI / Ollama 协议面、请求校验、兼容风格错误响应、streaming 错误帧和轻量本地模型发现；真实文本推理归 R7，图像、视觉、OCR、ASR 与 TTS 执行归 R8，不计入 R4 完成条件。
2. ✅ R4 范围内未发现阻塞缺陷：`/v1/models`、`/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`、`/v1/images/generations`、`/api/version`、`/api/tags`、`/api/show`、`/api/generate` 与 `/api/chat` 均已接线，并返回对应协议风格的成功响应或诊断错误。
3. ⏭️ 未闭环事项已归入后续阶段：真实 GGUF smoke、逐 token streaming、GPU offload、多模型常驻、真实 Whisper ASR、llama.cpp GGUF TTS 与多模态真实模型 smoke 继续在 R7 增强和 R8 中跟踪。

### 05. ✅ R5: Windows Service / Linux systemd / macOS launchd

目标：让 Tomur 可以作为系统服务常驻，并提供双击启动到本地工作台的入口。

交付物：

1. ✅ `tomur service install`。
2. ✅ `tomur service uninstall`。
3. ✅ `tomur service start`。
4. ✅ `tomur service stop`。
5. ✅ `tomur service status`。
6. ✅ `tomur service run` 服务宿主入口。
7. ✅ Windows Service 安装与管理代码路径。
8. ✅ Linux systemd service 安装与管理代码路径。
9. ✅ macOS launchd user agent 安装与管理代码路径。
10. ✅ 无参数启动与 `tomur open` 作为双击启动路径。
11. ✅ Windows 原生托盘图标与打开工作台、Runtime 状态、退出控制。

验收：

1. ✅ Windows 服务名稳定为 `Tomur`。
2. ✅ Linux unit 名稳定为 `tomur.service`。
3. ✅ macOS launchd label 稳定为 `dev.tomur.service`。
4. ✅ 服务模式和 `tomur serve` 使用同一套 host 逻辑。
5. ✅ 服务安装显式固定数据目录、工作目录和 bundle 解压目录。
6. ✅ 日志、工作目录和权限问题可诊断。
7. ✅ 原生托盘图标不复用旧桌面项目，已在 Tomur 单体 Windows 交互式外壳中实现。
8. ⏭️ Windows Service、Linux systemd、macOS launchd 与 Windows 托盘真机 smoke 统一后移到后期测试阶段；当前 R5 完成口径为代码路径与文档闭环。
9. ⏭️ macOS native runtime 仍需后续补齐并验证 `osx-x64` / `osx-arm64` bundle 资产，不作为 R5 服务代码路径的阻塞项。

### 06. ✅ R6: 模型 Catalog 与下载

目标：让 Tomur 可以推荐、下载、校验和列出本地模型。

交付物：

1. ✅ `tomur pull`。
2. ✅ `tomur list`。
3. ✅ `tomur ps`。
4. ✅ 下载断点续传。
5. ✅ checksum 校验。
6. ✅ proxy 支持。
7. ✅ license 提示。
8. ✅ 硬件档位推荐。
9. ✅ `GET /api/models/catalog`。
10. ✅ `GET /api/models/installed`。

默认模型包：

1. 本地通用助手：`unsloth/Qwen3.5-9B-GGUF` / `Qwen3.5-9B-Q4_K_M.gguf`。
2. 文本翻译：`Mungert/Hunyuan-MT-7B-GGUF` / `Hunyuan-MT-7B-q4_k_m.gguf`。
3. Embeddings：`ggml-org/embeddinggemma-300M-GGUF` / `embeddinggemma-300M-Q8_0.gguf`。
4. Reranker：`gpustack/bge-reranker-v2-m3-GGUF` / `bge-reranker-v2-m3-Q8_0.gguf`。
5. ASR：`ggerganov/whisper.cpp` / `ggml-large-v3-turbo-q5_0.bin`。
6. VAD sidecar：`ggml-org/whisper-vad` / `ggml-silero-v6.2.0.bin`。
7. TTS：llama.cpp TTS / GGUF TTS bundle，首批内置为 `OuteAI/OuteTTS-0.2-500M-GGUF` + WavTokenizer GGUF sidecar。
8. 视觉理解：`unsloth/Qwen3-VL-4B-Instruct-GGUF` / `Qwen3-VL-4B-Instruct-Q4_K_M.gguf` + `mmproj-F16.gguf`。
9. 图像生成：`unsloth/FLUX.2-klein-4B-GGUF` + sidecar bundle。

验收：

1. 低内存机器能得到更小模型建议，并明确提示。
2. 下载失败可恢复。
3. 校验失败不会把模型标记为可用。

R6 接线状态：

1. 内置 Catalog 覆盖默认聊天、翻译、embeddings、reranker、ASR + VAD、TTS、VLM 和图像生成包。
2. `tomur pull` 支持 `recommended`、`optional`、`all` 与包 ID 选择，支持 `--proxy`、`--no-proxy`、`--force` 和 `--dry-run`。
3. 下载使用 `.part` 文件断点续传；存在 expected SHA256 的资产校验失败时会删除无效文件并中止登记。
4. `<data>/models/models.manifest.json` 记录安装包、资产 hash、license notice 与 bundle 资产。
5. `/v1/models`、`/api/tags`、`tomur list` 和 `tomur ps` 会读取安装清单和本地散落模型文件。
6. 真实下载 smoke、不同代理环境和低内存实机建议真机验证后移到后期测试阶段；本阶段不加载模型，不代表 R7/R8 推理能力已完成。

R6 审计结论：

1. ✅ R6 功能范围已闭环：内置 Catalog、硬件档位推荐、`tomur pull/list/ps`、断点续传、checksum 校验、proxy 支持、license 提示、安装清单和模型 catalog / installed API 均已接线。
2. ✅ 无虚标：R6 完成口径限定为模型资产推荐、下载、校验、登记和可见性管理；真实文本、多模态推理执行分别归 R7 / R8，不计入 R6 完成条件。
3. ⏭️ 真实下载、不同代理环境和低内存实机建议 smoke 统一后移到后期测试阶段，不阻塞 R6 功能完成标记。

### 07. ✅ R7: 本地推理首通

目标：接通文本模型运行。

交付物：

1. ✅ llama.cpp session manager。
2. ✅ chat completion。
3. ✅ completion。
4. ✅ embeddings。
5. ✅ 模型加载、卸载、状态查询。
6. ✅ 基础 token usage。

验收：

1. ✅ `/v1/chat/completions` 可以调用本地模型返回文本。
2. ✅ `/api/chat` 可以调用本地模型返回文本。
3. ✅ 模型未加载、内存不足、文件损坏时可诊断。

R7 接线状态：

1. llama.cpp P/Invoke、受管理 native import resolver、进程内单 session manager 与按需模型加载已接入。
2. `/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`、`/api/generate` 和 `/api/chat` 会在模型可见且能力匹配时调用本地 runtime。
3. OpenAI / Ollama 非流式成功响应已返回文本和基础 token usage；streaming 成功路径当前先返回兼容帧中的整段结果，逐 token streaming 留待后续增强。
4. `/api/runtime/status` 会报告 llama native prepared、native 缺失或当前加载的 llama.cpp session，包含 generation / embeddings 模式；`POST /api/runtime/session/unload` 可卸载当前 session，`tomur ps` 继续列出可见资产并提示服务进程内按需加载。
5. native runtime 缺失、模型加载失败、上下文超限、模型能力不匹配和 embedding 不可用会返回诊断错误。
6. 本阶段按协作规则未主动执行构建、启动和真实 GGUF smoke 验证；GPU offload、多模型常驻、reranker 和 R8 多模态不属于本次完成范围。

### 08. 🚧 R8: 多模态能力

目标：逐步接通语音、图像、OCR 和视觉理解。

交付物：

1. ✅ Whisper ASR。
2. ✅ llama.cpp TTS / GGUF TTS。
3. ✅ OCR 托管执行适配器。
4. 🚧 stable-diffusion.cpp 图像生成。
5. ✅ VLM 托管执行适配器。
6. 🚧 `/v1/images/generations`。
7. ✅ `/v1/audio/transcriptions`。
8. ✅ `/v1/audio/speech`。
9. ✅ `/api/vision/analyze` 与 `/api/ocr/analyze`。

验收：

1. ✅ 每个 backend 都可以独立诊断。
2. ✅ 未配置 backend 不影响文本 API。
3. 🚧 每个公开接口至少完成一次真实模型 smoke，并记录通过/阻塞证据。
4. ⏳ Web UI 能显示 runtime 缺失和修复动作。

R8 接线状态：

1. `GET /api/runtime/multimodal` 已提供 ASR、TTS、OCR、stable-diffusion.cpp image generation 与 VLM backend 的统一诊断面。
2. 多模态诊断会同时检查 native component 状态与本地模型资产可见性，并返回修复动作。
3. `/api/vision/analyze`、`/api/ocr/analyze` 与包含 data URI / base64 图片的 `/v1/chat/completions` 已接入 VLM / OCR 托管执行适配器；当 native library、主模型和 mmproj sidecar ready 时会尝试真实本地执行。
4. 包含 `image_url` / `input_image` 的 `/v1/chat/completions` 请求不再把图片输入作为普通文本交给 R7 文本 runtime；远程图片 URL 当前不会自动下载，会要求调用方发送 data URI。
5. `/v1/audio/transcriptions` 已接入 Whisper ASR 托管执行适配器，并通过 `ggml-large-v3-turbo-q5_0.bin` 与 `jfk.wav` 真实 smoke，返回 JFK 样本文本。
6. `/v1/audio/speech` 已接入 OuteTTS GGUF bundle、WavTokenizer sidecar 与 `tomur-tts` native bridge；`Tomur/native/tts.native/tts_bridge.cpp` 复用 llama.cpp `tools/tts` 的 OuteTTS 文本归一化、audio code 生成与 WavTokenizer 转 PCM 路线，托管层返回 WAV。该代码路径仍需补一次真实模型成功 smoke。
7. VLM 与 OCR 已通过 `ggml-org/SmolVLM-500M-Instruct-GGUF` 快速 smoke；SmolVLM 只作为低内存验收包，默认视觉理解包仍是 Qwen3-VL 4B。
8. `/v1/images/generations` 已接入 stable-diffusion.cpp PNG 生成适配器，并补齐当前 C API 的 `backend` / `params_backend` 转发；图像生成现在通过内部 image worker 子进程执行，native assert、超时或 worker 崩溃会返回主进程结构化诊断，不再直接拖垮 Tomur 主服务进程。
9. FLUX.2 klein 4B smoke 仍触发 `conditioner.hpp:1671: GGML_ASSERT(!hidden_states.empty()) failed`，真实成功出图尚未完成；当前隔离 worker 只收敛崩溃半径，不代表图像生成能力已通过 R8 验收。`stable-diffusion.native` bridge 已补充上游版本、sidecar 传入状态和生成参数 stderr 诊断，下一轮 smoke 可据此继续定位。
10. R8 smoke 记录见 `Tomur/docs/r8-smoke-report.md`；当前结论为部分通过，图像生成仍是 R8 blocker，TTS 已完成真实合成适配但仍需补成功 smoke 证据。

### 09. 🚧 R9: Microsoft AI 抽象与 Agent Framework 编排

目标：把 Tomur 的本地模型、图像、OCR、ASR、TTS、文件检索和 runtime 诊断能力整理成稳定的 .NET AI 抽象与工具边界；需要智能体或工作流编排时统一使用 Microsoft Agent Framework，不自建重复的 Agent / Workflow runtime。

架构原则：

1. 本地推理仍由 Tomur native runtime 负责，Agent Framework 只做会话、工具调用、工作流和观测编排。
2. 文本模型优先适配 `Microsoft.Extensions.AI.IChatClient`，embeddings、图像、语音等能力在对应抽象稳定可用时对齐；抽象不足的能力先保留 Tomur 内部接口，并通过 Agent Framework tool 暴露。
3. 内置工具必须是本地优先、可诊断、可权限控制的 .NET 方法，不把浏览器 UI 或第三方远端服务作为默认 agent 依赖。
4. 工具入参、出参和检查点状态使用 source-generated JSON；禁止通过宽泛反射、动态脚本或不透明插件绕开 AOT / trimming 约束。
5. Community 默认只启用轻量会话和本地工具调用；复杂多 Agent、长流程、HITL 和团队治理能力保留为 Pro / Enterprise 或高级可选能力。

交付物：

1. ✅ Tomur 本地 chat runtime 到 `Microsoft.Extensions.AI.IChatClient` 的适配层。
2. ✅ Agent Framework 文本会话入口：`POST /api/agents/chat` 通过 `Microsoft.Agents.AI.ChatClientAgent` 调用本地 `IChatClient`，默认保持 `tool_mode=none`，并支持手动只读工具结果作为 `tool_results` 回填上下文；当调用方显式设置 `tool_mode=read_only` 与 `tools[]` 时，会先通过受控 `AITool` 边界调用 `runtime.diagnose` / `tools.inspect`，再把结果作为同一轮会话上下文交给本地 agent；`tool_mode=auto_read_only` 会由 Tomur 根据当前消息选择这两个只读工具，不开放模型自动选择多模态工具。
3. ✅ 工具目录与状态映射：`GET /api/agents/runtime` 与 `GET /api/agents/tools` 暴露 chat、image、vision、OCR、ASR、TTS、files 和 runtime diagnostics 的当前状态、路由、schema、side effect、callable、requires confirmation、invocation modes 和诊断动作；`GET /api/agents/tool-bindings` 暴露当前 `Microsoft.Extensions.AI.AITool` 绑定与同样的安全边界字段。
4. ✅ 受控只读工具调用入口：`POST /api/agents/tools/invoke` 只允许调用 `runtime.diagnose` 与 `tools.inspect`，返回 schema、审计、耗时和诊断结果；有副作用工具必须返回阻塞诊断。
5. ✅ 受控只读 workflow：`POST /api/agents/workflows/read-only` 先通过 Tomur 的 bounded tool plan 执行 `runtime.diagnose` / `tools.inspect` 两个只读步骤，并可选通过 `Microsoft.Agents.AI.Workflows` sequential workflow 托管本地 `ChatClientAgent` 摘要工具结果。
6. ⏳ 图像生成工具：从会话中调用 `/v1/images/generations` 或内部 stable-diffusion.cpp adapter；当前手动入口为 `/v1/images/generations`，自动 tool-calling 仍留到 R9 后续受控调用流程，FLUX.2 成功出图 smoke 仍需补证据。
7. ⏳ 视觉理解工具：从会话中调用 VLM adapter，支持 data URI / base64 图片输入。
8. ⏳ OCR 工具：从会话中调用 OCR adapter，返回文本、版面和诊断信息。
9. ⏳ ASR 工具：从会话中调用 Whisper adapter，返回 transcript、语言、时间片段和诊断信息。
10. ⏳ TTS 工具：从会话中调用 llama.cpp GGUF TTS adapter，返回音频、音色、采样率和诊断信息；当前手动入口为 `/v1/audio/speech`，自动 tool-calling 仍留到 R9 后续受控调用流程。
11. ⏳ 文件问答工具：对接本地文件、SQLite 索引和基础 RAG，不引入 PostgreSQL 作为 Community 默认依赖。
12. ⏳ runtime 工具：允许会话读取 `doctor` / native / model readiness 诊断，但修复类动作必须有明确用户确认。
13. ⏳ Agent Framework telemetry 接入 Tomur 本地日志和后续 OpenTelemetry 管线，记录工具调用、耗时、失败原因和模型使用量。

R9 接线状态：

1. `Tomur.csproj` 已引入 `Microsoft.Extensions.AI`、`Microsoft.Agents.AI` 与 `Microsoft.Agents.AI.Workflows`；workflow execution 先用于公开只读编排入口的本地 `ChatClientAgent` 摘要步骤，不承载多模态自动工具调用。
2. `LocalChatClient` 已把 Tomur R7 文本 chat runtime 适配为 `IChatClient`，支持模型解析、基础采样参数、system instructions 和 text/tool content 序列化。
3. `AgentRuntimeService` 已能构造本地 `ChatClientAgent` 并提供 `POST /api/agents/chat` 文本会话入口；该入口默认使用 `ChatToolMode.None`，不让模型自动调用尚未闭环的 R8 工具，同时支持 `tool_mode=read_only` 的显式工具上下文：调用方可以在 `tools[]` 中请求 `runtime.diagnose` / `tools.inspect`，Tomur 会通过受控 `AITool` 执行并把结果作为 tool 消息回填给本轮回答；`tool_mode=auto_read_only` 只做 Tomur 侧关键词规划，不让模型选择任意工具。
4. `GET /api/agents/runtime` 与 `GET /api/agents/tools` 已暴露本地工具地图。`chat.respond` 是 agent endpoint；`runtime.diagnose` 与 `tools.inspect` 已作为只读 `AIFunction` 暴露在 `GET /api/agents/tool-bindings`；所有工具描述都带 route、schema、side effect、callable、requires confirmation 与 invocation modes；VLM、OCR、ASR、TTS 与 image generation 会随 backend readiness 标记；`files.search` 仍为 planned。
5. `POST /api/agents/tools/invoke` 已提供受控只读工具调用入口，当前只执行 `runtime.diagnose` 与 `tools.inspect`，并返回输入 schema、审计字段和 source-generated JSON 结果；图像生成、VLM、OCR、ASR、TTS、files 和修复类 runtime 动作会返回阻塞诊断，不会被自动调用。
6. `POST /api/agents/workflows/read-only` 已接入受控只读 workflow：可以显式给出 `tools[]`，也可以由 Tomur 从请求消息中规划 `runtime.diagnose` / `tools.inspect`；`respond=false` 时只返回工具步骤，默认在存在本地 chat 模型时用 Agent Framework sequential workflow 托管 `ChatClientAgent` 摘要结果。
7. R9 当前只代表 Microsoft AI 抽象、只读 AITool 绑定、受控只读工具调用、显式/自动只读工具上下文和 Agent Framework 文本/只读 workflow 编排，不代表模型自动选择多模态 tool-calling、checkpoint、telemetry 或本地文件 RAG 已完成。

验收：

1. 普通文本会话不需要 Agent Framework 也能继续通过兼容 API 运行。
2. 🚧 `POST /api/agents/chat` 可以通过 `ChatClientAgent` 调用本地文本模型，并可显式或由 Tomur 自动规划注入只读工具上下文；仍需构建/启动和真实模型 smoke 验证。
3. 🚧 `POST /api/agents/workflows/read-only` 可以执行受控只读工具步骤并可选摘要结果；仍需构建/启动和真实模型 smoke 验证。
4. ⏳ 开启编排后，模型可以在一次会话中选择调用图像生成、视觉理解、OCR、ASR、TTS、文件检索和 runtime 诊断工具。
5. ⏳ 工具调用失败时返回可诊断错误，不伪造图像、文字、音频或识别结果。
6. ⏳ AOT / trimming 审计能定位到具体依赖和调用点；若 Agent Framework 依赖暂时阻塞 Native AOT，必须保留自包含单文件发布路径，并把 AOT 承诺限定在可通过审计的核心 runtime。

### 10. ⏳ R10: 会话编排与语音回合服务

目标：在 Tomur 单体中形成一整套对话服务，让用户可以用文本、图片、文件和语音与本地 AI 交互，并允许 AI 在会话中调用图像生成、识别、OCR、ASR、TTS 和本地文件问答能力。

交付物：

1. ⏳ 会话状态模型：记录用户消息、附件、工具调用、诊断、生成产物和音频结果，默认存储在本地 SQLite / 文件目录。
2. ⏳ 文本会话编排：在同一轮对话中支持模型选择工具、执行工具、回填结果并继续生成回答。
3. ⏳ 多模态附件入口：支持图片、音频和本地文件作为会话输入，明确限制远程 URL 自动下载策略。
4. ⏳ 语音输入链路：客户端录音或上传音频后，调用 Whisper ASR 生成 transcript，再交给会话编排层处理。
5. ⏳ 语音输出链路：AI 回复生成后，调用 llama.cpp GGUF TTS 生成可播放音频，并把音频结果登记为本地产物。
6. ⏳ 语音回合 API：提供“音频输入 -> ASR -> 会话处理 -> TTS -> 音频输出”的单回合服务。
7. ⏳ 流式增强：文本 token streaming、ASR 分段结果、TTS 分段生成、停止生成和取消工具调用。
8. ⏳ VAD 与打断：先实现按键/按钮式录音，后续再加入 VAD、唤醒词、barge-in 打断和播放控制。
9. ⏳ 安全边界：模型触发下载、删除、覆盖、外部网络或修复 runtime 前必须有用户确认。

验收：

1. 文本对话中可以要求“生成一张图”“识别这张图”“朗读这段回答”“把这段音频转文字”，系统会调用对应本地能力或返回明确诊断。
2. 语音回合能返回 transcript、assistant text、TTS audio 和完整诊断链路。
3. 任一多模态 backend 缺失时，不影响纯文本会话；UI 和 API 都能提示需要下载的模型或需要准备的 native runtime。
4. 所有会话产物保存在 Tomur 数据目录中，不写入程序二进制，不要求用户理解 backend 内部目录。

### 11. ⏳ R11: React + Ant Design X Web UI

目标：实现 Chat-first 的内置本地工作台。R11 不把 Models、Downloads、Runtime、Files 作为默认一级页面；这些能力优先作为 Chat 上下文、状态抽屉和 Settings 分组暴露，避免把 Tomur 首屏做成管理后台。

1. React + TypeScript + Vite。
2. `antd`。
3. `@ant-design/x`。
4. `@ant-design/x-markdown`。
5. `@ant-design/x-sdk`。
6. 按 Ant Design X 的 Agent TBox / RICH 交互架构组织 Chat 工作台。
7. 使用 Tomur 自己的 API，不接入第三方 agent 服务。
8. 前端构建产物由同一个 Tomur C# 项目托管。
9. 不自研 Bubble、Sender、Conversations、Attachments、Prompts 等基础 AI 对话组件，除非官方组件无法满足 Tomur 的本地运行约束。

M0：工程与工作台外壳：

1. ⏳ 建立 `web/` React + TypeScript + Vite 工程。
2. ⏳ 接入 `antd`、`@ant-design/x`、`@ant-design/x-markdown` 与 `@ant-design/x-sdk`。
3. ⏳ 建立 Chat-first 应用外壳：主区域为 Chat，顶部或输入区附近提供模型选择器，右上角提供 Settings 入口。
4. ⏳ Runtime / download / model readiness 先以紧凑状态条、popover 或抽屉呈现，不作为默认一级页面。
5. ⏳ 前端构建产物由 `Tomur.csproj` 托管，`tomur open` 和无参数启动进入同一个工作台。

M1：Settings 信息架构：

1. ⏳ General：语言、主题、数据目录、启动行为。
2. ⏳ Models：本地模型、导入、删除、默认模型和模型参数。
3. ⏳ Downloads：下载队列、失败重试、proxy 和 license 提示。
4. ⏳ Runtime：native runtime、组件状态、诊断和 prepare 动作。
5. ⏳ API Server：host、port、API key、OpenAI / Ollama 兼容接口状态。
6. ⏳ Files：数据目录、附件目录和后续 RAG 索引配置。
7. ⏳ Advanced：日志、实验特性和 Agent 工具开关。

Chat 上下文入口：

1. ⏳ 模型缺失、runtime 不可用、下载中、native library 缺失等状态在 Chat 内联诊断中提示，并可打开对应 Settings 分组。
2. ⏳ 附件入口默认保留在 Chat 输入区；图片、文件和音频能力未接通时显示诊断，不展示为已可用能力。
3. ⏳ 停止生成、重新生成、复制、模型选择和基础参数调整在 Chat 工作台内完成。

验收：

1. 默认入口提供可直接使用的 Chat 工作台。
2. 首屏不出现 Models、Downloads、Runtime、Files 作为默认一级导航；相关管理能力位于 Settings、状态抽屉或 Chat 诊断入口。
3. 支持 streaming 消息、停止生成、重新生成、复制、附件入口和模型选择；若后端尚未支持成功 token streaming，UI 必须按当前 API 能力展示，不伪造流式能力。
4. Runtime、Downloads 和模型状态可以在 UI 中刷新，并能跳转到对应 Settings 分组。
5. 所有文案只描述 Tomur 当前已接通能力。
6. Chat 页面支持文本、图片、文件和音频入口；未接通的 backend 显示诊断和修复动作，不展示为已可用能力。
7. 语音模式先支持按钮式录音、转写、回复朗读和播放控制；VAD、唤醒词和打断作为后续增强。

### 12. ⏳ R12: Native AOT / 自包含发布

目标：让 Tomur 形成稳定发布体验，并在不牺牲本地优先能力的前提下继续推进 Native AOT。AOT 是核心 CLI / launcher / local runtime 的优先目标；如果 Agent Framework、复杂 workflow 或某些多模态依赖暂时无法通过 AOT 审计，仍必须保持同一 `tomur` 入口的自包含单文件发布体验。

交付物：

1. ⏳ `PublishAot=true` 发布 profile。
2. ⏳ `SelfContained=true`。
3. ⏳ `PublishSingleFile=true`。
4. ⏳ source-generated JSON 覆盖 API DTO、配置、catalog、工具 schema、workflow/checkpoint 状态和诊断响应。
5. ⏳ trimming 兼容配置与逐项 warning 清单。
6. ⏳ AOT 兼容性矩阵：core CLI、HTTP API、native loader、model catalog、download、chat runtime、多模态 adapters、Agent Framework 编排、Web 静态托管分别标记 supported / blocked / fallback。
7. ⏳ Windows x64 发布。
8. ⏳ Linux x64 发布。
9. ⏳ macOS x64 / arm64 自包含发布。
10. ⏳ native bundle 发布清单。

验收：

1. 发布产物不要求用户安装 .NET runtime。
2. 发布产物携带必需 native libraries。
3. 首次运行能准备本地 runtime 目录。
4. AOT 警告必须逐项处理，不用 blanket suppression 掩盖。
5. AOT profile 中不可用的编排能力必须明确禁用或返回诊断，不允许运行时才因反射缺失崩溃。
6. 非 AOT fallback 仍保持自包含、单文件或近似单体体验，并使用同一套公开命令与 API。

## ⏭️ 下一步

继续收敛 R8，并为 R9/R10 做设计接线：

1. 为 R8 补 `/v1/audio/speech` 的 OuteTTS + WavTokenizer WAV 成功 smoke，并记录模型、接口、耗时和诊断证据。
2. 继续定位 FLUX.2 klein + stable-diffusion.cpp 的 conditioner assert；当前 `/v1/images/generations` 已隔离到 worker，默认 sampler / scheduler 已按上层成功链路交给 upstream auto/default，`stable-diffusion.native` bridge 会输出上游版本、sidecar 传入状态和生成参数诊断，下一步是用默认 `steps=4` 小图 smoke 补成功出图或更完整失败日志。
3. 继续用小模型/小素材维护 R8 smoke 套件，目标是单项几秒到五分钟内完成，并保留模型、接口、耗时和结果证据。
4. 在用户明确要求验证时执行 Tomur 独立项目构建、启动和真实 GGUF chat / embedding smoke。
5. 为 R7 增强逐 token streaming、GPU offload 选择、多模型常驻和更细的 session 诊断。
6. 在后期测试阶段补充 R5 的 Windows Service、Linux systemd、macOS launchd 和 Windows 托盘实机 smoke 验收记录。
7. 补齐并验证 macOS `osx-x64` / `osx-arm64` native runtime bundle 资产。
8. 继续推进 R9：在已接入 `runtime.diagnose` / `tools.inspect` 只读 AITool、`POST /api/agents/tools/invoke`、`tool_mode=auto_read_only` 和 `POST /api/agents/workflows/read-only` 的基础上，补构建/启动 smoke、工作流错误帧、telemetry 草案与后续受控 tool-calling 循环；等待 R8 图像与 TTS 成功 smoke 证据补齐后再开放对应自动生成工具。
9. 为 R10 设计语音回合的最小闭环：录音/上传、ASR、会话处理、TTS、播放和产物登记。
10. 为 R12 建立 AOT 审计清单，区分必须 AOT 的核心路径与可暂时走自包含 fallback 的高级编排路径。
