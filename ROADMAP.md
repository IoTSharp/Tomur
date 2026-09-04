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
    plate.native/
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
2. llama.cpp、Whisper、PaddleOCR、HyperLPR3/MNN 车牌识别桥接、stable-diffusion.cpp、llama.cpp TTS / GGUF TTS 等 C++ dynamic libraries 由 Tomur 发布产物携带；车牌模型权重仍作为独立本地资产管理。
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
| 02 | R14 | 🚧 进行中 | Intel GPU / NPU 加速支持 |
| 03 | R15 | 🚧 进行中 | 纯 C# GLM / MoE 模型提供器实验 |
| 04 | R16 | 🚧 进行中 | Tool Calling 兼容协议与 Agent 自主编排闭环 |
| 05 | R17 | ⏳ 计划中 | Runtime 偏好、下载队列与 Settings 写入 |
| 06 | R18 | ⏳ 计划中 | 回归 smoke、发布证据与长期维护 |
| 07 | R19 | ⏳ 计划中 | TomurLPR 纯 C# 车牌识别提供器 |
| 08 | R20 | 🚧 进行中 | Realtime 双向语音与会话网关 |

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

### 02. 🚧 R14: Intel GPU / NPU 加速支持

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

### 03. 🚧 R15: 纯 C# GLM / MoE 模型提供器实验

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

### 04. 🚧 R16: Tool Calling 兼容协议与 Agent 自主编排闭环

目标：让本地模型能够根据函数声明自主选择工具，并在 OpenAI、Ollama 与 Tomur Agent 三条入口中保持可关联、可回灌、可流式表达的多轮调用协议。

协议边界：

1. `POST /v1/chat/completions` 接收 `tools`、`tool_choice`、`parallel_tool_calls`、assistant `tool_calls` 与 `tool_call_id`，返回 `finish_reason=tool_calls` 和可聚合的 SSE `delta.tool_calls`；工具由兼容客户端执行并回灌。
2. `POST /api/chat` 接收 Ollama function tools、assistant `tool_calls`，以及通过 `tool_call_id` 或兼容 `tool_name` 关联的工具结果，返回调用 ID、对象参数、streaming tool call chunk、`done_reason` 和终帧统计；工具同样由兼容客户端执行并回灌。
3. `POST /api/agents/chat` 增加模型选择工具的服务端循环，使用调用 ID、结构化参数和工具结果逐轮继续推理，并保留最大轮次、取消、事件日志和 telemetry。
4. 只读工具可以在模型自主模式下执行；写文件、生成产物、runtime 修复或其他副作用工具继续要求显式 allowlist、`confirm=true` 与预批准参数完全匹配，模型输出本身不构成授权。
5. provider 契约继续采用 extend-only 设计。旧文本 provider、旧请求与无工具 Chat 行为保持不变；不把工具字段静默丢弃或伪造成普通文本能力。

当前进度：

1. ✅ provider-neutral 工具声明、调用、结果与 finish reason 表达，以及本地模型 JSON tool-call codec 基础代码已完成。
2. ✅ Microsoft.Extensions.AI `FunctionCallContent` / `FunctionResultContent` 与有界 `FunctionInvokingChatClient` 循环基础代码已完成；依赖已升级到 Microsoft Agent Framework `1.19.0` 与 Microsoft.Extensions.AI `10.9.0`，运行时状态会报告已接入的审批绑定、流式 Agent、workflow composition 和 usage aggregation 能力面。
3. ✅ OpenAI / Ollama 非流式、缓冲式 streaming、调用 ID、历史回灌和同轮多个调用 wire contract 基础代码已完成；当前 streaming 在完整推理和协议解析后发送可聚合帧，不是逐 token 参数增量，Ollama 工具终帧使用 `done_reason=tool_calls`。
4. ✅ M10 专项 `49/49` 自动化测试与 win-x64 `native-aot-audit` 发布已通过；source-generated JSON 工具契约已进入该构建面。1.19.0 包升级后的构建与测试回归尚未执行。
5. ⏳ 真实 GGUF、managed GLM 与 OLMoE 的单调用、并行调用、多轮结果回灌、取消和轮次上限 smoke，以及完整 Agent 工具循环、请求取消后的副作用审计和并发事件日志验证尚待执行。
6. 🚧 `plate.recognize` 只读工具已接入 controlled 调用边界、source-generated JSON、`tomur-plate` C ABI 与 native bundle manifest；M10 `70/70`、M13 `25/25` 自动化测试已通过，HyperLPR3/MNN/OpenCV 的 Linux ARM64 CPU 构建已完成 ELF、C ABI 与依赖闭包校验。六个 `r2_mobile` 模型资产、真实图片识别和 Linux ARM64 目标机 smoke 尚未验证。

验收：

1. 单工具、多个工具、工具结果回灌和最终文本回答在三条入口中均保持调用 ID、名称、参数与顺序。
2. OpenAI SSE 与 Ollama NDJSON 的工具调用可由标准客户端重组，无工具请求的现有文本响应形状不变。
3. 未声明工具、非法 arguments、工具失败、确认拒绝、取消和轮次超限返回对应协议风格的明确诊断。
4. source-generated JSON、Native AOT 和旧 provider 契约回归通过，不启用反射序列化兜底。

### 05. ⏳ R17: Runtime 偏好、下载队列与 Settings 写入

目标：把当前以诊断和 CLI 为主的运行时控制，逐步收敛为可在 Web UI 中安全编辑和确认的本地设置。

