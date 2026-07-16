# Tomur

[English](./README.en.md)

Tomur 是基于 .NET 10 与 C# 构建的本地 AI 运行时与开发者工作台，面向离线优先、隐私敏感、低运维成本的个人与团队开发环境。单一 `tomur` 进程同时承载 CLI、本地 HTTP 服务、系统服务形态、模型资产管理、运行时诊断和 Chat-first Web 工作台。

Tomur 同时支持两条本地大模型运行路线：由 Tomur 使用纯 C# 实现的托管模型 provider，以及以 llama.cpp 为核心的 native runtime。两条路线并行存在，按模型格式、架构和本机运行条件显式选择。

| 技术路线 | 实现位置 | 能力边界 |
| --- | --- | --- |
| 纯 C# 托管模型 | `providers/Glm`、`providers/Olmoe` | 使用 C# 实现 safetensors 读取、tokenizer、张量与量化 kernel、KV cache、attention、MoE routing、expert cache 和增量生成，不依赖第三方推理 dynamic library。已接通 GLM / MoE 与 OLMoE 的显式模型格式，并取得定向真实模型推理证据；完整协议、性能、资源释放和跨平台矩阵仍按路线图收敛。 |
| llama.cpp native | `native/llama.cpp`、`native/llama.native`、`app/Inference` | 面向 GGUF 文本生成与 embeddings，使用 Tomur 管理的 native bundle、硬件 backend 选择、GPU offload 和 CPU fallback，是当前文本与 embedding 兼容 API 的默认且已有验证路线。 |

两条路线共用同一套模型 Catalog、安装清单、session 管理、OpenAI / Ollama / Anthropic Messages 兼容 API、Runtime 诊断和 Web Chat。模型权重、SQLite 数据库、日志、用户文件和生成结果统一作为本地资产管理。

## 🧭 为什么是 Tomur

Tomur 不把本地模型能力限定在单一推理后端。纯 C# provider 与 llama.cpp native runtime 在同一个本地程序中使用一致的模型、协议和诊断边界：

1. 🧮 按模型格式和架构在纯 C# GLM / OLMoE provider 与 llama.cpp GGUF runtime 之间显式选择，不用更换服务入口。
2. 🔌 通过同一个本地服务提供 OpenAI、Ollama 和 Anthropic Messages 兼容 API。
3. 📦 在同一个 Catalog 和数据目录中管理模型下载、checksum、安装清单与本地可见性。
4. 💬 使用同一个 Web 工作台直接对话、上传附件并查看实际使用的 provider、runtime 和 session 状态。
5. 🩺 通过 `tomur doctor`、Runtime API 和 UI 诊断托管 provider、native library、模型、内存、端口、代理、SQLite 与硬件状态。
6. 🚀 以自包含、单文件、Native AOT 友好的发布路线降低本地部署前置条件。

Tomur 关注的是本机 AI 运行体验，不是多租户服务器、后台管理平台或复杂工作流治理系统。

## 💬 交流讨论

欢迎加入 Tomur 企业微信群，交流使用体验、本地 AI 实践与项目开发。

<img src="./docs/images/tomur-wecom-group.png" alt="Tomur 企业微信群二维码" width="240">

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

1. 💬 通过纯 C# provider 与 llama.cpp native runtime 两条路线执行本地文本生成。
2. 🧮 使用 `providers/Glm` 与 `providers/Olmoe` 承载纯 C# GLM / MoE、OLMoE 模型加载、量化、缓存和生成。
3. ⚙️ 使用 llama.cpp 承载 GGUF 文本生成、embeddings、硬件加速选择与 CPU fallback。
4. 🧠 本地 embeddings 与 reranking。
5. 🔌 OpenAI 兼容 HTTP API。
6. 🔁 Ollama 兼容 HTTP API。
7. 🧩 Claude Code 所需的 Anthropic Messages 兼容入口。
8. 📦 模型目录、下载、校验与本地资产管理。
9. 🩺 CPU、内存、磁盘、代理、端口、模型、托管 provider 与 native libraries 运行时诊断。
10. 🎛️ Whisper、OCR native、stable-diffusion.cpp 与 llama.cpp TTS / GGUF TTS 多模态 native runtime。
11. 🖥️ 系统服务运行模式。
12. 🧑‍💻 React + Ant Design X Web 工作台。

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

