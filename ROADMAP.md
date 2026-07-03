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
| 08 | R8 | ✅ 已完成 | 多模态能力 |
| 09 | R9 | ✅ 已完成 | Microsoft AI 抽象与 Agent Framework 编排 |
| 10 | R10 | ✅ 后端能力闭环 | 会话编排与语音回合服务 |
| 11 | R11 | ✅ 已完成 | React + Ant Design X Web UI |
| 12 | R12 | 🚧 进行中 | Native AOT 已清警告，发布矩阵收敛 |

### 00. ✅ R0: 项目门面

目标：建立清晰的 Tomur 产品门面。

交付物：

1. `README.md` 说明 Tomur 的定位、目标能力和技术架构。
2. `ROADMAP.md` 作为唯一长期路线图。
3. 文档不描述尚未完成的能力为已实现。
4. 文档不引入外部产品名或非 Tomur 背景。

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
13. ✅ Windows x64 native 构建入口：`tomur native build --rid win-x64 --backend all|cpu|cuda13`。
14. ✅ native manifest 支持 `cpu` / `cuda13` 变体目录和优先级选择。

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
3. ⏭️ 未闭环事项已归入后续阶段：真实 GGUF smoke、多模型常驻、真实 Whisper ASR、llama.cpp GGUF TTS 与多模态真实模型 smoke 继续在 R7 增强和 R8 中跟踪；GPU / NPU offload 策略已接入，真实加速能力取决于对应 ggml accelerator backend 动态库是否进入 runtime bundle。

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
3. OpenAI / Ollama 非流式成功响应已返回文本和基础 token usage；OpenAI 文本 streaming 成功路径会随本地生成回调输出文本增量，Ollama streaming 仍先返回兼容帧中的整段结果。
4. `/api/runtime/status` 会报告 llama native prepared、native 缺失或当前加载的 llama.cpp session，包含 generation / embeddings 模式；`POST /api/runtime/session/unload` 可卸载当前 session，`tomur ps` 继续列出可见资产并提示服务进程内按需加载。
5. native runtime 缺失、模型加载失败、上下文超限、模型能力不匹配和 embedding 不可用会返回诊断错误。
6. llama.cpp backend catalog 设备探测、CUDA / NPU / Metal / Vulkan / SYCL / OpenVINO 等 accelerator backend 可见性诊断、GPU/NPU 优先 offload 策略和 CPU fallback 已接入；文本模型加载会按探测结果设置 `n_gpu_layers`、selected device、KQV offload 与 op offload。实际加速需要 `ggml-cuda`、`ggml-cann` 等对应动态库存在并成功被 native runtime 枚举。
7. 本阶段按协作规则未主动执行启动和真实 GGUF smoke 验证；多模型常驻、reranker 和 R8 多模态不属于本次完成范围。

### 08. ✅ R8: 多模态能力

目标：逐步接通语音、图像、OCR 和视觉理解。

交付物：

1. ✅ Whisper ASR。
2. ✅ llama.cpp TTS / GGUF TTS。
3. ✅ OCR 托管执行适配器。
4. ✅ stable-diffusion.cpp 图像生成。
5. ✅ VLM 托管执行适配器。
6. ✅ `/v1/images/generations`。
7. ✅ `/v1/audio/transcriptions`。
8. ✅ `/v1/audio/speech`。
9. ✅ `/api/vision/analyze` 与 `/api/ocr/analyze`。

验收：

1. ✅ 每个 backend 都可以独立诊断。
2. ✅ 未配置 backend 不影响文本 API。
3. ✅ 每个公开接口至少完成一次真实模型 smoke，并记录通过/阻塞证据。
4. ✅ Web UI 能显示 runtime 缺失和修复动作。

R8 接线状态：