计划范围：

当前已接入基础：`POST /api/runtime/session/load` 可按已安装模型 ID 和上下文长度显式加载唯一文本 session；它与既有 status/unload API 共用 `SessionManager` 生命周期，加载新模型会释放旧模型。该接口已完成项目构建验证，真实 CUDA 模型加载、并发取消和显存释放仍归 R18 smoke。

1. Settings 写入 API：API key 创建/撤销、server URL、默认 backend、proxy、GPU/offload 偏好。
2. 下载队列 API：模型包选择、进度、暂停/恢复、失败重试、checksum 结果和 license 提示。
3. 模型管理：本地模型删除、manifest 修复、可见性刷新和模型能力提示。
4. Runtime 操作：session load/unload、native prepare、backend 选择和修复动作统一确认。
5. 文件与检索配置：附件目录、生成产物目录、文件索引状态和本地 RAG 配置。

验收：

1. 所有写入动作都能在执行前显示影响范围和目标路径。
2. 失败时保留结构化诊断，并给出 CLI/API 等价操作。
3. Web UI 不绕过 Tomur 本地配置文件、模型 manifest 和 runtime 诊断状态。
4. 不引入多租户、后台管理壳或企业治理概念。

### 06. ⏳ R18: 回归 smoke、发布证据与长期维护

目标：把已经接通的 API、native runtime、Web UI、服务模式和发布形态收敛成可重复维护的回归证据。

计划范围：

1. 维护 R8-R11 小模型/小素材 smoke 套件，保留模型、接口、耗时、诊断和 WAV/PNG 产物证据。
2. 在用户明确要求验证时执行 Tomur 项目构建、启动和真实 GGUF chat / embedding smoke。
3. 补齐 Windows CUDA13、Intel GPU、Intel NPU 的真实 chat smoke。
4. 补充 Windows Service、Linux systemd、macOS launchd 和 Windows 托盘实机 smoke 验收记录。
5. 补齐 macOS `osx-x64` / `osx-arm64` native runtime bundle 资产。
6. 为 R10/R11 补构建/启动 smoke，并按 `docs/r10-r11-smoke-maintenance.md` 维护 Web 录音入口、播放控制、失败诊断展示和会话历史同步的回归清单。
7. 按 `docs/r12-aot-release-audit.md` 补齐 R12 Linux/macOS 发布执行记录、服务形态实机 smoke 和发布包最小回归证据。
8. 为 `plate.recognize` 补齐 win-x64、linux-x64 与 linux-arm64 原生构建，使用真实车头、车侧和车尾大图验证候选、业务色码、置信度、空结果和损坏图片诊断。

验收：

1. 每个公开能力都有对应的 smoke 入口或明确的未验证记录。
2. 发布包验证覆盖 CLI、API、Web 静态托管、native prepare、模型可见性和 runtime 诊断。
3. 失败证据与成功证据同样保留，便于 UI 和 doctor 给出准确修复动作。

### 07. ⏳ R19: TomurLPR 纯 C# 车牌识别提供器

目标：在保留现有 HyperLPR3/MNN/OpenCV native 车牌识别路径的前提下，建立名为 TomurLPR 的独立纯 C# 类库，以受限托管推理运行时完成车牌检测、对齐、分类和文字识别，并通过静态项目引用接入 Tomur 单进程宿主。

当前基线：

1. `plate.recognize` 已通过 `tomur-plate` C ABI 接入 HyperLPR3 3.0、MNN 2.2.0 与 OpenCV 4.12.0；当前 native 路径仍是默认实现和托管实现的正确性 oracle。
2. HyperLPR3 上游源码固定为提交 `9307450f7b7915be18f23a539ec05b41fe6629f4`，六个 `r2_mobile` 模型包括 320/640 检测 backbone/head、96×96 分类模型和 160×48 识别模型。
3. native 代码契约测试和 Linux ARM64 构建证据已经存在，但真实车头、车侧、车尾大图识别与目标机 smoke 仍按 R18 计划待执行。
4. HyperLPR3 源码使用 Apache-2.0；模型权重、训练数据来源和转换后资产的使用与再分发仍需单独审核。审核完成前不得把模型写入 TomurLPR NuGet 包、Tomur 可执行文件或 native bundle。
5. 当前尚未建立 TomurLPR 项目、托管车牌推理内核或真实图片托管推理证据；R19 全部能力保持计划状态。

产品与工程边界：

1. TomurLPR 是对外库名；仓内第一阶段放在 `providers/PlateRecognition/` 的独立 C# 类库中。普通类型使用 `PlateRecognizer`、`PlateResult` 等能力名称，provider ID、配置键和诊断代码使用 `managed-plate` 等中性能力名称。
2. TomurLPR 不建立独立服务进程、HTTP API、管理界面或模型目录。`app/Tomur.csproj` 通过项目引用静态包含它，现有 `app/PlateRecognition/` 负责宿主适配、provider 选择、工具结果规范化和运行时诊断。
3. 托管核心不得依赖 MNN、OpenCV、ONNX Runtime、TorchSharp、SkiaSharp 或其他第三方 native dynamic library，也不得在内部以未声明的 P/Invoke 回退到 native 实现。
4. R19 只实现固定车牌模型所需的受限模型格式、计算图和算子集合，不开发通用 MNN 兼容运行时。未知算子、shape、layout、精度或模型版本必须拒绝加载并给出可诊断错误。
5. 核心识别 API 接收带宽、高、stride 和像素格式的已解码像素缓冲区；图片文件或 data URI 解码通过独立受控适配层完成，避免把平台图像栈耦合进推理核心。
6. 模型权重继续作为 Tomur 数据目录下的独立资产管理，使用 manifest、格式版本、来源、license 提示和 SHA-256 校验；权重、测试图片和转换产物不嵌入程序集。
7. native 与 managed provider 必须显式可选并分别诊断。在全部验收门槛通过前，native 保持默认，managed 保持实验状态；不得静默切换实现或把 fallback 结果描述成目标 provider 的推理结果。
8. 第一阶段不拆分独立 Git 仓库。只有在公开 API 与模型格式达到 v1 稳定、许可证和发布流程闭环，并出现明确的 Tomur 外部使用需求后，才评估拆仓和独立 NuGet 发布。

