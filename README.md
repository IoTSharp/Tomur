# Tomur

[English](./README.en.md)

Tomur 是一个集成了本地模型服务、OpenAI / Ollama / Anthropic Messages 兼容 API、模型资产管理、运行时诊断和 Chat-first Web 工作台的本地 AI 基础设施，面向离线优先、隐私敏感、低运维成本的个人与团队开发环境。它基于 .NET 10 与 C# 构建，以单一 `tomur` 进程承载 CLI、本地 HTTP 服务、系统服务形态、native runtime 管理和 Web 静态资源托管。

Tomur 将模型权重、SQLite 数据库、日志、用户文件和生成结果作为本地资产管理；程序负责下载、校验、准备运行时并返回可诊断的错误，让使用者通过一个入口运行、调试和使用本机模型，而不必理解底层 native backend 或模型目录实现。

## 🧭 为什么是 Tomur

本地 AI 工具链通常会同时涉及模型文件、native dynamic libraries、兼容 API、本地服务、Web 对话界面、日志和诊断。Tomur 的目标是把这些零散部分收敛成一个本地程序：

1. 🔌 启动一个本地服务，提供 OpenAI / Ollama / Anthropic Messages 兼容 API。
2. 📦 在同一个入口中管理模型下载、校验和本地可见性。
3. 💬 使用内置 Web 工作台直接对话、上传附件、查看运行时状态。
4. 🩺 通过 `tomur doctor`、Runtime API 和 UI 诊断 native library、模型、端口、代理、SQLite 与硬件状态。
5. 🚀 以自包含、单文件、Native AOT 友好的发布路线降低部署前置条件。

Tomur 关注的是本机 AI 运行体验，不是多租户服务器、后台管理平台或复杂工作流治理系统。

## 🚀 快速开始

查看命令入口：

```powershell
tomur --help
```

启动本地服务并打开工作台：

```powershell
tomur open
```

准备 native runtime，并安装推荐模型包：

```powershell
tomur native prepare
tomur pull recommended
```

以服务模式运行本地 HTTP API：

```powershell
tomur serve --open
```

源码仓库内开发运行时，可直接指向主程序项目：

```powershell
dotnet run --project app -- --help
dotnet run --project app -- serve --open
```

默认本地服务地址为 `http://127.0.0.1:5137`。

## 🧩 目标能力

1. 💬 本地文本生成。
2. 🧠 本地 embeddings 与 reranking。
3. 🔌 OpenAI 兼容 HTTP API。
4. 🔁 Ollama 兼容 HTTP API。
5. 🧩 Claude Code 所需的 Anthropic Messages 兼容入口。
6. 📦 模型目录、下载、校验与本地资产管理。
7. 🩺 CPU、内存、磁盘、代理、端口、模型与 native libraries 运行时诊断。
8. ⚙️ llama.cpp、Whisper、OCR native、stable-diffusion.cpp 与 llama.cpp TTS / GGUF TTS native runtime 支持。
9. 🧮 可选的纯 C# 模型提供器，用于按模型架构逐步扩展本地推理路径。
10. 🖥️ 系统服务运行模式。
11. 🧑‍💻 React + Ant Design X Web 工作台。

Tomur 不会在未接通本地 runtime 时伪造推理结果。模型缺失、native runtime 或托管 provider 不可用、bundle 资产损坏、上下文超限、能力不匹配或内存不足时，API、CLI 和 UI 都应返回可诊断的错误。

## 🔌 API 示例

健康检查：

```powershell
curl.exe http://127.0.0.1:5137/health
```

列出本地可见模型：

```powershell
curl.exe http://127.0.0.1:5137/v1/models
```

调用 OpenAI 风格聊天接口：

```powershell
curl.exe http://127.0.0.1:5137/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{
    "model": "qwen35-9b-q4km",
    "messages": [
      { "role": "user", "content": "用一句话介绍 Tomur。" }
    ],
    "stream": false
  }'
```

调用 Ollama 风格聊天接口：

```powershell
curl.exe http://127.0.0.1:5137/api/chat `
  -H "Content-Type: application/json" `
  -d '{
    "model": "qwen35-9b-q4km",
    "messages": [
      { "role": "user", "content": "列出当前 runtime 状态。" }
    ],
    "stream": false
}'
```

调用 Claude Code / Anthropic Messages 风格聊天接口：