1. `GET /api/runtime/multimodal` 已提供 ASR、TTS、OCR、stable-diffusion.cpp image generation 与 VLM backend 的统一诊断面。
2. 多模态诊断会同时检查 native component 状态与本地模型资产可见性，并返回修复动作。
3. `/api/vision/analyze`、`/api/ocr/analyze` 与包含 data URI / base64 图片的 `/v1/chat/completions` 已接入 VLM / OCR 托管执行适配器；当 native library、主模型和 mmproj sidecar ready 时会尝试真实本地执行。
4. 包含 `image_url` / `input_image` 的 `/v1/chat/completions` 请求不再把图片输入作为普通文本交给 R7 文本 runtime；远程图片 URL 当前不会自动下载，会要求调用方发送 data URI。
5. `/v1/audio/transcriptions` 已接入 Whisper ASR 托管执行适配器，并通过 `ggml-large-v3-turbo-q5_0.bin` 与 `jfk.wav` 真实 smoke，返回 JFK 样本文本。
6. `/v1/audio/speech` 已接入 OuteTTS GGUF bundle、WavTokenizer sidecar 与 `native/tts.native/tts_bridge.cpp` 中的 `tomur-tts` native bridge；该路径复用 llama.cpp `tools/tts` 的 OuteTTS 文本归一化、audio code 生成与 WavTokenizer 转 PCM 路线，托管层返回 WAV，并已通过真实模型 smoke。
7. VLM 与 OCR 已通过 `ggml-org/SmolVLM-500M-Instruct-GGUF` 快速 smoke；SmolVLM 只作为低内存验收包，默认视觉理解包仍是 Qwen3-VL 4B。
8. `/v1/images/generations` 已接入 stable-diffusion.cpp PNG 生成适配器，并补齐当前 C API 的 `backend` / `params_backend` 转发；图像生成现在通过内部 image worker 子进程执行，native assert、超时或 worker 崩溃会返回主进程结构化诊断，不再直接拖垮 Tomur 主服务进程。
9. FLUX.2 klein 4B 的历史 blocker 为 stable-diffusion.cpp 与共享 ggml runtime 的 tensor name capacity 不一致；`llama.native` 顶层共享 ggml / llama runtime 已统一以 `GGML_MAX_NAME=128` 构建，随后重建 stable-diffusion CUDA13 变体并完成 managed runtime prepare，`/v1/images/generations` 已通过真实小图 smoke。
10. Whisper、PaddleOCR-VL、stable-diffusion.cpp 与 llama.cpp GGUF TTS 已在 manifest 中接入 `cpu` / `cuda13` native 变体；托管执行路径会根据 llama.cpp backend catalog 的 CUDA 探测结果请求 CUDA13 变体、设置 GPU layer offload / `use_gpu` / flash attention，并在缺少 CUDA13 变体或 `ggml-cuda` 时回退 CPU。
11. R8 smoke 记录见 `docs/r8-smoke-report.md`；2026-07-02 证据覆盖 `GET /api/runtime/multimodal`、`POST /v1/images/generations`、`POST /v1/audio/transcriptions`、`POST /v1/audio/speech`、`POST /v1/chat/completions` 图片输入、`POST /api/vision/analyze` 与 `POST /api/ocr/analyze`，并保留 WAV/PNG 产物签名证据。

### 09. ✅ R9: Microsoft AI 抽象与 Agent Framework 编排

目标：把 Tomur 的本地模型、图像、OCR、ASR、TTS、文件检索和 runtime 诊断能力整理成稳定的 .NET AI 抽象与工具边界；需要智能体或工作流编排时统一使用 Microsoft Agent Framework，不自建重复的 Agent / Workflow runtime。

架构原则：

1. 本地推理仍由 Tomur native runtime 负责，Agent Framework 只做会话、工具调用、工作流和观测编排。
2. 文本模型优先适配 `Microsoft.Extensions.AI.IChatClient`，embeddings、图像、语音等能力在对应抽象稳定可用时对齐；抽象不足的能力先保留 Tomur 内部接口，并通过 Agent Framework tool 暴露。
3. 内置工具必须是本地优先、可诊断、可权限控制的 .NET 方法，不把浏览器 UI 或第三方远端服务作为默认 agent 依赖。
4. 工具入参、出参和检查点状态使用 source-generated JSON；禁止通过宽泛反射、动态脚本或不透明插件绕开 AOT / trimming 约束。
5. Community 默认只启用轻量会话和本地工具调用；复杂多 Agent、长流程、HITL 和团队治理能力保留为 Pro / Enterprise 或高级可选能力。

交付物：