#### P0. ⏳ 基线、许可与算子清单

1. 固定上游源码、六个模型文件名、长度与 SHA-256，记录 native 编译版本、输入规范、输出 tensor 名称和后处理参数。
2. 使用 MNN 模型检查工具导出每个图的算子、shape、layout、dtype、常量和中间 tensor 清单，形成 TomurLPR 支持矩阵；不得根据网络名称猜测算子范围。
3. 复用并补齐 R18 的 native 真实图片 smoke，在合法可用的图片集合上记录候选框、关键点、层数、颜色、文字、置信度、耗时和中间 tensor，建立可重复 oracle。
4. 审核 HyperLPR3 代码、模型权重、训练数据说明、测试图片和转换产物的许可与归属；无法确认可再分发的资产仅允许用户自行提供，不进入 Catalog 默认下载与发布包。
5. 根据算子数量、动态 shape、数值精度和资产许可形成 go/no-go 记录。若需要实现通用 MNN、依赖未批准 native runtime 或无法建立合法验收集，则停止进入 P1 并重新评估模型路线。

#### P1. ⏳ 托管核心与确定性标量基线

1. 建立 `net10.0`、nullable、unsafe、AOT analyzer 开启的独立类库和专项测试项目，不提供外部进程或反射式插件加载。
2. 定义像素缓冲区、tensor descriptor、模型 manifest、受限计算图、内存计划、取消、线程安全和资源上限契约。
3. 先实现可读、确定性的标量算子，每个算子用独立向量和 native 中间 tensor 对照；在正确性稳定前不引入 SIMD、融合算子或并行调度。
4. 模型加载对文件长度、checksum、格式版本、tensor 范围、shape 乘法溢出、最大内存和未知算子执行完整校验，失败时不创建部分可用 session。

#### P2. ⏳ 裁剪车牌文字识别纵向闭环

1. 先接通 160×48 识别模型、归一化、20×78 输出和 CTC 去重/空白 token 解码，输入限定为已经裁剪并对齐的单层车牌像素。
2. 对每层输出建立标量实现与 native oracle 的误差记录，并验证中文省份、字母、数字、特殊字符、空结果和低置信度路径。
3. 补齐损坏模型、错误像素格式、异常 stride、超大输入、取消和重复加载/释放测试；本阶段不宣称完成端到端车牌识别。

#### P3. ⏳ 检测、分类、对齐与端到端闭环

1. 接通 96×96 颜色分类模型，保持业务颜色码、车牌类型和现有 `PlateRecognitionCandidate` 公开结果语义不变。
2. 接通 320/640 检测 backbone/head、`6300/25200 × 15` 解码、置信度过滤、NMS、四点关键点和坐标缩放。
3. 实现 resize、padding、crop、四点透视对齐和双层车牌拆分，分别验证边界像素、退化四边形和越界候选。
4. 通过宿主适配器接入现有 `plate.recognize`，保持参数、结果 JSON、工具权限、错误形状和 source-generated JSON 契约兼容。

#### P4. ⏳ 性能、发布与默认路径评估

1. 在标量正确性基线之上按 profiler 结果引入 `Span<T>`、池化缓冲区、内存映射、`System.Numerics` 和 `System.Runtime.Intrinsics`；不以关闭边界检查换取性能。
2. 记录模型加载、首张图片、稳态延迟、吞吐、峰值 RSS、分配量和取消响应，并与同机同素材的 native CPU oracle 比较后确定发布预算。
3. 为 managed provider 增加 Catalog、doctor、Runtime API/UI 的格式匹配、资产缺失、checksum、未知算子、内存不足和 provider 选择诊断。
4. 在用户明确要求验证时执行专项测试、完整 solution 回归、非 AOT 自包含发布、Native AOT 发布和 win-x64、linux-x64、linux-arm64 真实图片 smoke。
5. 只有在正确性、性能、资产许可、跨平台发布和回退诊断全部通过后，才评估将 managed 设为默认；native 路径继续保留为并行选择。

工作量判断：P0 预计 1–2 周，P1 预计 2–3 周，P2 预计 4–8 周，P3 预计 6–10 周，P4 预计 4–8 周。单人完成 CPU 版本预计 4–7 个月，达到可发布的精度、性能和跨平台质量预计 6–9 个月；P0 算子清单和许可审查完成后必须重新估算，该估算不构成能力完成声明。

验收：

