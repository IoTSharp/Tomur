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
    stable-diffusion.cpp/
    stable-diffusion.native/
  web/
    package.json
    src/
```

1. `Tomur.csproj` 是唯一 .NET 项目。
2. CLI、HTTP API、服务模式、runtime 管理和静态 Web UI 都由同一个进程承载。
3. `web/` 是 React 前端源码目录，构建产物由 `Tomur.csproj` 作为静态资源托管。
4. `native/` 用于放置 native backend 源码、CMake 工程和发布打包边界，不作为独立 .NET 项目。
5. `app/Native/` 只放 C# 动态库加载、P/Invoke 和托管适配边界。
6. 在 API、native runtime、AOT 和发布流程稳定之前，不拆分多个 C# 项目。

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
8. 按 Ant Design X 的 RICH 交互范式组织 Chat、Models、Downloads、Runtime、Files、Settings。
9. 使用 Bubble、Sender、Attachments、Prompts、Conversations、Welcome、Suggestion、XStream / XRequest 等官方能力搭建 UI。
10. Tomur 前端只连接 Tomur 本地 OpenAI / Ollama 兼容 API，不把第三方 API key 暴露到浏览器。

边界约束：

1. 直接使用官方 `@ant-design/x` React 组件体系。
2. 不自研 AI 对话基础组件，除非官方组件无法满足 Tomur 的本地运行约束。
3. 不把 Web UI 拆成独立产品；它始终由 Tomur 本地服务托管。

## 📦 自包含与 Native 资产策略

Tomur 的自包含目标是降低本地部署前置条件，避免要求用户单独安装 .NET runtime 或手工准备 C++ dynamic libraries。

1. `tomur.exe` / `tomur` 自包含 .NET runtime。
2. llama.cpp、Whisper、OCR、stable-diffusion.cpp、Qwen TTS 等 C++ dynamic libraries 由 Tomur 发布产物携带。
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
3. runtime 缓存：`<data>/runtime`
4. 模型目录：`<data>/models`
5. SQLite 数据库：`<data>/tomur.db`
6. 日志目录：`<data>/logs`
7. Web UI 静态资源：程序内置，运行时由本地 HTTP 服务托管。

## 🗺️ 阶段计划

| 顺序 | 阶段 | 状态 | 主题 |
| --- | --- | --- | --- |
| 00 | R0 | ✅ 已完成 | 项目门面 |
| 01 | R1 | 🚧 进行中 | 单项目 API 骨架 |
| 02 | R2 | 🚧 进行中 | 配置、数据目录与诊断 |
| 03 | R3 | ⏳ 计划中 | Native Library Bundle |
| 04 | R4 | ⏳ 计划中 | OpenAI / Ollama 兼容 API |
| 05 | R5 | ⏳ 计划中 | Windows Service / Linux systemd |
| 06 | R6 | ⏳ 计划中 | 模型 Catalog 与下载 |
| 07 | R7 | ⏳ 计划中 | 本地推理首通 |
| 08 | R8 | ⏳ 计划中 | 多模态能力 |
| 09 | R9 | ⏳ 计划中 | React + Ant Design X Web UI |
| 10 | R10 | ⏳ 计划中 | Native AOT 发布 |

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

### 01. 🚧 R1: 单项目 API 骨架

目标：先实现 API 级别访问，形成可启动的本地服务骨架。

交付物：

1. `Tomur.csproj`。
2. `Program.cs`。
3. `tomur --help`。
4. `tomur serve`。
5. `tomur doctor`。
6. `GET /health`。
7. `GET /api/version`。
8. `GET /v1/models`。
9. `POST /v1/chat/completions` 的协议骨架。
10. `POST /api/chat` 的协议骨架。

验收：

1. 服务能在本地启动。
2. OpenAI 风格客户端可以请求 `/v1/models`。
3. Ollama 风格客户端可以请求 `/api/version`。
4. 聊天端点先返回明确的未配置 runtime 诊断，而不是假装已经完成推理。

### 02. 🚧 R2: 配置、数据目录与诊断

目标：让 Tomur 有稳定的本地状态模型。

交付物：

1. ✅ 数据目录解析，支持默认目录、`TOMUR_DATA_DIR` 与 `--data-dir`。
2. ✅ 配置文件 `<data>/config/tomur.json`。
3. ✅ SQLite 初始化 `<data>/tomur.db`。
4. ✅ API key 本地哈希存储，提供 `tomur api-key create/list`。
5. ✅ `tomur doctor` 输出 OS、架构、CPU、内存、磁盘、proxy、端口、数据目录、SQLite、API key 与 runtime 状态。
6. ✅ `/api/runtime/status`。

验收：

1. 首次运行会创建必要目录。
2. 诊断输出清楚区分 ok、warning、error。
3. 配置损坏时有恢复路径。
4. 待显式构建或启动验证后再标记 R2 完成。

### 03. ⏳ R3: Native Library Bundle

目标：让 Tomur 自己携带并解析 C++ dynamic libraries。

交付物：

1. native bundle manifest。
2. 版本化 runtime extraction。
3. library checksum。
4. runtime probe。
5. Windows DLL search path 支持。
6. Linux shared object probe 支持。
7. ggml 共享库隔离规则。

验收：

1. Tomur 不依赖系统全局安装的 llama.cpp / Whisper / stable-diffusion.cpp 动态库。
2. runtime probe 可以报告每个 backend 是否可用。
3. 损坏的 native library 会触发重新释放或修复提示。

### 04. ⏳ R4: OpenAI / Ollama 兼容 API

目标：补齐第一批真实协议面。

OpenAI 首批端点：

1. `GET /v1/models`
2. `POST /v1/chat/completions`
3. `POST /v1/completions`
4. `POST /v1/embeddings`
5. `POST /v1/images/generations`

Ollama 首批端点：

1. `GET /api/version`
2. `GET /api/tags`
3. `POST /api/show`
4. `POST /api/generate`
5. `POST /api/chat`

验收：

1. streaming response 行为稳定。
2. 错误响应符合对应 API 风格。
3. 未下载模型、runtime 不可用、上下文超限等情况都有清晰错误码。

### 05. ⏳ R5: Windows Service / Linux systemd

目标：让 Tomur 可以作为系统服务常驻。

交付物：

1. `tomur service install`。
2. `tomur service uninstall`。
3. `tomur service start`。
4. `tomur service stop`。
5. `tomur service status`。
6. Windows Service 支持。
7. systemd service 支持。

验收：

1. Windows 服务名稳定为 `Tomur`。
2. Linux unit 名稳定为 `tomur.service`。
3. 服务模式和 `tomur serve` 使用同一套 host 逻辑。
4. 日志、工作目录和权限问题可诊断。

### 06. ⏳ R6: 模型 Catalog 与下载

目标：让 Tomur 可以推荐、下载、校验和列出本地模型。

交付物：

1. `tomur pull`。
2. `tomur list`。
3. `tomur ps`。
4. 下载断点续传。
5. checksum 校验。
6. proxy 支持。
7. license 提示。
8. 硬件档位推荐。

默认模型包：

1. 本地通用助手：`unsloth/Qwen3.5-9B-GGUF` / `Qwen3.5-9B-Q4_K_M.gguf`。
2. 文本翻译：`Mungert/Hunyuan-MT-7B-GGUF` / `Hunyuan-MT-7B-q4_k_m.gguf`。
3. Embeddings：`ggml-org/embeddinggemma-300M-GGUF` / `embeddinggemma-300M-Q8_0.gguf`。
4. Reranker：`gpustack/bge-reranker-v2-m3-GGUF` / `bge-reranker-v2-m3-Q8_0.gguf`。
5. ASR：`ggerganov/whisper.cpp` / `ggml-large-v3-turbo-q5_0.bin`。
6. VAD sidecar：`ggml-org/whisper-vad` / `ggml-silero-v6.2.0.bin`。
7. TTS：ElBruno.QwenTTS / Qwen TTS bundle。
8. 视觉理解：`unsloth/Qwen3-VL-4B-Instruct-GGUF` / `Qwen3-VL-4B-Instruct-Q4_K_M.gguf` + `mmproj-F16.gguf`。
9. 图像生成：`unsloth/FLUX.2-klein-4B-GGUF` + sidecar bundle。

验收：

1. 低内存机器能得到更小模型建议，并明确提示。
2. 下载失败可恢复。
3. 校验失败不会把模型标记为可用。

### 07. ⏳ R7: 本地推理首通

目标：接通文本模型运行。

交付物：

1. llama.cpp session manager。
2. chat completion。
3. completion。
4. embeddings。
5. 模型加载、卸载、状态查询。
6. 基础 token usage。

验收：

1. `/v1/chat/completions` 可以调用本地模型返回文本。
2. `/api/chat` 可以调用本地模型返回文本。
3. 模型未加载、内存不足、文件损坏时可诊断。

### 08. ⏳ R8: 多模态能力

目标：逐步接通语音、图像、OCR 和视觉理解。

交付物：

1. Whisper ASR。
2. Qwen TTS。
3. OCR。
4. stable-diffusion.cpp 图像生成。
5. VLM。
6. `/v1/images/generations`。
7. 后续音频 API。

验收：

1. 每个 backend 都可以独立诊断。
2. 未配置 backend 不影响文本 API。
3. Web UI 能显示 runtime 缺失和修复动作。

### 09. ⏳ R9: React + Ant Design X Web UI

目标：实现内置本地工作台。

1. React + TypeScript + Vite。
2. `antd`。
3. `@ant-design/x`。
4. `@ant-design/x-markdown`。
5. `@ant-design/x-sdk`。
6. 按 Ant Design X 的 Agent TBox / RICH 交互架构组织页面。
7. 使用 Tomur 自己的 API，不接入第三方 agent 服务。
8. 前端构建产物由同一个 Tomur C# 项目托管。
9. 不自研 Bubble、Sender、Conversations、Attachments、Prompts 等基础 AI 对话组件，除非官方组件无法满足 Tomur 的本地运行约束。

首批页面：

1. Chat。
2. Models。
3. Downloads。
4. Runtime。
5. Files。
6. Settings。

验收：

1. 默认入口提供可直接使用的 Chat 工作台。
2. 支持 streaming 消息、停止生成、重新生成、复制、附件入口和模型选择。
3. Runtime 和 Downloads 状态可以在 UI 中实时刷新。
4. 所有文案只描述 Tomur 当前已接通能力。

### 10. ⏳ R10: Native AOT 发布

目标：让 Tomur 形成稳定发布体验。

交付物：

1. `PublishAot=true`。
2. `SelfContained=true`。
3. `PublishSingleFile=true`。
4. source-generated JSON。
5. trimming 兼容配置。
6. Windows x64 发布。
7. Linux x64 发布。
8. native bundle 发布清单。

验收：

1. 发布产物不要求用户安装 .NET runtime。
2. 发布产物携带必需 native libraries。
3. 首次运行能准备本地 runtime 目录。
4. AOT 警告必须逐项处理，不用 blanket suppression 掩盖。

## ⏭️ 下一步

继续完成 R2 验证并准备 R3：

1. 按项目约束执行显式构建或启动验证后，将 R1/R2 状态更新为完成。
2. 检查 `tomur doctor --data-dir <path>`、`tomur api-key create/list` 与 `/api/runtime/status`。
3. 进入 R3 Native Library Bundle，建立 native bundle manifest、版本化 runtime extraction 与 backend probe。