1. ✅ Tomur 本地 chat runtime 到 `Microsoft.Extensions.AI.IChatClient` 的适配层。
2. ✅ Agent Framework 文本会话入口：`POST /api/agents/chat` 通过 `Microsoft.Agents.AI.ChatClientAgent` 调用本地 `IChatClient`，默认保持 `tool_mode=none`，并支持手动只读工具结果作为 `tool_results` 回填上下文；当调用方显式设置 `tool_mode=read_only` 与 `tools[]` 时，会先通过受控 `AITool` 边界调用 `runtime.diagnose`、`tools.inspect` 或 `files.search`，再把结果作为同一轮会话上下文交给本地 agent；`tool_mode=auto_read_only` 会由 Tomur 根据当前消息选择只读工具，不开放模型自动选择任意工具；`tool_mode=controlled` 可显式调用 ready 的 R8 工具或确认式 `runtime.repair`，artifact 与修复工具必须带 `confirm=true`。
3. ✅ 工具目录与状态映射：`GET /api/agents/runtime` 与 `GET /api/agents/tools` 暴露 chat、image、vision、OCR、ASR、TTS、files 和 runtime diagnostics 的当前状态、路由、schema、side effect、callable、requires confirmation、invocation modes 和诊断动作；`GET /api/agents/tool-bindings` 暴露当前 `Microsoft.Extensions.AI.AITool` 绑定与同样的安全边界字段。
4. ✅ 受控工具调用入口：`POST /api/agents/tools/invoke` 可调用 `runtime.diagnose`、`tools.inspect` 与 `files.search`，也可在 `mode=controlled` 时调用 ready 的 R8 工具或确认式 `runtime.repair`；`image.generate`、`audio.speak` 与 runtime 修复动作需要 `confirm=true`，生成产物写入 `<data>/files/agents`，边界阻塞返回 409，适配器诊断失败返回 503。
5. ✅ 受控只读 workflow：`POST /api/agents/workflows/read-only` 先通过 Tomur 的 bounded tool plan 执行 `runtime.diagnose`、`tools.inspect` 或 `files.search` 只读步骤，并可选通过 `Microsoft.Agents.AI.Workflows` sequential workflow 托管本地 `ChatClientAgent` 摘要工具结果。
6. ✅ 本地事件摘要：agent chat、受控工具调用、只读 workflow 以及对应失败事件会写入 `<data>/logs/agents.jsonl`，`GET /api/agents/events` 可读取最近事件；事件只记录事件名、工具名（如适用）、模式、状态、耗时、阻塞状态和诊断动作，不记录用户消息正文或完整工具结果。
7. ✅ 结构化 agent 错误响应：`POST /api/agents/chat`、`POST /api/agents/workflows/read-only` 与 `POST /api/agents/tools/invoke` 在失败时返回统一的 agent error JSON，包含事件名、模式、工具名（如适用）、runtime、model 与 `RuntimeDiagnostic`，便于后续 UI 状态抽屉和本地调试。
8. ✅ Agent telemetry：`GET /api/agents/telemetry` 暴露 `Tomur.Agents` `ActivitySource` 的 span / attribute 约定与 exporter 状态；agent chat、受控工具调用、只读 workflow 和失败路径会写入不含用户正文与完整工具结果的本地 Activity 标签，OTLP exporter 通过环境变量 opt-in。
9. ✅ 图像生成工具：可通过 `image.generate` controlled 工具调用内部 stable-diffusion.cpp image worker；需要 `confirm=true`，返回本地图像产物 metadata 与诊断。FLUX.2 成功出图 smoke 已在 R8 报告中记录。
10. ✅ 视觉理解工具：可通过 `vision.analyze` controlled 工具调用 VLM adapter，支持 data URI / base64 图片输入。
11. ✅ OCR 工具：可通过 `ocr.recognize` controlled 工具调用 OCR adapter，返回文本与诊断信息。
12. ✅ ASR 工具：可通过 `audio.transcribe` controlled 工具调用 Whisper adapter，返回 transcript 与诊断信息。
13. ✅ TTS 工具：可通过 `audio.speak` controlled 工具调用 llama.cpp GGUF TTS adapter；需要 `confirm=true`，返回本地 WAV 产物 metadata 与诊断。真实成功 WAV smoke 已在 R8 报告中记录。
14. ✅ 文件问答工具：`files.search` 对接 Tomur 管理的 `<data>/files` 本地文本文件、SQLite 表与 FTS5 基础 RAG，不引入 PostgreSQL 作为 Community 默认依赖。
15. ✅ runtime 工具：`runtime.diagnose` 允许会话读取 `doctor` / native / model readiness 诊断；`runtime.repair` 只支持明确枚举的修复动作，并且必须通过 `mode=controlled` 与 `confirm=true` 执行。
16. ✅ Agent Framework telemetry 已接入 opt-in OpenTelemetry exporter 管线：默认保持本地 `ActivitySource` 与 JSONL 事件日志，设置 `TOMUR_AGENTS_OTEL_EXPORTER=otlp` 与 `TOMUR_AGENTS_OTEL_ENDPOINT` 后注册 OTLP trace exporter。

R9 接线状态：