1. 代码、模型、测试素材和转换产物的许可边界分别记录；未批准资产不会被打包、自动下载或描述为可分发。
2. 运行时依赖闭包确认托管核心不携带或加载第三方 native dynamic library，源码中不存在未声明的 P/Invoke/native fallback。
3. 所有受支持算子都有独立数值测试和至少一个真实模型中间 tensor 对照；未知或不兼容图不会进入执行阶段。
4. 开发 parity 集不少于 500 张合法图片，独立发布验收集不少于 5,000 张，覆盖无车牌、模糊、夜间、倾斜、远距离、单双层和主要业务颜色。
5. 在同一验收集上，文字完全匹配率、检测召回率、误检率和颜色分类准确率相对固定 native oracle 的绝对差不超过 1 个百分点；特殊车牌类别单独报告，不用总体指标掩盖退化。
6. 损坏图片、损坏模型、checksum 不匹配、未知算子、越界 shape、内存上限、取消和并发冲突均返回稳定诊断，不崩溃、不伪造候选。
7. 专项测试、Tomur 完整回归、非 AOT 自包含和 Native AOT 发布逐项通过且无未解释的 trimming/AOT warning；每个平台的真实图片结果与构建成功分开记录。
8. 性能预算由 P0/P4 的同机 native 基线确定；未达到预算时 managed 保持实验可选状态，不以降低精度、跳过模型阶段或静默回退通过验收。

### 08. 🚧 R20: Realtime 双向语音与会话网关

目标：在保留现有文件级 ASR、整段 TTS、单回合语音 API 和按钮式录音路径的前提下，为 Tomur 建立本地优先、可取消、有界并可诊断的 Realtime 双向语音链路，形成“持续采集 -> VAD 端点检测 -> 增量 ASR -> 本地文本模型 -> 增量 TTS -> 连续播放”的会话能力，并支持用户在助手播报期间插话和打断。

当前基线：

1. `POST /v1/audio/transcriptions`、`POST /v1/audio/speech` 与 `POST /api/conversations/{conversationId}/voice-turns` 已提供文件级 ASR、整段 WAV TTS 和顺序式单回合语音处理；这些接口继续作为批处理兼容面和非 Realtime fallback。
2. Web 工作台已经提供按钮式录音、16 kHz mono PCM WAV 转换、语音回合提交和 TTS 产物播放，但当前必须停止录音后整段上传，并等待 ASR、文本生成和 TTS 全部完成，不是持续双向流。
3. 默认 Whisper bundle 已包含 Silero VAD sidecar 资产，但应用层尚无 VAD session、native bridge、speech start/stop 事件或独立 readiness 诊断；资产存在不代表 VAD 已接入。
4. Whisper 当前每次请求创建 context 并执行一次阻塞式 `whisper_full`；TTS 当前每次请求重新加载 acoustic 与 WavTokenizer 模型并在完整合成后一次返回 PCM，二者都没有 Realtime 所需的常驻 session、增量结果和 native 取消闭环。
5. R8 真实模型 smoke 只证明批处理公开接口可执行，其中 ASR 记录为 `30016 ms`、TTS 记录为 `9574 ms`；该证据不代表实时延迟达标，见 [R8 Multimodal Smoke Report](./docs/r8-smoke-report.md)。
6. 文本生成已有 token callback、请求取消、会话消息与产物持久化，可以作为 Realtime 文本阶段的基础；当前全局文本 session 仍由单执行门串行化，尚无独立 Realtime 资源协调、增量语音推理、输出音频背压或 barge-in 执行链。
7. `tomur.realtime.v1` WebSocket 网关、一次性 ticket、单活跃 session、有界队列和手动 commit 输入缓冲已经接入，但尚未执行构建、协议或真实设备 smoke；AudioWorklet、VAD、ASR partial transcript、TTS audio delta、连续播放缓冲和断线恢复仍未实现。

产品与工程边界：