```powershell
curl.exe "http://127.0.0.1:5137/v1/models?limit=1000"

curl.exe http://127.0.0.1:5137/v1/messages `
  -H "Content-Type: application/json" `
  -H "anthropic-version: 2023-06-01" `
  -d '{
    "model": "claude-tomur-qwen35-9b-q4km-<hash>",
    "max_tokens": 512,
    "messages": [
      { "role": "user", "content": "用一句话介绍 Tomur。" }
    ],
    "stream": false
  }'
```

`GET /v1/models?limit=1000` 会为本地文本模型返回 `claude-tomur-*` 发现别名；实际可用别名以本地模型列表为准。

实际可用模型名来自本地安装清单与模型目录。可使用以下命令查看：

```powershell
tomur list
tomur ps
tomur list --catalog
```

## 🚧 当前状态

Tomur 已完成 R1 至 R11 的主要闭环，并进入 R12 Native AOT / 自包含发布矩阵收敛、R13 Web 前端能力聚合闭环、R14 Intel GPU / NPU 加速与 R15 纯 C# GLM / MoE provider 实验阶段。已完成历史见 [CHANGELOG.md](./CHANGELOG.md)。

| 阶段 | 状态 |
| --- | --- |
| R1-R4 | 单项目 API 骨架、配置与本地状态、native bundle 边界、OpenAI / Ollama 首批兼容 API 已接入。 |
| R5-R7 | 系统服务代码路径、模型 Catalog 与下载、本地 llama.cpp 文本推理首通已接入。 |
| R8 | Whisper ASR、GGUF TTS、VLM、OCR 与 stable-diffusion.cpp 图像生成已完成当前公开接口范围内的真实模型 smoke 记录。 |
| R9-R10 | Microsoft AI 抽象、Agent Framework 受控编排、SQLite 本地文件检索、会话状态、附件入口和语音回合服务已接入。 |
| R11 | React + Ant Design X Chat-first Web 工作台已接入，并由 `app/wwwroot` 通过 Tomur 本地 HTTP 服务托管。 |
| R12 | Native AOT 发布已确认可通过且无警告；Linux/macOS 发布记录、macOS native bundle 资产与服务形态实机 smoke 仍在收敛。 |
| R13 | Web 前端能力聚合闭环已接入 Agent / Capabilities 聚合视图、只读 Agent 工具入口、副作用工具确认流、协议能力地图和 Claude Code / Anthropic Messages 兼容协议面；可视化下载队列和 Settings 写入仍在推进。 |
| R14 | Intel GPU / NPU 支持开始接入现有 ggml dynamic backend 机制；`vulkan`、`sycl`、`openvino` 与 `intel` native build 入口、runtime accelerator 偏好、OpenVINO / NPU 环境设置、CPU fallback 诊断、NPU 不适配错误返回、Web Runtime 展示和 smoke 记录入口已建立。真实 Intel GPU / NPU smoke 仍需实机记录。 |
| R15 | M1-M10 基础代码已完成；M11 性能基础代码正在推进。当前已接入显式 `packed-offset` rowwise int4/int8 GLM 权重、managed model readiness、OpenAI / Anthropic SSE、Ollama 增量 streaming、可取消 session unload、结构化 session/resource 诊断、可回退 scalar 的 SIMD/并行 matvec、gate/up paired dispatch 和自动 expert cache/hot pin/prefetch。managed GLM 已增加显式 `glm4_moe_lite` architecture/config/prompt 契约，但真实 REAP 权重的转换、完整模型加载和对话验证仍待异机执行。随机 tiny GLM 已完成三类兼容 API 链路 smoke；`allenai/OLMoE-1B-7B-0125-Instruct` 原始 BF16 权重已通过 Catalog、provider load 和中文 Ollama 非流式真实对话，证据见 [packed GLM](./docs/r15-packed-glm-smoke.md)、[GLM4 MoE Lite 异机验证计划](./docs/r15-glm4-moe-lite-validation.md) 与 [OLMoE real-model smoke](./docs/r15-olmoe-smoke.md)。新增 M10/M11 回归代码尚未执行构建、测试、服务 smoke 或性能测量；阶段 timing、activation integer dot、prefill batch expert union 与 mmap 实验仍待完成。OLMoE 的 OpenAI、Anthropic、SSE、完整 int8 转换和性能优化仍待验证。约 370 GB 的完整 GLM-5.2 模型尚未执行真实对话。现有 llama.cpp 路径继续保留并作为默认路径。 |

仍属于后续工作的内容包括：Intel GPU / NPU 真实 smoke（记录入口见 `docs/r14-intel-acceleration-smoke.md`）、可视化下载队列、Settings 写入编辑、模型删除、VAD / 打断、流式语音回合、多模型常驻、Linux/macOS 发布执行记录和服务形态实机 smoke。

详细阶段计划与验收边界见 [ROADMAP.md](./ROADMAP.md)。

## 🏗️ 架构概览

主程序保持集中，纯托管模型提供器使用独立类库隔离：

```text
Tomur/
  README.md
  README.en.md
  CHANGELOG.md
  ROADMAP.md
  app/
    Tomur.csproj
    Program.cs
    Api/
    Cli/
    Config/
    Native/
    Providers/
    Runtime/
    Services/
    Web/
  providers/
    Glm/
      Tomur.Providers.Glm.csproj
  tests/
    Tomur.Providers.M1.Tests/
    Tomur.Providers.M2.Tests/
    Tomur.Providers.M3.Tests/
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