1. `Tomur.csproj` 已引入 `Microsoft.Extensions.AI`、`Microsoft.Agents.AI` 与 `Microsoft.Agents.AI.Workflows`；workflow execution 先用于公开只读编排入口的本地 `ChatClientAgent` 摘要步骤，不承载模型自主多模态工具调用。
2. `LocalChatClient` 已把 Tomur R7 文本 chat runtime 适配为 `IChatClient`，支持模型解析、基础采样参数、system instructions 和 text/tool content 序列化。
3. `AgentRuntimeService` 已能构造本地 `ChatClientAgent` 并提供 `POST /api/agents/chat` 文本会话入口；该入口默认使用 `ChatToolMode.None`，不让模型自动调用任意工具，同时支持 `tool_mode=read_only` 的显式工具上下文：调用方可以在 `tools[]` 中请求 `runtime.diagnose` / `tools.inspect`，Tomur 会通过受控 `AITool` 执行并把结果作为 tool 消息回填给本轮回答；`tool_mode=controlled` 可显式调用 `image.generate`、`vision.analyze`、`ocr.recognize`、`audio.transcribe` 与 `audio.speak`，其中图像生成和 TTS 必须带 `confirm=true`；`tool_mode=auto_controlled` 在未显式给出 `tools[]` 时仍只做只读规划，避免自动构造缺少附件参数的多模态调用。
4. `GET /api/agents/runtime` 与 `GET /api/agents/tools` 已暴露本地工具地图。`chat.respond` 是 agent endpoint；`runtime.diagnose`、`tools.inspect` 与 `files.search` 已作为只读 `AIFunction` 暴露在 `GET /api/agents/tool-bindings`；所有工具描述都带 route、schema、side effect、callable、requires confirmation 与 invocation modes；VLM、OCR、ASR、TTS 与 image generation 会随 backend readiness 标记；`runtime.repair` 作为受控声明工具暴露，修复动作必须显式确认。
5. `POST /api/agents/tools/invoke` 已提供受控工具调用入口：默认只读模式执行 `runtime.diagnose`、`tools.inspect` 与 `files.search`，`mode=controlled` 可执行 ready 的 R8 adapters 和确认式 `runtime.repair`，并返回输入 schema、审计字段、source-generated JSON 结果、产物 metadata 或结构化诊断。
6. `POST /api/agents/workflows/read-only` 已接入受控只读 workflow：可以显式给出 `tools[]`，也可以由 Tomur 从请求消息中规划 `runtime.diagnose` / `tools.inspect`；`respond=false` 时只返回工具步骤，默认在存在本地 chat 模型时用 Agent Framework sequential workflow 托管 `ChatClientAgent` 摘要结果。
7. `GET /api/agents/events` 已接入本地事件摘要读取；agent chat、受控工具调用、只读 workflow 及其失败路径都会写入 `<data>/logs/agents.jsonl`，用于后续 UI 状态抽屉和本地诊断。事件不记录用户消息正文或完整工具结果。
8. `GET /api/agents/telemetry` 已接入 telemetry 状态读取；`AgentTelemetry` 使用 `System.Diagnostics.ActivitySource` 为 `agent_chat`、`tool_invocation`、`read_only_workflow` 与 agent error 路径打点，标签只记录 runtime、model、tool、status、elapsed、blocked、side effect 与诊断 code 等结构化字段；OTLP exporter 默认关闭，仅通过 `TOMUR_AGENTS_OTEL_EXPORTER=otlp` 与 `TOMUR_AGENTS_OTEL_ENDPOINT` opt-in 注册。
9. `/api/agents/chat`、`/api/agents/workflows/read-only` 与 `/api/agents/tools/invoke` 失败时已返回统一的 agent error JSON，而不再只回裸 `RuntimeDiagnostic`，便于 UI 和本地调试直接识别事件类型、模式、工具名、runtime、model 与诊断动作。
10. R9 当前代表 Microsoft AI 抽象、只读 AITool 绑定、SQLite 本地文件检索、ready R8 controlled declaration、确认式 runtime repair、显式受控工具调用、显式/自动只读工具上下文、本地事件摘要、结构化 agent 错误响应、opt-in OpenTelemetry exporter 和 Agent Framework 文本/只读 workflow 编排；不代表模型自主选择多模态 tool-calling 或 checkpoint 已完成。

验收：