1. R20 首先实现 ASR -> 文本模型 -> TTS 的级联式本地语音链路，目标是连续监听、增量转写、增量播报和自然打断等交互行为；直接 speech-to-speech 模型及其语义、韵律和情绪能力不属于 R20 完成门槛，也不得把级联链路描述为直接音频模型。
2. Realtime gateway、session 协调、协议适配、运行时诊断和 Web 静态资源继续由 `Tomur.csproj` 承载；不新增外部语音服务进程、另一套服务器产品或必须联网的中转服务。
3. 首版传输采用本地 WebSocket。P0 冻结 Tomur 原生路由、协议版本和事件矩阵；控制事件使用 JSON，音频优先使用带固定二进制帧头的 binary frame。OpenAI Realtime 风格兼容作为原生 session engine 上的适配层单独验收，未完成事件矩阵前不得宣称协议兼容。
4. WebSocket 连接建立流程必须验证 Host、协议版本、身份凭据和连接配额；升级阶段至少完成 Host、协议与连接配额校验，浏览器工作台还必须验证 Origin，且即使监听 loopback 也不得依赖 CORS 防止跨站 WebSocket 劫持。浏览器使用 SameSite/HttpOnly 同源 cookie，或先经同源 HTTP 请求获取短期一次性 ticket 并在升级后的首个控制事件完成认证；认证完成前只允许认证/关闭事件且不得分配 native 资源。非浏览器客户端缺少 Origin 时进入独立规则，必须提供有效 Bearer 凭据或一次性 ticket。非 loopback 暴露必须使用明确启用的鉴权与安全传输策略，凭据、ticket、session token 和重连 token 都不写入 URL query 或日志。
5. WebRTC 不作为本地 MVP 前置条件。只有在跨设备、弱网、NAT 穿透或远程媒体质量需求形成独立证据后再评估接入，且不得替换本地 WebSocket fallback。
6. 浏览器实时采集使用 AudioWorklet 或等价的固定帧音频管线，输入基线为 signed PCM16 little-endian、16 kHz mono、20 ms/frame；网络输出基线为 signed PCM16 little-endian、24 kHz mono 分片，并在浏览器端显式重采样到 AudioContext 的 44.1/48 kHz 等实际设备采样率。固定二进制帧头至少携带 frame kind、stream/response identifier、sequence、单调采集或播放时间戳和 payload length；输入 capture stream/utterance sequence 与输出 response epoch 分离，并定义 gap、设备时钟漂移、重复、乱序和重连后的序号恢复语义。
7. 每个连接、session、turn、输入 frame、输出 chunk、客户端发送缓存、服务端输入/输出队列、控制事件队列、单次语音时长、空闲时间、会话总时长和重连次数都必须有数值上限与超时。P0 必须为每类队列确定 overflow 策略；输入语音缺口、输出音频欠载或控制事件丢失不得被静默忽略。
8. 首个发布版本默认限制单进程一个活跃 Realtime 会话；资源不足或已有活跃会话时返回稳定的 busy / resource diagnostic，不排队伪装成实时响应。并发能力只能在资源调度和延迟矩阵通过后扩展。
9. VAD、ASR 与 TTS 使用独立 session 契约和 readiness 状态。native bridge 必须提供 create、push/process、result callback、cancel/abort 和 destroy 边界；托管层不得把不可取消的长时间 native 调用包装成伪流式接口。
10. partial transcript、原始 PCM frame 和内部控制事件默认只驻留内存；final transcript、客户端确认已展示的助手文本、客户端播放管线确认已消费的音频所对应文本、必要诊断和用户允许保留的音频产物复用现有 conversation store。浏览器 `displayed` / `played` acknowledgement 只证明内容已渲染或已进入音频渲染时间线，不证明物理扬声器实际发声；原始麦克风音频默认不写日志、不写数据库。
11. 输入 capture stream 不继承当前输出 response epoch；服务端保留有界 VAD pre-roll，并在确认用户插话时把前缀提升到新的 utterance/turn，随后递增 response epoch、取消当前 LLM/TTS 并要求客户端清空尚未播放的音频。客户端轻量检测只能先 pause/duck，收到权威 speech started 后才不可逆 clear，误判时必须可恢复；旧 response epoch 的迟到文本、音频和未提交工具结果不得进入新回合或继续播放。
12. Realtime 模式继续遵守现有工具安全边界。只读工具可以在既有有界规则内执行；有副作用的工具仍要求请求显式 allowlist、幂等键，以及来自独立明确用户操作并与规范化参数绑定的确认。ASR transcript 或模型判断本身不构成副作用确认，取消也不得伪装成撤销已经提交的外部副作用。
13. 现有文件级 ASR、整段 TTS 和 voice turn API 保持兼容，并作为浏览器能力不足、麦克风不可用、Realtime runtime 未就绪或用户选择 push-to-talk 时的 fallback。
14. R20 与 R19 没有技术前置关系；R20 可以按产品优先级独立启动，路线图编号不要求等待 TomurLPR 完成。

#### P0. 🚧 协议、性能与资源可行性闸门

当前实施切片：先冻结 Tomur Realtime v1 原生控制事件、二进制 PCM 帧头、连接认证、单活跃 session 与有界队列契约，建立不分配 native session 的网关生命周期基础。VAD、增量 ASR、增量 TTS、全双工、前端 AudioWorklet 与真实设备延迟证据仍保持计划状态。

1. 定义 `session`、`capture stream`、`utterance`、`turn`、`response`、`item`、`event`、`sequence` 和 `response epoch` 生命周期，冻结握手、认证首事件、固定二进制帧头、音频 append/commit/clear、gap、speech started/stopped、transcript delta/done、text delta/done、audio delta/done、text displayed acknowledgement、playback consumed acknowledgement、cancel、error 和 close 的事件语义。
2. 定义状态机：`connecting -> listening -> user_speaking -> transcribing -> thinking -> speaking`，以及 `interrupted`、`reconnecting`、`failed` 和 `closed` 转移；每个转移都明确资源所有权、取消对象和允许接收的事件。
3. 定义浏览器 SameSite/HttpOnly cookie 或同源 HTTP 换取一次性 ticket 的流程、升级后认证前状态、Host/Origin allowlist，以及无 Origin 非浏览器客户端的 Bearer/ticket 规则；同时冻结短期 ticket/session/reconnect token 的 TTL 与重放保护、每来源连接和事件速率限制、最大消息大小、凭据脱敏及非 loopback 安全传输边界。
4. 使用客户端 `performance.now()`、服务端单调时钟、音频采集时间戳和统一 trace ID 定义端到端测量方法；固定参考硬件、模型、音频集、预热次数、样本量和 percentile 计算，避免用重复 partial、静音 PCM 或不同时间基准满足延迟指标。
5. 对现有 Whisper、Silero VAD、文本模型、OuteTTS acoustic 模型和 WavTokenizer 分别记录 cold load、warm execution、首结果、实时系数、CPU/GPU 使用、resident memory 与峰值 RSS，并验证 ASR、文本模型和 TTS 同时常驻、共享 ggml library 兼容及 backend 并发时的硬件档位。
6. 使用扬声器、耳机和浏览器无法提供有效 AEC 的设备执行可行性试验，记录 echo-induced false barge-in、有效插话召回率和停止播放延迟；冻结 VAD event precision/recall、非语音每分钟误触发、echo-induced false interruption、中文 CER、英文 WER 的绝对通过门槛和分层语料，AEC 或识别质量无法满足门槛时必须降级到半双工、push-to-talk 或更高模型档位，不报告 full duplex ready。
7. 审核 Whisper runtime/model、Silero VAD 权重、TTS acoustic/WavTokenizer 权重、speaker 数据、native 依赖和新增前端 DSP 依赖的精确版本、checksum、再分发、商业使用与 NOTICE 边界；当前 TTS 许可不满足目标场景时，只能评估更小且许可可用的 llama.cpp / GGUF TTS 模型，不删除或静默替换现有路径。
8. 为客户端 `WebSocket.bufferedAmount`、服务端输入音频、输出音频和控制事件队列分别确定数值上限与 overflow 策略，定义 gap/reset/cancel/close 行为；所有心跳、空闲超时、总时长、单次发言、循环和重试必须有最大次数和墙钟截止时间。
9. 形成 P0 go/no-go 记录。若常驻 session、并存内存、native 取消、AEC 或 warm TTS 实时系数无法达到 MVP 预算，则停止进入全双工 UI，先调整模型、bridge、降级策略或资源方案。

