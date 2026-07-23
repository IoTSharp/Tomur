# Tomur Roadmap

本文件记录 Tomur 的后续产品与工程路线图，作为功能拆分、架构取舍和阶段验收的依据。已完成阶段的历史记录维护在 [CHANGELOG.md](./CHANGELOG.md)。

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
2. 提供 OpenAI / Ollama / Anthropic Messages 兼容接口，降低现有客户端和自动化工具的接入成本。
3. 支持交互式运行、后台服务运行和系统服务运行。
4. 同时维护非 AOT 自包含与 Native AOT 友好的发布路线，并明确不同发布面可加载的 provider 范围。
5. 保留发行包携带的 C++ native dynamic libraries，同时允许纯 C# 模型提供器作为并行运行路径。
6. 模型权重、SQLite 数据库、日志和生成结果由 Tomur 作为本地资产管理，独立于程序二进制更新。

## 🏗️ 工程形态

主程序保持集中，纯托管模型提供器使用独立类库隔离：

```text
Tomur/
  README.md
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

1. `Tomur.csproj` 是主程序项目；`providers/` 只承载独立纯托管模型提供器及其实现。
2. `Program.cs` 承担进程入口、顶层命令分发和全局帮助；CLI 具体命令按类别放在 `app/Cli/`。
3. CLI、HTTP API、服务模式、runtime 管理和静态 Web UI 都由同一个进程承载。
4. `web/` 是 React 前端源码目录，构建产物由 `Tomur.csproj` 作为静态资源托管。
5. `native/` 用于放置 native backend 源码、CMake 工程和发布打包边界，不作为独立 .NET 项目。
6. `app/Native/` 只放 C# 动态库加载、P/Invoke 和 native 适配边界；通用 provider 契约与选择逻辑放在 `app/Providers/`。
7. 除模型提供器及其必要契约外，不按 API、CLI、存储或业务功能继续拆分项目。
8. 现有 native providers 保持可用且默认行为不变；新增纯托管 provider 必须按模型格式和架构显式选择。
9. `tests/` 可以按验证阶段建立独立测试项目，但不形成新的产品、服务进程或 HTTP API。

## 🎨 Web UI 技术决策

Tomur Web UI 采用 Ant Design X 的 AI 应用技术架构，不自研基础对话组件。

1. React。
2. TypeScript。
3. Vite。
4. `antd`。
5. `@ant-design/x`。
6. `@ant-design/x-markdown`。
7. `@ant-design/x-sdk`。
8. 按 Ant Design X 的 Agent TBox / RICH 交互范式组织 Chat-first 工作台。
9. Models、Downloads、Runtime、Files 默认收敛为 Settings 分组、状态抽屉或 Chat 上下文诊断入口。
10. Tomur 前端只连接 Tomur 本地兼容 API，不把第三方 API key 暴露到浏览器。

## 📦 自包含与 Native 资产策略

Tomur 的自包含目标是降低本地部署前置条件，避免要求用户单独安装 .NET runtime 或手工准备 C++ dynamic libraries。

1. `tomur.exe` / `tomur` 自包含 .NET runtime。
2. llama.cpp、Whisper、PaddleOCR、stable-diffusion.cpp、llama.cpp TTS / GGUF TTS 等 C++ dynamic libraries 由 Tomur 发布产物携带。
3. RID 发布默认使用 `PublishSingleFile=true`、`SelfContained=true` 和 `IncludeNativeLibrariesForSelfExtract=true`。
4. `IncludeAllContentForSelfExtract` 默认保持 `false`，模型权重、SQLite 数据库、日志、用户文件和大体积 backend 资产不作为普通内容整体塞进可执行文件。
5. native backend 动态库由 Tomur 的 native bundle manifest 管理，并在首次运行或版本变化时准备到 Tomur 管理的版本化 runtime 目录。
6. runtime 目录由 Tomur 校验、更新和清理，不暴露成用户手工配置的前置步骤。
7. 缺少或损坏 native library 时，Tomur 必须返回可诊断错误，并在 UI 和 `tomur doctor` 中给出修复动作。
8. 托管 provider 通过 `Tomur.csproj` 项目引用静态纳入主程序；Native AOT 与非 AOT 发布使用各自构建包含的 provider 集合，不从外部目录动态加载任意托管程序集。

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
| 00 | R0-R11 | ✅ 已完成 | 项目门面、API、模型资产、本地推理、多模态、Agent、会话与 Web 工作台 |
| 01 | R12 | 🚧 进行中 | Native AOT / 自包含发布矩阵 |
| 02 | R13 | 🚧 进行中 | Web 前端能力聚合闭环 |
| 03 | R14 | 🚧 进行中 | Intel GPU / NPU 加速支持 |
| 04 | R15 | 🚧 进行中 | 纯 C# GLM / MoE 模型提供器实验 |
| 05 | R16 | 🚧 进行中 | Tool Calling 兼容协议与 Agent 自主编排闭环 |
| 06 | R17 | ⏳ 计划中 | Runtime 偏好、下载队列与 Settings 写入 |
| 07 | R18 | ⏳ 计划中 | 回归 smoke、发布证据与长期维护 |

已完成历史、验收边界和 smoke 记录入口见 [CHANGELOG.md](./CHANGELOG.md)。后续阶段不得把尚未接通或未经验证的 runtime 能力写成已实现。

### 01. 🚧 R12: Native AOT / 自包含发布矩阵

目标：让 Tomur 形成稳定发布体验。当前项目已确认 Windows x64 Native AOT 发布可通过且无警告；R12 后续重点从 AOT 清警告转向发布矩阵、native bundle 资产随包校验和服务形态 smoke。

当前边界：

1. `native-aot-audit.pubxml` 保持 `PublishAot=true`、`SelfContained=true`、`PublishSingleFile=true` 和 `SuppressTrimAnalysisWarnings=false`。
2. `self-contained-single-file.pubxml` 保留非 AOT 自包含单文件发布路径，作为兼容发布 profile。
3. RID 发布保持 `IncludeAllContentForSelfExtract=false`，避免把模型权重、SQLite 数据库、日志和用户文件写入程序二进制。
4. native runtime 仍由 bundle manifest 与 `tomur native prepare` 准备到 Tomur 数据目录下的版本化 runtime 缓存。

仍需推进：

1. Linux x64 Native AOT 发布日志与 smoke 记录。
2. macOS `osx-x64` / `osx-arm64` 自包含与 Native AOT 发布日志、native bundle prepare 和 smoke 记录。
3. Windows Service、Linux systemd、macOS launchd 与 Windows 托盘使用发布产物的实机 smoke。
4. 缺失或损坏 native 资产的 doctor / UI 修复记录。
5. 发布包最小回归执行记录，覆盖 `tomur --help`、`tomur doctor`、`tomur serve`、`GET /health`、`GET /api/version`、`GET /v1/models`、Web 静态托管和 native prepare。

验收：

1. 发布产物不要求用户安装 .NET runtime。
2. 发布产物携带当前 RID 必需 native libraries。
3. 首次运行能准备本地 runtime 目录。
4. AOT 警告逐项处理，不用 blanket suppression 掩盖。
5. AOT profile 保持完整 Tomur build surface，不通过删除公开能力绕过兼容性问题。
6. 非 AOT profile 保持自包含、单文件或近似单体体验，并使用同一套公开命令与 API。

### 02. 🚧 R13: Web 前端能力聚合闭环

目标：在保持 Chat-first 默认入口的前提下，把 Tomur 已具备的本地后端能力聚合到 Web 工作台中，让用户可以从 Chat、Settings 和状态抽屉进入模型、下载、runtime、Agent 工具、多模态、协议兼容、产物和本地文件能力。

不改变以下边界：

1. 不把首屏改造成管理后台或营销页。
2. 不把 Models、Downloads、Runtime、Files 提升为默认一级导航。
3. 不引入多租户、RBAC、SSO、远程团队治理或外部平台叙事。
4. 对会产生副作用的动作继续使用明确确认边界。
5. UI 只展示 Tomur 自身能力；未接通或后端不存在的动作必须显示诊断、路线或 CLI/API 入口。

仍需推进：

1. 多模态与产物呈现：补齐图像、音频、文本文件和生成产物的预览、打开、复制路径或下载入口。
2. 下载体验增强：在后端具备 Web 下载操作 API 前，继续提供推荐、命令和状态；后续补可视化下载队列、进度、失败重试和 proxy 配置。
3. Settings 写入增强：逐步补齐 API key 创建/撤销、server URL、默认 backend、proxy、GPU/offload 偏好等本地配置编辑能力。
4. 文件与本地检索入口：补齐附件目录、生成产物目录、文件索引状态和更完整 RAG 配置。
5. Chat 操作补齐：基础采样参数、history limit、TTS 参数、模型能力提示，以及附件/语音持久化回合替换能力。
6. 发布闭环：明确前端构建产物生成与 `app/wwwroot` 嵌入流程，避免发布包缺失 Web 静态资源。

验收：

1. 用户可以在 Web UI 中看到 Tomur 当前公开后端能力的完整地图。
2. 用户可以区分 OpenAI、Ollama、Claude Code、Conversations、Agent、Runtime 与多模态能力的已可用、缺模型、缺 native runtime、需确认和仅 CLI/API 可用状态。
3. 用户可以从 Chat 进入对应诊断或 Settings 分组。
4. 前端不虚标未实现动作；每个不可用状态都给出本地可执行的下一步。
5. Web UI 继续由 Tomur 本地 HTTP 服务托管，首屏仍是可直接使用的 Chat 工作台。

### 03. 🚧 R14: Intel GPU / NPU 加速支持

目标：在现有 llama.cpp / ggml dynamic backend 机制内支持 Intel GPU 与 Intel NPU，并保持 CPU fallback、可诊断加载和不伪造推理结果的边界。

支持策略：

1. Intel GPU 优先接入 llama.cpp `ggml-sycl` 与 `ggml-openvino` backend；`ggml-vulkan` 作为通用 GPU fallback。
2. Intel NPU 优先接入 llama.cpp `ggml-openvino` backend，并通过 `GGML_OPENVINO_DEVICE=NPU`、上下文限制、prefill chunk 和模型兼容诊断控制风险。
3. 不新增外部服务进程，不为 Intel 加速另建服务器产品，不把 OpenVINO 或 SYCL 运行时细节暴露成用户必须理解的前置概念。
4. 未下载模型、OpenVINO / SYCL runtime 不可用、驱动不可用、上下文过大或模型不兼容时，API、UI 和 `tomur doctor` 必须返回清晰诊断。

交付物：

1. ✅ 扩展 `tomur native build --backend`，支持 `vulkan`、`openvino`、`sycl` 与 `intel`。
2. ✅ 为 `llama.native` 补齐 `windows-x64-sycl` CMake preset，并安装 `ggml-sycl`。
3. ✅ 保持 `windows-x64-openvino` 与 `windows-x64-vulkan` 构建入口可由 CLI 直接触达。
4. ✅ 在 native bundle manifest 中明确 Intel backend 的可选库、variant、required backend 和诊断信息。
5. ✅ 在 runtime 配置中加入 accelerator 偏好字段：`auto|cpu|cuda|vulkan|sycl|openvino`、设备选择键、GPU layers、OpenVINO device 和 NPU prefill chunk。
6. ✅ 在 backend 初始化前设置受控环境变量，例如 `GGML_OPENVINO_DEVICE=GPU|GPU.0|GPU.1|NPU` 与 NPU 相关 prefill 设置。
7. ✅ 调整 accelerator 选择策略：Intel GPU 默认优先 `sycl/openvino`，Intel NPU 只在 OpenVINO backend 可用且用户允许时选中。
8. ✅ `tomur doctor`、`/api/runtime/status` 与 Web Runtime 面板显示 Intel GPU/NPU backend、OpenVINO / SYCL runtime、设备枚举、选中设备、fallback 原因、NPU 不适配诊断和修复提示。
9. 🚧 已建立 `docs/r14-intel-acceleration-smoke.md` 作为 Intel GPU 与 Intel NPU smoke 记录入口，覆盖 `/v1/chat/completions`、selected accelerator、GPU layers、token usage、错误诊断和 CPU fallback；真实实机记录仍需补入。

验收：

1. ✅ 缺少 Intel backend 动态库时，Tomur 继续使用 CPU，不影响文本 API。
2. ✅ Intel GPU backend 存在且设备可枚举时，文本模型可按选中 backend 请求 offload；真实推理通过仍以 smoke 记录为准。
3. ✅ Intel NPU backend 存在但模型或上下文不适配时，返回清晰诊断，不伪造推理结果。
4. 🚧 Intel GPU / NPU smoke 记录入口已建立并包含具体 backend、设备名、模型、上下文、token usage 和成功或失败证据字段；真实 GPU / NPU 实机证据仍需补充。
5. ✅ README、ROADMAP、CHANGELOG 和 runtime UI 口径都明确区分“backend 可见”“设备可枚举”“真实推理通过”三个状态。

### 04. 🚧 R15: 纯 C# GLM / MoE 模型提供器实验

目标：在保留 llama.cpp 等现有 native provider 的前提下，新增一个由 Tomur 自己实现的纯 C# 模型提供器，用于加载特定 GLM / MoE 模型格式并逐步接通本地文本生成。

GLM 基础代码顺序、性能计划、集中验证门槛与发布标准见 [providers/Glm/ROADMAP.md](./providers/Glm/ROADMAP.md)；OLMoE 小模型接入边界见 [providers/Olmoe/ROADMAP.md](./providers/Olmoe/ROADMAP.md)。

实现边界：

1. 提供器使用独立 C# 类库，程序集、命名空间、类型、provider ID、配置和诊断只按模型架构或能力命名，不使用参考项目名称。
2. 推理路径不得调用未声明的 native dynamic library；允许使用 `unsafe`、SIMD intrinsics、内存映射和随机访问文件 I/O。
3. provider 选择采用 extend-only 契约；现有 llama.cpp 文本与 embedding 路径保持默认行为，不修改兼容 API 的请求和响应形状。
4. 首批只支持明确标记的 GLM / MoE 模型目录，不把 safetensors 文件一概识别为可运行模型。
5. 模型配置不兼容、张量缺失、量化格式未知、内存不足、上下文超限或 forward 失败时返回结构化诊断，不伪造 token。
6. 托管 provider 使用独立类库与稳定契约项目，主程序通过项目引用静态注册；Native AOT 与非 AOT 的实际发布结果仍须分别验证。

基础代码进度：

1. ✅ 已建立 provider 契约与选择边界，并用现有 fallback 保持 llama.cpp 行为。
2. ✅ 已建立 `Tomur.Providers.Glm` 独立类库与中性 provider ID。
3. ✅ 已实现模型目录、配置、tokenizer 与 safetensors header/tensor index 的只读探测。
4. ✅ 已建立固定 seed 的 tiny F32 fixture、版本化 oracle、tensor manifest、SHA-256 校验与隐藏生成/校验入口。
5. ✅ 已建立统一 tensor descriptor、只读 shard 随机访问、resident/scratch 所有权、F32/F16/BF16 转换、int4/int8 量化视图与 expert slab。
6. ✅ 已实现 embedding、RMSNorm、LayerNorm、F32 matvec/matmul、int8/int4 解量化矩阵乘、activation int8 量化、基础激活与 elementwise 算子，以及稳定 top-k scalar reference kernels。
7. ✅ 已实现 tokenizer model/vocab/merge/added token 解析、GLM prompt template、多个 role/EOS stop token，以及保留 UTF-8 与文本 stop 尾部的增量解码。
8. ✅ 已实现 resident dense model 的精确 shape/dtype 校验、resident/KV/scratch 预预算、预算超限前置失败、取消/释放边界、session 诊断，以及 embedding、input RMSNorm 与 dense MLP scalar 基础路径。
9. ✅ 已实现 MLA q/kv projection、interleaved partial RoPE、reference/absorbed attention、单 token decode、多 token prefill、按层 compressed KV cache、上下文边界与失败回滚，并接入 M7 独立测试项目。
10. ✅ 已实现 MoE router、shared/routed expert 合并、按层固定容量 LRU、lease 隔离、RAM 配额、有界异步磁盘读取、取消和 cache/I/O 诊断，并接入 M8 独立测试项目。
11. ✅ 已接通有界批次完整 scalar forward、prompt prefill、compressed KV 增量 decode、greedy、temperature、top-k/top-p、penalty、多 EOS、文本 stop、context/cancellation 和增量 callback，并接入 M9 独立测试项目。
12. ✅ M10 集成基础代码已完成：显式标记的 packed rowwise safetensors 目录已接通 offset-binary int4、`*.qs` per-row scale、量化 resident 权重、GLM role token Chat、managed model readiness、兼容 API 可见性校验、OpenAI / Anthropic SSE、Ollama 增量 NDJSON、可取消 unload、结构化 session/resource 诊断以及三协议 streaming 回归测试代码。转换后的随机 tiny 模型已完成三类兼容 API 链路 smoke，证据见 [R15 packed GLM smoke 记录](./docs/r15-packed-glm-smoke.md)；M10 专项自动化测试当前为 `49/49` 通过，完整服务 smoke 仍归 M14。
13. 🚧 `glm4_moe_lite` 已作为 managed GLM 的显式兼容架构接入，真实候选 `cerebras/GLM-4.7-Flash-REAP-23B-A3B` 已在 Linux 验证机完成转换、加载、readiness、最短非流式 completion 和一次 Web Chat 非流式真实对话。P0 将生产 MLA 默认切换到 Absorbed 后，同一 1-token 请求从 `186.596971s` 降至 `26.595764s` 并返回相同 token；一次活动请求经 unload 取消后返回结构化 `session_unloaded`。完整 Chat、streaming、Anthropic、自然语言质量、持续吞吐、缓存、重复取消/unload 和跨平台矩阵仍待完成。
14. 🚧 独立 `managed-olmoe` provider 已接通标准 causal attention、q/k RMSNorm、softmax top-k router、BF16 与 rowwise int8 experts、官方 chat template 和生成链路；O4 已补齐 tiny scalar oracle、错误/资源/内存边界。O5 已增加有界原子 BF16/F16/F32 expert 转换、输入/产物 SHA-256 清单、三协议非流式与 streaming 回归代码，以及加载/首 token/output token/s/decode token/s session 诊断。原始 BF16 `allenai/OLMoE-1B-7B-0125-Instruct` 已通过 Catalog、provider load 与中文非流式真实对话；完整 rowwise int8 产物已在 Linux 服务器通过 checksum、probe、readiness、专项 33/33 回归以及 Tomur Chat/OpenAI 非流式真实 forward。streaming、Anthropic、完整性能与 unload 矩阵仍待按 [O5 验证记录](./docs/r15-olmoe-o5-validation.md) 执行。
15. ✅ M11 性能优化基础代码已完成：managed GLM 已增加可回退 scalar 的 SIMD/shape-aware F32、int8、int4 matvec，gate/up paired dispatch，RAM budget 自动 cache capacity，usage histogram hot pin、显式 expert prefetch、cache 热路径降分配、forward 阶段 timing、activation integer dot 评估、prefill batch expert union 和可切换 mmap I/O 实验边界。全部性能基准、allocation 与跨平台验证仍归 M14；本轮未执行构建或测试。
16. ✅ M12 高级能力基础代码已完成：managed GLM 已增加 DSA/MTP 配置与 tensor probe、接收 indexer score 的稳定 DSA top-k selection、dense-equivalent runtime 路径、可选 MTP resident head 与单步 draft、speculative rejection sampling、grammar forced spans、router lookahead prefetch、live expert repin，以及带模型身份、维度检查和 SHA-256 的 compressed KV 快照/恢复与 isolated KV fork。未验证的稀疏 DSA 不使用 attention score 冒充 indexer score；M12 独立测试代码已接入 solution，真实 indexer/MTP 语义、采样分布、性能、完整模型和跨平台验证仍归 M14，本轮未执行构建或测试。
17. ✅ M13 发布与兼容基础代码已完成：`providers/Abstractions` 承载稳定契约，`Tomur.csproj` 直接引用 GLM 与 OLMoE provider，`ModelProviderRegistry` 在进程启动时静态注册批准的 provider；不再使用发布后复制、外部目录扫描或反射激活。win-x64 `native-aot-audit` 发布已通过；M13 专项测试、非 AOT 自包含发布和跨平台实机 smoke 仍归 M14。
18. ⏳ llama.cpp 文本路径仍由 `SessionManager` 特殊 fallback 驱动；后续将通过 `LlamaCppProvider` 接入同一 provider 契约，在保持 Agent、兼容 API、embedding、硬件加速和 native 诊断行为不变的前提下统一文本 provider 路由。该项尚未实现，不得描述为当前行为。

#### ⏳ 统一 provider 路由计划

实现范围：

1. 新增 `LlamaCppProvider`，实现 `ITextGenerationProvider`；其 session 适配现有 `LlamaNativeSession`，继续使用 `LlamaBackendInitializer`、`HardwareAccelerationService` 和 `app/Native` 的 bundle/动态库基础设施。
2. llama.cpp Chat 路径实现 `IChatGenerationSession`，保持当前 `LlamaPromptBuilder` 的 prompt 模板、stop sequence、context 截断和原始消息角色语义，不因统一接口改变生成输入。
3. provider 注册从无依赖的直接构造扩展为受控工厂或依赖注入注册，使 llama.cpp provider 可以获得 backend initializer、accelerator selection 和 logger；GLM 与 OLMoE 的静态项目引用保持不变。
4. 为 embedding 增加独立、可选的 extend-only 契约，例如 `IEmbeddingProvider` 与 embedding session；不把 embedding 方法强行加入所有文本 provider 必须实现的接口。
5. 统一 `SessionManager` 的文本 session 所有权、切换、取消、unload、snapshot 和错误记录，同时保留 GPU layers、selected accelerator、native backend 初始化和 CPU fallback 诊断。
6. `LocalInferenceService`、`LocalChatClient`、Agent Framework ChatClient、OpenAI、Ollama 与 Anthropic Messages 入口继续使用同一模型选择和 session 管理链路；Agent 工具结果总结与工作流总结纳入文本回归。
7. `image.generate`、VLM、OCR、Whisper ASR 与 TTS 继续由 `IsolatedImageGenerationService`、`MultimodalExecutionService` 和各自 native runtime 处理，不纳入 `LlamaCppProvider` 文本契约；文件搜索、SQLite、下载和 runtime repair 同样保持独立。
8. 只有文本协议、Agent、embedding、session 生命周期、native 诊断和 multimodal 隔离回归全部通过后，才删除 `SessionManager` 中现有的 llama.cpp 特殊 fallback。

验收：

1. GGUF Chat/Completion 通过 `LlamaCppProvider` 后，OpenAI、Ollama 与 Anthropic Messages 的非流式、streaming、token usage、stop reason 和结构化错误形状保持兼容。
2. Agent 普通对话、工具前后文本响应、工具结果总结和 workflow summary 继续使用本地模型；受控工具确认、审计和调用边界不因 provider 重构改变。
3. GGUF embedding 通过可选 embedding 契约返回与现有路径一致的向量维度、usage、取消和模型能力诊断；不支持 embedding 的 provider 不需要实现该能力。
4. session 在 managed provider 与 llama.cpp 之间切换时正确释放模型、KV、expert cache、native handle 和 pooled buffer；取消、unload、服务重启与并发拒绝行为保持可重复。
5. accelerator selection、GPU layers、OpenVINO/SYCL/Vulkan/CUDA 可见性、CPU fallback、native bundle 缺失和模型不兼容继续通过 `tomur doctor`、Runtime API 与 Web Runtime 返回一致诊断。
6. stable-diffusion.cpp 绘图、llama.cpp mtmd VLM、OCR、Whisper 与 TTS 的 API 和 Agent 工具回归通过；文本 provider 加载失败不得阻断这些独立 multimodal runtime，multimodal 失败也不得污染文本 provider 状态。
7. Native AOT 与非 AOT 构建均保留静态 provider 注册，逐项处理 trimming/AOT analyzer 警告，不使用 blanket suppression。

集中验证进度：

1. ✅ 转换后的 tiny packed GLM 已在 Windows 完成 Catalog、OpenAI 非流式/SSE、Ollama Chat 与 Anthropic Messages；当前静态 provider 项目引用结构已完成完整 solution 128/128 回归，并由隔离 `tomur doctor` 确认 GLM 与 OLMoE 均已注册。该证据不替代完整模型协议与性能矩阵。
2. ✅ 完整 GLM-4.7 已完成源资产校验、转换、Catalog/readiness、Reference/Absorbed 对照、最短真实 completion、一次 Web Chat 非流式真实对话和一次活动请求 unload 取消；M7/M9 定向回归已在 Windows 与 Linux 通过。
3. ✅ OLMoE 已完成 Windows BF16 中文非流式真实对话，以及 Linux 专项构建、33/33 自动化测试、完整 int8 转换、checksum/probe/readiness、Tomur Chat 与 OpenAI 非流式真实 forward。
4. 🚧 GLM 与 OLMoE 的 streaming、Anthropic 真实模型请求、完整性能、重复取消/unload、资源释放和跨平台矩阵仍归 M14；tiny fixture 或单次请求证据不得替代这些矩阵。
5. ⏳ 完整 GLM-5.2 固定清单为 `383,760,077,466` bytes（357.4 GiB，约 384 GB）；Linux 验证机当前已出现 150 个正式文件且没有 `.part`，但主下载状态失败，最终 inventory、size 与 SHA-256 审计尚未完成，不得据此宣称模型可用或真实对话通过。
6. 🚧 M14 完成前，managed GLM provider 继续保持实验状态；已经通过的真实对话可以作为 smoke 证据，但不等同于完整可用性验收。

### 05. 🚧 R16: Tool Calling 兼容协议与 Agent 自主编排闭环

目标：让本地模型能够根据函数声明自主选择工具，并在 OpenAI、Ollama 与 Tomur Agent 三条入口中保持可关联、可回灌、可流式表达的多轮调用协议。

协议边界：

1. `POST /v1/chat/completions` 接收 `tools`、`tool_choice`、`parallel_tool_calls`、assistant `tool_calls` 与 `tool_call_id`，返回 `finish_reason=tool_calls` 和可聚合的 SSE `delta.tool_calls`；工具由兼容客户端执行并回灌。
2. `POST /api/chat` 接收 Ollama function tools、assistant `tool_calls`，以及通过 `tool_call_id` 或兼容 `tool_name` 关联的工具结果，返回调用 ID、对象参数、streaming tool call chunk、`done_reason` 和终帧统计；工具同样由兼容客户端执行并回灌。
3. `POST /api/agents/chat` 增加模型选择工具的服务端循环，使用调用 ID、结构化参数和工具结果逐轮继续推理，并保留最大轮次、取消、事件日志和 telemetry。
4. 只读工具可以在模型自主模式下执行；写文件、生成产物、runtime 修复或其他副作用工具继续要求显式 allowlist、`confirm=true` 与预批准参数完全匹配，模型输出本身不构成授权。
5. provider 契约继续采用 extend-only 设计。旧文本 provider、旧请求与无工具 Chat 行为保持不变；不把工具字段静默丢弃或伪造成普通文本能力。

当前进度：

1. ✅ provider-neutral 工具声明、调用、结果与 finish reason 表达，以及本地模型 JSON tool-call codec 基础代码已完成。
2. ✅ Microsoft.Extensions.AI `FunctionCallContent` / `FunctionResultContent` 与有界 `FunctionInvokingChatClient` 循环基础代码已完成。
3. ✅ OpenAI / Ollama 非流式、缓冲式 streaming、调用 ID 和历史回灌 wire contract 基础代码已完成；当前 streaming 在完整推理和协议解析后发送可聚合帧，不是逐 token 参数增量。
4. ✅ M10 专项 `49/49` 自动化测试与 win-x64 `native-aot-audit` 发布已通过；source-generated JSON 工具契约已进入该构建面。
5. ⏳ 真实 GGUF、managed GLM 与 OLMoE 的单调用、并行调用、多轮结果回灌、取消和轮次上限 smoke，以及完整 Agent 工具循环、请求取消后的副作用审计和并发事件日志验证尚待执行。

验收：

1. 单工具、多个工具、工具结果回灌和最终文本回答在三条入口中均保持调用 ID、名称、参数与顺序。
2. OpenAI SSE 与 Ollama NDJSON 的工具调用可由标准客户端重组，无工具请求的现有文本响应形状不变。
3. 未声明工具、非法 arguments、工具失败、确认拒绝、取消和轮次超限返回对应协议风格的明确诊断。
4. source-generated JSON、Native AOT 和旧 provider 契约回归通过，不启用反射序列化兜底。

### 06. ⏳ R17: Runtime 偏好、下载队列与 Settings 写入

目标：把当前以诊断和 CLI 为主的运行时控制，逐步收敛为可在 Web UI 中安全编辑和确认的本地设置。

计划范围：

1. Settings 写入 API：API key 创建/撤销、server URL、默认 backend、proxy、GPU/offload 偏好。
2. 下载队列 API：模型包选择、进度、暂停/恢复、失败重试、checksum 结果和 license 提示。
3. 模型管理：本地模型删除、manifest 修复、可见性刷新和模型能力提示。
4. Runtime 操作：session unload、native prepare、backend 选择和修复动作统一确认。
5. 文件与检索配置：附件目录、生成产物目录、文件索引状态和本地 RAG 配置。

验收：

1. 所有写入动作都能在执行前显示影响范围和目标路径。
2. 失败时保留结构化诊断，并给出 CLI/API 等价操作。
3. Web UI 不绕过 Tomur 本地配置文件、模型 manifest 和 runtime 诊断状态。
4. 不引入多租户、后台管理壳或企业治理概念。

### 07. ⏳ R18: 回归 smoke、发布证据与长期维护

目标：把已经接通的 API、native runtime、Web UI、服务模式和发布形态收敛成可重复维护的回归证据。

计划范围：

1. 维护 R8-R11 小模型/小素材 smoke 套件，保留模型、接口、耗时、诊断和 WAV/PNG 产物证据。
2. 在用户明确要求验证时执行 Tomur 项目构建、启动和真实 GGUF chat / embedding smoke。
3. 补齐 Windows CUDA13、Intel GPU、Intel NPU 的真实 chat smoke。
4. 补充 Windows Service、Linux systemd、macOS launchd 和 Windows 托盘实机 smoke 验收记录。
5. 补齐 macOS `osx-x64` / `osx-arm64` native runtime bundle 资产。
6. 为 R10/R11 补构建/启动 smoke，并按 `docs/r10-r11-smoke-maintenance.md` 维护 Web 录音入口、播放控制、失败诊断展示和会话历史同步的回归清单。
7. 按 `docs/r12-aot-release-audit.md` 补齐 R12 Linux/macOS 发布执行记录、服务形态实机 smoke 和发布包最小回归证据。

验收：

1. 每个公开能力都有对应的 smoke 入口或明确的未验证记录。
2. 发布包验证覆盖 CLI、API、Web 静态托管、native prepare、模型可见性和 runtime 诊断。
3. 失败证据与成功证据同样保留，便于 UI 和 doctor 给出准确修复动作。