1. ✅ 普通文本会话不需要 Agent Framework 也能继续通过兼容 API 运行；R9 smoke 已验证 `GET /v1/models` 返回 200。
2. ✅ `POST /api/agents/chat` 可以通过 `ChatClientAgent` 调用本地文本模型，并可显式或由 Tomur 自动规划注入只读工具上下文；显式 controlled tools 可调用 ready 的 R8 adapters。构建与启动 smoke 已通过；真实多模态模型能力沿用 R8 smoke 证据。
3. ✅ `POST /api/agents/workflows/read-only` 可以执行受控只读工具步骤并可选摘要结果；R9 smoke 已验证 `respond=false` 下执行 `runtime.diagnose` 与 `tools.inspect` 两步成功。
4. ✅ 开启编排后，Tomur 可以在一次会话中按调用方显式工具请求调用图像生成、视觉理解、OCR、ASR、TTS、files.search、runtime 诊断和确认式 runtime repair 工具；模型自主选择多模态工具仍未默认开放。
5. ✅ 工具调用失败时返回可诊断错误，不伪造图像、文字、音频或识别结果；R9 smoke 已验证 `runtime.repair` 缺少 `confirm=true` 返回 409，R8 adapters 的真实模型 smoke 证据已记录。
6. ✅ AOT / trimming 审计已定位到 Microsoft.Extensions.AI、Microsoft.Agents.AI、Microsoft.Agents.AI.Workflows、SQLite 与 OpenTelemetry 调用点；记录见 `docs/r9-aot-trimming-audit.md`。若 Agent Framework 或 OpenTelemetry 依赖暂时阻塞 Native AOT，保留自包含单文件发布路径，并把 AOT 承诺限定在可通过审计的核心 runtime。

### 10. ✅ R10: 会话编排与语音回合服务

目标：在 Tomur 单体中形成一整套对话服务，让用户可以用文本、图片、文件和语音与本地 AI 交互，并允许 AI 在会话中调用图像生成、识别、OCR、ASR、TTS 和本地文件问答能力。

交付物：

1. ✅ 会话状态模型：记录用户消息、附件、工具调用、诊断、生成产物和音频结果，默认存储在本地 SQLite / 文件目录。
2. ✅ 文本会话编排：在同一轮对话中执行 Tomur 确定性工具计划、回填结果并继续生成回答；模型自主多模态 tool-calling 不默认开放。
3. ✅ 多模态附件入口：支持图片、音频和本地文件作为会话输入，远程 URL 不自动下载并返回会话诊断。
4. ✅ 语音输入链路：客户端录音或上传音频后，调用 Whisper ASR 生成 transcript，再交给会话编排层处理。
5. ✅ 语音输出链路：AI 回复生成后，调用 llama.cpp GGUF TTS 生成可播放音频，并把音频结果登记为本地产物。
6. ✅ 语音回合 API：提供“音频输入 -> ASR -> 会话处理 -> TTS -> 音频输出”的单回合服务。
7. ⏳ 流式增强：文本 token streaming、ASR 分段结果、TTS 分段生成、停止生成和取消工具调用。
8. ⏳ VAD 与打断：先实现按键/按钮式录音，后续再加入 VAD、唤醒词、barge-in 打断和播放控制。
9. ⏳ 安全边界：模型触发下载、删除、覆盖、外部网络或修复 runtime 前必须有用户确认。

R10 当前接线状态：

1. `GET /api/conversations` 与 `POST /api/conversations` 已接入本地会话列表和创建入口。
2. `GET /api/conversations/{conversationId}` 已返回会话详情、消息、产物和诊断记录。
3. `POST /api/conversations/{conversationId}/turns` 已接入文本回合编排：追加用户文本消息，处理 data URI / base64 图片、音频和本地文件附件，按明确输入规划 VLM、OCR、ASR、图像生成、files.search 或 TTS 工具，并把 assistant 回复、tool 摘要、产物引用和失败诊断写回同一会话；图像生成、TTS 与 runtime repair 保持 `confirm=true` 边界。
4. `POST /api/conversations/{conversationId}/voice-turns` 已接入语音回合入口：支持 multipart `file` 上传或 JSON `audio_base64` / `audio_data_uri`，先调用本地 ASR 得到 transcript，再复用文本回合编排生成 assistant 回复，默认尝试调用 TTS 并把输入音频与输出音频登记为会话产物；ASR、文本 runtime 或 TTS 缺失时会写入会话诊断，不伪造结果。
5. `POST /api/conversations/{conversationId}/messages` 已支持追加用户、助手、system 和 tool 消息，并记录附件引用、工具调用摘要、关联产物 ID 与 metadata。
6. `POST /api/conversations/{conversationId}/artifacts` 已支持登记本地产物路径、类型、media type、来源、大小和 metadata，为 TTS 音频、图像生成结果和附件处理提供统一落点。
7. `GET /api/conversations/{conversationId}/artifacts/{artifactId}/content` 已支持从 Tomur 数据目录读取会话产物内容，供前端播放 TTS WAV 或展示生成图片；路径超出数据目录时拒绝读取。
8. `POST /api/conversations/{conversationId}/diagnostics` 已支持记录会话级诊断、backend、model、动作建议和 metadata。
9. `DELETE /api/conversations/{conversationId}` 已接入会话软删除，列表默认过滤删除状态，产物文件不被自动移除。
10. SQLite schema 已扩展到会话、消息、产物和诊断表，JSON 字段使用 source-generated serializer 边界；当前不代表模型自主多模态 tool-calling、流式语音回合或 VAD/打断已经完成。