#### P1. 🚧 WebSocket 网关与流式 Push-to-talk

1. 在 Tomur 单进程宿主内建立版本化 WebSocket gateway、session registry、每连接有界输入/输出 channel，以及结构化握手、关闭码和错误响应。
2. 实现 Host 校验、浏览器 Origin 校验和 cookie/一次性 ticket 认证，以及无 Origin 非浏览器客户端的 Bearer/ticket 认证；升级后的未认证连接只允许有界认证事件或直接终止 WebSocket transport，认证或协议失败在分配 native session 前结束，并覆盖 token TTL、重放保护、连接/消息/事件速率限制和敏感字段日志脱敏。
3. 控制事件携带稳定 ID、严格递增 sequence、时间戳和按作用域所需的标识：session 事件不要求 epoch，输入事件使用 `capture_stream_id` 与可选 `utterance_id`，输出文本、音频和取消事件使用 `response_epoch`。binary PCM frame 使用已冻结的固定帧头，只在 session 已协商且处于允许输入状态时接收，乱序、重复、过大、存在 gap 或格式不匹配时按 P0 策略 reset、cancel 或 close，并发送结构化诊断。
4. 建立统一 Realtime 资源协调器，明确 Realtime 与普通 Chat、批处理 ASR/TTS、模型 load/unload、runtime repair 的优先级、内存预留、抢占、锁顺序和取消栅栏；不得只依赖单活跃 Realtime session 限制规避其他入口的并发。
5. 先以手动 commit 的 push-to-talk 完成端到端管线，允许暂时复用批处理 ASR/TTS 验证网关生命周期，但必须明确标记为非全双工、非延迟达标，不作为 R20 完成证据。
6. Web 端建立独立 Realtime session 状态模块；AudioWorklet 负责固定帧采集、输入降采样和声级数据，播放模块通过有界 jitter buffer、输出重采样、媒体时间线与设备时钟漂移修正把 24 kHz 网络 PCM 连续送入 44.1/48 kHz AudioContext，并监控 `WebSocket.bufferedAmount`。DSP 使用 44.1/48 kHz 确定性夹具验证，音频线程不得产生无界分配；达到高水位时按 P0 策略停止当前 utterance 或关闭 session，不把压力转移成无界浏览器内存。
7. 保留现有录音按钮和 voice turn 请求作为 fallback，并为麦克风拒绝、AudioWorklet 不可用、握手失败、runtime busy、输入/输出 overflow 和 session 超时提供明确状态。

#### P2. ⏳ VAD 与增量 ASR

1. 为现有 Silero sidecar 建立独立 VAD native ABI 和托管 session，支持阈值、最短语音、最短静音、speech padding、最大 utterance、reset、cancel 和释放。
2. 服务端 VAD 作为 turn boundary 的权威来源，维护有界 pre-roll，按固定音频帧产生 speech started/stopped，并将检测前缀绑定到新 utterance，避免插话首词因 response epoch 切换而丢失；客户端声级或轻量检测只用于即时反馈和 pause/duck，收到权威 speech started 后才清空旧播放队列，误判必须恢复，不替代服务端最终边界。
3. 建立常驻 Whisper transcription session，支持音频 push、滚动窗口与重叠、partial transcript、final transcript、语言检测、上下文提示、去重和 native abort callback。
4. partial 只作为临时会话项发送；VAD endpoint 或手动 commit 后生成唯一 final transcript，再复用现有 conversation store 持久化为一个用户回合。
5. 使用确定性音频夹具覆盖静音、背景噪声、短促声、连续长语音、双语、重叠窗口、超长发言、取消和损坏 frame，并分别记录 VAD、partial 和 final 的结果与延迟。

#### P3. ⏳ 增量文本与 TTS 音频输出