`Tomur.csproj` 承载 CLI、本地 HTTP API、服务模式启动、runtime 管理和 Web 静态资源托管。`Program.cs` 负责进程入口、顶层命令分发和全局帮助；具体 CLI 实现按类别放在 `app/Cli/`。`providers/` 仅用于独立纯 C# 模型提供器，不拆出第二套服务或产品入口；`tests/` 只承载阶段化验证项目。

`native/` 放置 native backend 源码、CMake 工程和发布打包边界；`app/Native/` 只保留 C# 动态库加载、P/Invoke 与 native 适配代码。纯托管 provider 不替换 llama.cpp 等现有路径，并按模型格式和架构显式选择。Web 源码位于 `web/`，构建产物输出到 `app/wwwroot`，运行时由 Tomur 本地 HTTP 服务托管。

## 📁 本地状态

Tomur 使用稳定的数据目录保存配置、模型、runtime 缓存、SQLite 数据库、日志和生成产物。

| 平台 | 默认数据目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Tomur` |
| Linux | `~/.local/share/tomur` |
| macOS | `~/Library/Application Support/Tomur` |

数据目录内的关键路径：

| 路径 | 用途 |
| --- | --- |
| `<data>/config/tomur.json` | 本地配置文件 |
| `<data>/tomur.db` | SQLite 数据库 |
| `<data>/runtime` | 版本化 native runtime 缓存 |
| `<data>/models` | 本地模型目录与安装清单 |
| `<data>/logs` | 日志目录 |

可通过 `--data-dir <path>` 或 `TOMUR_DATA_DIR` 覆盖数据目录。配置文件损坏时，诊断流程会把损坏文件移动为 `.damaged-<timestamp>`，再写入默认配置。

## 📦 运行时资产

Tomur 的发布产物应携带必要的 C++ native dynamic libraries，并在首次运行或版本变化时准备到 Tomur 管理的 runtime 目录。模型权重不会被打包进程序二进制，而是由 `tomur pull` 下载到本地模型目录，并登记到 `<data>/models/models.manifest.json`。

独立纯托管 provider DLL 属于非 AOT 自包含发布面；Native AOT 发布需要静态引用兼容 provider，或明确报告动态托管 provider 不可用。该差异不影响现有 native provider 的发布和使用。

`tomur native prepare` 用于释放或修复 native runtime bundle；`tomur doctor` 用于检查 runtime、模型、SQLite、端口、代理与硬件状态。缺失或损坏的 native library 会通过 CLI、API 和 UI 返回明确诊断。

Windows x64 native 构建入口：

```powershell
tomur native build --rid win-x64 --backend all
tomur native build --rid win-x64 --backend vulkan
tomur native build --rid win-x64 --backend sycl
tomur native build --rid win-x64 --backend openvino
tomur native build --rid win-x64 --backend intel
```

`--backend cpu` 与 `--backend cuda13` 可只构建单一变体；`--backend intel` 会构建 llama.cpp 的 `sycl`、`openvino` 与 `vulkan` dynamic backend 入口。缺失 Intel backend 或设备不可枚举时，Tomur 会保留 CPU fallback 并在 `tomur doctor`、`/api/runtime/status` 和 Web Runtime 面板中显示原因。

## 🗺️ 路线图

长期阶段计划、完成口径和后续工作维护在 [ROADMAP.md](./ROADMAP.md)；已完成历史维护在 [CHANGELOG.md](./CHANGELOG.md)。README 只保留项目首页所需的定位、使用路径和当前边界。