验收：

1. 文本对话中可以要求“生成一张图”“识别这张图”“朗读这段回答”“把这段音频转文字”，系统会调用对应本地能力或返回明确诊断。
2. 语音回合能返回 transcript、assistant text、TTS audio 和完整诊断链路。
3. 任一多模态 backend 缺失时，不影响纯文本会话；UI 和 API 都能提示需要下载的模型或需要准备的 native runtime。
4. 所有会话产物保存在 Tomur 数据目录中，不写入程序二进制，不要求用户理解 backend 内部目录。

### 11. ✅ R11: React + Ant Design X Web UI

目标：实现 Chat-first 的内置本地工作台。R11 不把 Models、Downloads、Runtime、Files 作为默认一级页面；这些能力优先作为 Chat 上下文、状态抽屉和 Settings 分组暴露，避免把 Tomur 首屏做成管理后台。

1. React + TypeScript，Vite 作为构建工具。
2. `antd`。
3. `@ant-design/x`。
4. `@ant-design/x-markdown`。
5. `@ant-design/x-sdk`。
6. 按 Ant Design X 的 Agent TBox / RICH 交互架构组织 Chat 工作台。
7. 使用 Tomur 自己的 API，不接入第三方 agent 服务。
8. 前端构建产物由同一个 Tomur C# 项目托管。
9. 不自研 Bubble、Sender、Conversations、Attachments、Prompts 等基础 AI 对话组件，除非官方组件无法满足 Tomur 的本地运行约束。

M0：工程与工作台外壳：

1. ✅ 建立 `web/` React + TypeScript 工程，使用 Vite 构建。
2. ✅ 接入 `antd`、`@ant-design/x`、`@ant-design/x-markdown` 与 `@ant-design/x-sdk`。
3. ✅ 建立 Chat-first 应用外壳：主区域为 Chat，顶部提供模型选择器，右上角提供 Settings 与状态抽屉入口。
4. ✅ Runtime / download / model readiness 先以紧凑状态条、内联诊断和抽屉呈现，不作为默认一级页面。
5. ✅ 前端构建产物输出到 `app/wwwroot`，并由 `Tomur.csproj` 托管；`tomur open`、无参数启动与 `GET /` 使用同一工作台入口。

R11 当前接线状态：

1. `web/` 已形成可继续演进的 Ant Design X 工作台源码，默认入口为 Chat-first 单页工作台，而不是管理后台式首页。
2. 工作台已接入 `/api/version`、`/api/runtime/status`、`/api/runtime/multimodal`、`/api/models/catalog`、`/api/models/installed` 与 `/v1/models`，可读取本地版本、模型、下载与 runtime 诊断。
3. 工作台已接入 `/v1/chat/completions`，支持基础消息发送、OpenAI 文本增量 streaming、停止生成、重新生成与复制；界面按当前 API 能力显示，不伪造未接通能力。
4. Models、Downloads、Runtime、Files 已收敛到 Settings 分组、状态条、状态抽屉与 Chat 内联诊断入口，符合 Chat-first 信息架构约束。
5. Settings 已具备 General、Models、Downloads、Runtime、API、Files 与 Advanced 分组入口；General 显示配置、数据目录和启动命令，Models 显示本地模型与安装包，Downloads 显示推荐包、license 提示和可复制下载命令，API 显示 host/port/API key 与兼容接口，Files 显示 Tomur 本地目录、SQLite 和目录状态，Advanced 显示 accelerator、agent telemetry/events 与多模态后端状态。
6. Runtime 分组已接入 native bundle 状态、component 状态、诊断提示、`POST /api/runtime/native/prepare`、`POST /api/runtime/session/unload`、最近 prepare 文件结果、复制 CLI/API 动作和明确下一步；prepare 失败时也会保留结构化结果，不伪装为成功。
7. Chat 输入区已接入 Ant Design X `Attachments`，支持图片、音频和文本文件随下一轮发送；带附件或请求朗读的回合会创建或复用后端会话并调用 `/api/conversations/{conversationId}/turns`，后端返回的消息、工具摘要、产物和诊断会回填到当前气泡。
8. 语音入口已接入按钮式录音；浏览器录音会先转为 16 kHz mono PCM WAV，再以 multipart `file` 调用 `/api/conversations/{conversationId}/voice-turns`。语音回合返回的 transcript、assistant 文本、诊断和 TTS 产物会展示在 Chat 中，TTS 音频通过 `/api/conversations/{conversationId}/artifacts/{artifactId}/content` 播放。
9. 纯文本普通对话仍保留 `/v1/chat/completions` streaming 路径；成功回复后会创建或复用 `/api/conversations` 会话并补写文本历史，附件/语音回合复用同一会话上下文。
10. 工作台启动时会读取 `/api/conversations` 列表，点击历史会话时懒加载详情、消息、诊断和产物；会话菜单调用 `DELETE /api/conversations/{conversationId}` 做本地软删除。
11. Chat 气泡下方的诊断标签会根据 code/backend 跳转到对应 Settings 分组，模型缺失、runtime 不可用、native library 缺失、API/文件问题都有上下文入口。
12. R11 当前代表 M0 工作台外壳、M1 Settings 信息架构、状态接线、Runtime 操作区、附件回合、按钮式录音、TTS 播放、诊断上下文入口和会话历史同步已接通；不代表可视化下载队列、Settings 写入编辑、模型删除、VAD/唤醒词/打断或流式语音回合已完成。