1. 为 Realtime 路径建立异步文本生成适配器，把现有 token callback 转换为不阻塞 native decode 的 `IAsyncEnumerable` 或有界 channel；网络慢写不得占用模型生成线程。
2. 实现 Unicode、标点、最小/最大字符数和最大等待时间约束的短句聚合器，在不等待完整助手回复的前提下形成可合成片段，并在取消时丢弃尚未提交的片段。
3. 扩展 TTS native bridge，提供 acoustic/WavTokenizer 模型常驻、session create、片段 synthesize、PCM callback、abort、reset 和 destroy；不得在每个文本片段重新加载两个模型。
4. 将生成音频按 sequence 和 response epoch 输出为 24 kHz mono PCM16 chunk，客户端 jitter buffer 按序映射到媒体时间线，重采样到实际 AudioContext 采样率并修正设备时钟漂移；缺帧、迟到帧、重复帧、播放 underrun 和 cancel 后到达的数据都有确定处理规则。
5. 重新验证当前输出开头固定静音的必要性；不得以固定 250 ms 静音掩盖波形边界问题。分片拼接需要通过淡入淡出、零交叉或等价策略避免爆音，同时记录首个可听样本延迟。
6. TTS 模型在参考硬件上达不到实时系数、首音频预算或连续播放稳定性时保持不可用或 degraded 诊断，不以提前发送静音 PCM 声称首音频达标。

#### P4. ⏳ 全双工、barge-in 与生命周期

1. 助手播报期间保持麦克风采集和 VAD 运行；浏览器启用可用的 echo cancellation、noise suppression 和自动增益策略，并记录设备不支持时的 fallback。
2. 客户端轻量检测到可能插话时立即 pause/duck；服务端从独立 capture stream 的有界 pre-roll 确认有效 speech started 后，为完整输入前缀建立新 utterance/turn、递增 response epoch、取消 LLM/TTS、重置待合成片段，并通知客户端停止和清空当前播放。客户端误判或服务端未确认时必须恢复旧 response 播放或进入确定的取消状态。
3. LLM token、TTS PCM、工具结果和持久化操作在提交前复核所属 response epoch；取消栅栏之后不得启动新的副作用。已经越过提交点的副作用按工具自身取消语义完成或失败，必须保留审计与最终状态，不得因丢弃旧 response epoch 结果而声称未执行。
4. 为每个助手文本片段记录 `generated`、`tts_committed`、`sent`、客户端确认的 `displayed` 和 `played` 边界，并将音频 sequence 映射回源文本；`played` 定义为 AudioWorklet/AudioContext 已在计划媒体时间消费，不等同于物理扬声器可听。插话后持久化带 `interrupted` 状态的 canonical assistant item，下一轮上下文只使用 P0 协议确定的已展示或播放管线已消费内容；未确认内容最多保留为诊断元数据，不得伪装成用户已经看到或听到的历史。
5. 支持用户主动取消、静音、结束会话、页面卸载、网络断开、模型 unload、runtime repair 和进程停止；所有路径都必须在有界时间内释放音频轨道、channel、CancellationTokenSource 和 native handle。
6. 建立有界断线恢复语义，只恢复 session 配置和已提交 conversation item，不重放未确认的原始音频、不继续旧 response，也不重复执行可能有副作用的工具。

#### P5. ⏳ Chat 语音体验、诊断与协议适配

1. 在现有 Chat-first 工作台内提供进入语音模式、静音、结束、取消当前回复和设备选择；不新增管理后台式首页或语音一级导航，退出语音模式后返回同一 conversation。
2. UI 明确展示 `connecting`、`listening`、`user_speaking`、`transcribing`、`thinking`、`speaking`、`interrupted`、`reconnecting` 和错误状态，并同时呈现 partial/final transcript、文本回复和必要诊断。
3. 高频触控按钮满足移动端尺寸和可访问名称要求；状态变化使用适当的 live region，动画遵守 reduced-motion，声级或波形区域使用稳定尺寸，不能遮挡消息与控制项。
4. 扩展 runtime API、doctor 和 Settings/状态入口，分别报告 gateway、VAD native/model、ASR warm session、TTS warm session、输入/输出队列、丢帧、underrun、当前状态、最近错误和硬件资源建议。
5. 分开表达 `gateway available`、`model ready`、`session warm`、`full duplex connected` 和 `realtime smoke passed`；任何单一状态不得被概括为完整 Realtime 已验证。
6. 在原生 session engine 稳定后实现 OpenAI Realtime 风格适配，逐项记录会话更新、输入音频、turn detection、conversation item、response、文本/音频 delta、取消、错误和关闭行为；未覆盖事件返回明确不支持诊断，不静默忽略。

#### P6. ⏳ 质量、发布与真实设备证据