## 🏗️ 架构概览

Tomur 保持单进程产品边界。仓库按主程序、托管模型 provider、native runtime、Web 工作台和验证项目组织：

```text
Tomur/
  Tomur.slnx
  app/
    Tomur.csproj
    Program.cs
    Agents/
    Api/
      Anthropic/
      Ollama/
      OpenAI/
    Assets/
    Cli/
    Config/
    Conversations/
    Diagnostics/
    Hardware/
    Inference/
    Models/
    Multimodal/
    Native/
    Providers/
    Runtime/
    Serialization/
    Services/
    Storage/
    wwwroot/
  providers/
    Abstractions/
      Tomur.Providers.Abstractions.csproj
    Glm/
      Tomur.Providers.Glm.csproj
    Olmoe/
      Tomur.Providers.Olmoe.csproj
  tests/
    Tomur.Providers.M1.Tests/ ... Tomur.Providers.M13.Tests/
    Tomur.Providers.Olmoe.Tests/
  native/
    bundle.manifest.json
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
      app/
      components/
  docs/
  README.md
  README.en.md
  ROADMAP.md
  CHANGELOG.md
```

`app/Tomur.csproj` 是唯一产品宿主，承载 CLI、ASP.NET Core 本地 HTTP API、系统服务与托盘启动、模型和会话管理、runtime 诊断以及 Web 静态资源托管。`Program.cs` 只负责进程入口、顶层命令分发和全局帮助；`app/Cli/ServeCommand.cs` 组装同一套本地服务 host，`app/Api/` 提供 Tomur API 与 OpenAI、Ollama、Anthropic Messages 兼容入口。

`providers/Abstractions` 保存主程序与托管 provider 共用的模型描述、manifest、推理和 session 契约。`providers/Glm` 与 `providers/Olmoe` 实现纯 C# 模型加载和生成；OLMoE 当前同时复用 GLM 项目的托管 tensor、kernel 与存储基础。主程序直接引用并注册这两个 provider，再根据本地模型格式、架构和 manifest 显式选择；未匹配的 GGUF 文本与 embedding 模型继续使用 llama.cpp 路径。provider 类库不提供独立进程或另一套 HTTP API。

`native/` 保存上游源码、Tomur CMake 适配工程和 `bundle.manifest.json` 发布清单。`app/Native/` 负责 bundle 准备、动态库解析和加载，`app/Inference/` 承载 llama.cpp 文本 session，`app/Multimodal/` 连接 Whisper、OCR、stable-diffusion.cpp 与 GGUF TTS。纯托管 provider 与这些 native runtime 并行存在，不替换现有 native 能力。

`web/` 使用 React、TypeScript、Vite 与 Ant Design X；构建产物写入 `app/wwwroot` 并作为嵌入资源由 Tomur 本地 HTTP 服务托管。`tests/` 中的 M1-M13 项目覆盖 GLM provider 的分阶段契约与回归，OLMoE 使用独立测试项目；它们只属于验证面，不形成产品服务。

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

纯托管 provider 集合由 `Tomur.csproj` 的项目引用在构建时确定，GLM 与 OLMoE provider 在进程启动时通过 `ModelProviderRegistry` 静态注册，不从外部 `providers/` 目录动态发现程序集。模型 manifest 仍负责声明 provider、架构和格式；构建未包含对应 provider 或模型资产不完整时，Catalog、API、doctor 与 Runtime UI 返回明确诊断。Native AOT 与非 AOT 发布使用各自构建中已纳入的 provider 集合，该边界不影响现有 native provider 的发布和使用。

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