M1：Settings 信息架构：

1. ✅ General：版本、配置 schema、server URL、默认 backend、数据目录和启动命令。
2. ✅ Models：本地可见模型、安装包、资产校验状态、推荐下载命令和模型可见性命令。
3. ✅ Downloads：硬件档位建议、推荐包、可下载包、license 提示、proxy / 断点续传 / checksum 对应 CLI 命令入口；可视化下载队列仍属后续增强。
4. ✅ Runtime：native runtime、组件状态、诊断、prepare 动作和 session unload。
5. ✅ API Server：host、port、API key 状态、OpenAI / Ollama 兼容接口和 Conversations API 状态入口。
6. ✅ Files：数据目录、模型目录、runtime 目录、日志目录、SQLite 路径和目录状态；更完整附件目录管理和 RAG 索引配置仍属后续增强。
7. ✅ Advanced：accelerator、proxy、Agent telemetry/events、多模态 backend 状态和 R12 自包含发布命令入口。

Chat 上下文入口：

1. ✅ 模型缺失、runtime 不可用、native library 缺失等状态在 Chat 内联诊断中提示，并可打开对应 Settings 分组；下载中状态仍需随下载队列能力补齐。
2. ✅ 附件、按钮式录音、TTS 播放、会话诊断、历史列表和历史详情已接入 `/api/conversations`；附件目录管理和更完整文件索引配置仍需后续补齐。
3. ✅ 停止生成、重新生成、复制、模型选择和会话软删除已在 Chat 工作台内完成；基础参数调整仍需后续补齐。

验收：

1. 默认入口提供可直接使用的 Chat 工作台。
2. 首屏不出现 Models、Downloads、Runtime、Files 作为默认一级导航；相关管理能力位于 Settings、状态抽屉或 Chat 诊断入口。
3. 支持 OpenAI 文本 streaming 消息、停止生成、重新生成、复制、附件入口、按钮式录音、回复朗读播放和模型选择；UI 必须按当前 API 能力展示，不伪造未接通能力。
4. Runtime、Downloads 和模型状态可以在 UI 中刷新，并能跳转到对应 Settings 分组。
5. 所有文案只描述 Tomur 当前已接通能力。
6. Chat 页面支持文本、图片、文本文件和音频入口；未接通的 backend 显示诊断和修复动作，不展示为已可用能力。
7. 语音模式先支持按钮式录音、转写、回复朗读和播放控制；VAD、唤醒词和打断作为后续增强。

### 12. 🚧 R12: Native AOT / 自包含发布

目标：让 Tomur 形成稳定发布体验。当前项目已确认 Native AOT 发布可通过且无警告；R12 后续重点从 AOT 清警告转向发布矩阵、native bundle 资产随包校验和服务形态 smoke。`self-contained-single-file` 仍作为同一 `tomur` 入口的兼容发布 profile 保留，不代表 Native AOT 阻塞。

交付物：

1. ✅ `PublishAot=true` 发布 profile。
2. ✅ `SelfContained=true`。
3. ✅ `PublishSingleFile=true`。
4. ✅ source-generated JSON 覆盖 API DTO、配置、catalog、工具 schema、workflow 状态和诊断响应。
5. ✅ AOT / trimming warning 已清零，保留 `SuppressTrimAnalysisWarnings=false`，不使用 blanket suppression。
6. ✅ AOT 兼容性矩阵已覆盖 core CLI、HTTP API、native loader、model catalog、download、chat runtime、多模态 adapters、Agent Framework 编排、OpenTelemetry 和 Web 静态托管。
7. ✅ Windows x64 Native AOT 发布已确认无警告。
8. ⏭️ Linux x64 Native AOT 发布记录。
9. ⏭️ macOS x64 / arm64 自包含与 Native AOT 发布记录。
10. ✅ native bundle 发布清单与当前 RID 随包资产 checksum 记录。