1. 建立协议与状态机专项测试，覆盖事件顺序、重复/乱序 frame、背压、慢客户端、队列溢出、取消竞态、capture stream / response epoch 时序隔离、断线、超时、session busy、runtime 缺失和 native 失败。
2. 使用固定音频夹具验证 VAD、ASR partial/final 与 transcript 去重，并使用 24 kHz -> 44.1/48 kHz DSP 夹具验证输出时长、连续性、漂移和有界分配；使用确定性 fake ASR/LLM/TTS 验证网关和 UI，不以 fake engine 替代真实模型证据。
3. 在用户明确要求验证时，对固定推荐硬件、模型与标注音频集执行真实麦克风、扬声器、ASR、文本模型和 TTS 的端到端 smoke，分别记录 CPU/CUDA backend、cold/warm、p50/p95、VAD precision/recall、中文 CER、英文 WER、首个有意义 partial、final 修订、barge-in recall/false-positive、实时系数、丢帧、underrun 和取消时间；浏览器 acknowledgement 与物理可听延迟分开，后者使用音频回环设备或专用测试仪表测量。
4. 分别完成不少于 100 回合的重复交互 soak 和不少于 30 分钟的连续通话 soak；P0 冻结允许的 working set/native resident 回落容差和增长斜率，结束后 session-owned task/handle 必须为零、队列必须清空，不得用二选一场景替代。
5. 浏览器矩阵至少覆盖 Windows 的当前及上一主版本 Edge/Chrome、macOS 的当前及上一主版本 Safari/Chrome，以及 Linux 的当前及上一主版本 Firefox/Chrome；分别验证麦克风权限、AudioContext 激活、自动播放限制、设备热插拔、AEC 降级和 fallback。
6. 覆盖 Realtime 与普通 Chat、批处理 ASR/TTS、模型切换、session unload 和 runtime repair 的并发与取消竞态，验证统一资源协调器的优先级、锁顺序、内存预留和清理行为。
7. 执行专项测试、完整 solution 回归、Web 构建、非 AOT 自包含发布、Native AOT 发布和目标 RID smoke；构建成功、协议通过、真实模型通过、延迟达标和跨平台通过分别记录。
8. 建立 `docs/r20-realtime-voice-smoke.md` 作为证据入口；首次建立时保持 pending，只有真实设备、真实模型和量化指标均有记录后才更新对应验证状态。

工作量判断：P0 预计 1–2 周，P1 预计 1–2 周，P2 预计 2–3 周，P3 预计 3–5 周，P4 预计 2–3 周，P5 预计 1–2 周，P6 预计 2–3 周，顺序执行约为 12–20 工程周。单人达到可发布质量的初始规划范围为 4–6 个月；具备 C++ native、.NET gateway 和 React 音频经验的 2–3 人并行推进，在 P0 通过后 8–12 周只作为受控硬件 MVP / preview 估算，不作为跨平台发布承诺。发布质量工期必须在 P0 与 P3 证据完成后重新估算。

验收：

1. 双向音频与控制事件可以同时收发，事件 ID 与 sequence 稳定递增；输入事件的 `capture_stream_id` / `utterance_id`、输出事件的 `response_epoch` 及 partial/final、text/audio delta、cancel、error 和 close 形状稳定、作用域明确且顺序可验证，session 级事件不强制携带 epoch。
2. 所有输入 frame、输出 chunk、队列、session 数、单次语音、空闲时间、总时长和重连都有明确上限；慢客户端、断线、取消和 runtime unload 不产生无界内存增长或不可回收任务。
3. 在固定配置、推荐模型、参考硬件和不少于 100 个标注 turn 上，speech-start 检测 `p95 <= 200 ms`，用户停止说话到 VAD speech-stop `p95 <= 700 ms`，speech-stop 到 final transcript `p95 <= 800 ms`；所有端到端指标使用客户端音频采集时间和单调时钟定义。
4. 首个有意义 ASR partial 定义为相对上一次结果新增至少一个稳定词、中文字符或延长稳定前缀，从对应已标注语音片段采集完成起 `p95 <= 1.0 s`，后续有意义更新间隔 `p95 <= 500 ms`。在 P0 固定的代表性近讲中文/英文与日常噪声分层语料上，最终 transcript 的中文 CER 与英文 WER 均不得高于 15%，且相对同模型批处理基线的绝对退化不超过 2 个百分点；各主要分层单独报告，不得用汇总均值掩盖失败分层。
5. 用户停止说话到首个可听助手音频 `p95 <= 2.5 s`，优化目标为 `p95 <= 1.5 s`；不得用静音 PCM、占位音频或伪造事件满足指标。
6. VAD 采用 P0 冻结的事件匹配窗口计算，event precision 与 recall 均不低于 95%，纯静音/背景噪声误 speech-start 不高于 0.2 次/分钟。在扬声器、耳机和 AEC 降级矩阵中，有效 barge-in 到本地停止播放 `p95 <= 250 ms`，插话召回率不低于 95%，仅助手播放且无人说话时 echo-induced false interruption 不高于 0.1 次/分钟；超过门槛的设备必须明确降级，不报告 full duplex ready。
7. 稳态 TTS 的 `RTF = synthesis wall time / generated audible duration`，warm p95 小于 `0.8`；连续播放没有未解释的持续 underrun，达不到预算的硬件或模型返回 degraded / unavailable 诊断。
8. 100 回合重复交互和 30 分钟连续通话两个 soak 均完成后，队列清空、session-owned task/handle 为零，working set、native resident、线程和任务增长落在 P0 冻结的容差内，并能在结束、断线、取消和进程停止路径完整回收。
9. 麦克风拒绝、Origin/身份验证失败、token 重放、格式不匹配、损坏或 gap frame、模型/runtime 缺失、内存不足、上下文超限、客户端过慢、队列 overflow、session busy、网络断开和 native 失败均返回稳定诊断，不崩溃、不伪造 transcript 或音频。
10. 原始麦克风音频默认不持久化、不写日志；保留音频必须由用户可见设置明确启用。Realtime 工具调用继续遵守 allowlist、参数校验、幂等键、最大轮次和副作用确认边界；取消栅栏后不启动新副作用，已经提交的副作用始终记录最终状态。
11. 现有文件级 ASR、整段 TTS、voice turn、文本 Chat 和会话历史行为保持兼容；Realtime 不可用时用户仍可回退到按钮式录音或文本对话。
12. fake engine 测试、构建通过、native library 可见、模型 ready、真实推理通过、延迟达标、长会话通过和跨平台通过分别记录；只有完整证据闭环后才可将 R20 标记为完成。