R12 当前接线状态：

1. `app/Properties/PublishProfiles/native-aot-audit.pubxml` 已设置 `PublishAot=true`、`SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true` 和 `SuppressTrimAnalysisWarnings=false`。
2. `app/Properties/PublishProfiles/self-contained-single-file.pubxml` 保留非 AOT 自包含单文件发布路径，作为兼容发布 profile。
3. `Tomur.csproj` 在 RID 发布时默认启用 `SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`，并保持 `IncludeAllContentForSelfExtract=false`，避免把模型权重、SQLite 数据库、日志和用户文件写入程序二进制。
4. `JsonSerializerIsReflectionEnabledByDefault=false` 保持开启；API DTO、配置、catalog、runtime、native、agent、conversation 和多模态响应继续登记在 `AppJsonSerializerContext`。
5. R9 记录的 Native AOT 阻塞已经由 R12 承接并清除；当前 Native AOT 发布口径为可通过、无警告。
6. native runtime 仍由 bundle manifest 与 `tomur native prepare` 准备到 Tomur 数据目录下的版本化 runtime 缓存；当前 `win-x64` / `linux-x64` 随包资产与 checksum 已记录在 `docs/r12-native-bundle-inventory.md`。
7. R12 服务形态 smoke 清单已记录在 `docs/r12-service-smoke.md`，覆盖 Windows Service、Linux systemd 与 macOS launchd。
8. R12 发布包结构说明已记录在 `docs/r12-release-package-structure.md`，覆盖单文件边界、native runtime 随包目录、数据目录和最小回归。

仍需补充：

1. Linux x64 Native AOT 发布日志与 smoke 记录。
2. macOS `osx-x64` / `osx-arm64` 自包含与 Native AOT 发布日志、native bundle prepare 和 smoke 记录。
3. Windows Service、Linux systemd、macOS launchd 与 Windows 托盘使用发布产物的实机 smoke。
4. 缺失/损坏 native 资产的 doctor / UI 修复记录。
5. 发布包最小回归执行记录，覆盖 `tomur --help`、`tomur doctor`、`tomur serve`、`GET /health`、`GET /api/version`、`GET /v1/models`、Web 静态托管和 native prepare。

验收：

1. ✅ 发布产物不要求用户安装 .NET runtime。
2. 🚧 发布产物携带必需 native libraries；Windows 路径已进入当前 bundle 口径，Linux/macOS 随包资产仍需补齐记录。
3. ✅ 首次运行能准备本地 runtime 目录。
4. ✅ AOT 警告已逐项处理，不用 blanket suppression 掩盖。
5. ✅ AOT profile 保持完整 Tomur build surface，不通过删除公开能力绕过兼容性问题。
6. ✅ 非 AOT profile 仍保持自包含、单文件或近似单体体验，并使用同一套公开命令与 API。

## ⏭️ 下一步

继续维护已闭环的 R8-R11 smoke 套件，并推进 R12 发布矩阵：

1. 继续用小模型/小素材维护 R8 smoke 套件，保留模型、接口、耗时、诊断和 WAV/PNG 产物证据。
2. 在用户明确要求验证时执行 Tomur 独立项目构建、启动和真实 GGUF chat / embedding smoke。
3. 补一次 Windows CUDA13 真实 chat smoke：下载推荐文本 GGUF，启动 Tomur，发起 `/v1/chat/completions`，记录 selected accelerator、GPU layers、token usage 和错误/成功证据。
4. 为 R7 增强多模型常驻、更细的 session 诊断、真实 CUDA / NPU offload smoke 和 Ollama 增量 streaming。
5. 在后期测试阶段补充 R5 的 Windows Service、Linux systemd、macOS launchd 和 Windows 托盘实机 smoke 验收记录。
6. 补齐并验证 macOS `osx-x64` / `osx-arm64` native runtime bundle 资产。
7. 后续把模型自主工具选择循环、checkpoint 与更完整文件附件 RAG 放入 R12 之后的增量，不改变 R9 已完成的受控工具边界。
8. 为 R10/R11 补构建/启动 smoke，并按 `docs/r10-r11-smoke-maintenance.md` 维护 Web 录音入口、播放控制、失败诊断展示和会话历史同步的回归清单。
9. 按 `docs/r12-aot-release-audit.md` 补齐 R12 Linux/macOS 发布执行记录、服务形态实机 smoke 和发布包最小回归证据。


