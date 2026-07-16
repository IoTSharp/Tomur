# Tomur Changelog

本文件记录 Tomur 已完成的产品与工程历史。后续计划、进行中事项和验收目标维护在 [ROADMAP.md](./ROADMAP.md)。

## 未发布

### M13 发布与兼容

已完成 managed provider 发布与兼容基础代码：非 AOT 发布会构建批准的 `Tomur.Providers.Glm.dll` 并复制到主程序旁的 `providers/`，生成包含契约版本、程序集版本、provider ID 和 SHA-256 的 `providers.manifest.json`。provider discovery 会校验 `Tomur` 契约程序集版本，并在发布清单存在时拒绝缺失或 checksum 不匹配的 provider；失败只影响对应 managed provider，native provider 保持可用。Native AOT 继续返回 `dynamic_managed_providers_unavailable`，未引入未经验证的动态托管程序集加载。M13 测试项目已接入 solution；本轮未执行构建、测试或发布 smoke，跨平台证据归 M14。

### M12 高级能力

已完成 managed GLM 高级能力基础代码：DSA/MTP 配置与 tensor probe、接收 indexer score 的稳定 DSA top-k selection、dense-equivalent runtime 路径、可选 MTP resident head 与单步 draft、speculative rejection sampling、grammar forced spans、router lookahead prefetch、live expert repin，以及带模型身份、维度检查和 SHA-256 的 compressed KV 快照/恢复与 isolated KV fork。未验证的稀疏 DSA 不使用 attention score 冒充 indexer score。M12 独立测试项目已接入 solution；本轮未执行构建或测试，真实模型语义、采样分布、性能、完整模型和跨平台验证仍归 M14。

### M11 性能优化

已完成性能优化基础代码：managed GLM forward 阶段 timing、可回退的 activation int8 integer dot 评估 kernel、prefill 批次级 unique expert union/prefetch，以及默认 RandomAccess、可选 mmap 的 tensor I/O 实验边界。默认推理路径和 scalar oracle 保持不变；完整 benchmark、allocation、跨平台和真实模型验证仍归 M14。

### R15 当前已接入

1. 已建立 `Tomur.Providers.Glm` 独立纯 C# 类库、extend-only provider 契约、非 AOT 动态发现边界与 `SessionManager` 选择路径；未匹配的现有模型继续使用 llama.cpp。
2. 已建立 `model.tomur.json`、GLM 配置、tokenizer 基础结构和 safetensors header/tensor index 的有界只读探测，并接入 provider discovery、Catalog、doctor、Runtime API 与 Web Runtime 诊断。
3. 已建立固定 seed 的 tiny F32 fixture、版本化 oracle、tensor manifest、SHA-256 校验、隐藏 generate/verify 入口与 M1-M13 独立测试项目；fixture generator 1.1.0 增加 dense MLP checkpoint，同时保留既有 MoE teacher-forcing 基线。
4. 已建立统一 tensor descriptor、只读 shard 随机访问、F32/F16/BF16 resident 转换、int4/int8 量化视图、池化 workspace 与 expert slab 基础层。
5. 已建立无并行、无 intrinsics 的 scalar reference kernels，覆盖 embedding、normalization、F32 与 int8/int4 矩阵乘、activation int8 量化、基础激活、elementwise、residual 和 stable top-k，并显式校验 shape、stride、buffer、alias 与量化边界。
6. 已实现 WordLevel/BPE tokenizer、必要的 normalizer/pre-tokenizer/decoder 子集、byte-level UTF-8 映射、added/special token、GLM role prompt template、多个 token stop，以及跨 token UTF-8 和文本 stop 增量解码。
7. 已建立 `ManagedGlmModel` resident dense model 边界，按配置校验常驻 tensor shape/dtype，在 payload 读取前核算 resident、compressed KV 与 scratch bytes，并在预算超限、取消、损坏 shard 或 dispose 时提供明确失败与资源释放行为。
8. 已接通 resident embedding gather、input RMSNorm 与 dense SwiGLU MLP scalar 基础路径；session 会报告 resident tensor、resident/KV/scratch bytes、总预算和 open shard 数。
9. 已实现 MLA q/kv projection、interleaved partial RoPE、逐位置重建 K/V 的 reference path、KV latent weight absorption path、逐 head causal attention、稳定 softmax、单 token decode 与多 token prefill。
10. 已建立按 layer 保存 normalized KV latent 与 RoPE key 的 compressed KV cache，以及 position/context 状态、固定 attention workspace、非有限 score/output 诊断、取消安全点和 token/prefill 失败回滚边界。
11. 已实现 MoE router 的 sigmoid、correction bias、稳定 top-k、可选概率归一化和 routed scaling，并接通 resident shared expert 与 streamed routed expert 的 SwiGLU 合并路径。
12. 已建立 routed expert descriptor 契约与 F32/F16/BF16、int8/int4 可复用 buffer；量化 expert 使用 `*.weight` payload 和同前缀 `*.scales` per-row F32 sidecar。
13. 已建立按层固定容量 expert LRU、lease 隔离、RAM 配额、固定 worker bounded channel、同 key miss 合并、取消边界，以及 hit/miss/eviction/disk/等待时间/usage histogram 诊断。
14. 已按 embedding、逐层 norm/attention/residual/MLP 或 MoE、final norm 和 lm head 顺序接通完整 scalar forward，并使用最大 32 token 的有界批次执行 prompt prefill、compressed KV 增量 decode 和最终 logits 投影。
15. 已实现稳定 greedy、temperature、top-k/top-p、repeat/frequency/presence penalty、固定 seed sampling、多 EOS、跨 token 文本 stop、max output token、context limit、cancellation 和增量文本 callback。
16. managed session 已切换到 `managed-glm-generation`，在 expert 读取前完成总上下文预检，只在成功后累计 usage，并通过 `managed_context_length_exceeded` 或 `managed_inference_failed` 返回失败，不生成占位 completion。
17. forward batch、sampling、tensor、attention 与 MoE workspace 已纳入内存计划；session 级 expert cache 在 resident、KV 和 scratch 之后校验每层最小 top-k working set，并报告 cache/I/O 诊断。
18. `model.tomur.json` 已增加 extend-only 的可选 `quantization_layout`；`packed-offset` 显式支持 `*.qs` F32 row scale、U8 int8、offset-binary packed int4 和按 matrix payload 长度识别 int8/int4，量化 resident 权重保持压缩存储。
19. managed Chat 已通过可选 session 契约直接接收结构化 role/content，并按 GLM role token 语义构造 prompt；现有不实现该契约的 provider 行为保持不变。
20. 服务启动时，已加载 managed provider 的情况下只降级 native `runtime` / `acceleration` 初始化错误，其他启动错误仍保持失败，不要求 packed managed 模型先准备 native bundle。
21. 转换后的随机 tiny packed 模型已完成 Catalog、OpenAI Chat 非流式与 SSE、Ollama Chat、Anthropic Messages，以及 int8 embedding/lm_head + int4 dense/expert 混合精度 OpenAI Chat smoke；结果只证明格式与 forward 链路，不作为自然语言质量证据。记录见 `docs/r15-packed-glm-smoke.md`。
22. 已建立独立 `Tomur.Providers.Olmoe` 纯 C# provider，接通 OLMoE config/probe、标准 causal attention、full KV cache、q/k RMSNorm、split-half RoPE、softmax top-k router、streamed experts、官方 chat template、生成与 session 诊断；tiny F32/BF16 与 signed-int8 `rowwise-qs` 路径纳入自动化测试。
23. 原始 BF16 `allenai/OLMoE-1B-7B-0125-Instruct` 三个 safetensors shard 已通过 Catalog、动态 provider load、完整模型加载与中文 Ollama 非流式真实对话；`/api/runtime/status` 可报告 147 个 resident tensor、3 个打开的 shard、resident/KV/scratch 预算和 expert cache/I/O。记录见 `docs/r15-olmoe-smoke.md`。
24. 文本 session 生命周期日志已从硬编码的 llama 文案改为中性 `text generation session`，并显示实际 `runtime`；Runtime 诊断会优先报告已加载的 managed session，不再被未使用的 llama.cpp native bundle 状态覆盖。
25. managed GLM 以 extend-only 方式增加 `glm4_moe_lite` architecture/config 契约，要求 manifest 与 `model_type` 一致，并校验当前实现所需的 MLA head、interleaved RoPE、SwiGLU、sigmoid/noaux router 与 dense/sparse layer 边界；GLM-4.7 prompt 分支已对齐 prefix 换行、默认非 thinking assistant closure 和 tool response 包装。真实 REAP 权重转换与完整模型 smoke 尚未执行，异机步骤记录在 `docs/r15-glm4-moe-lite-validation.md`。
26. 已增加可选 managed model readiness 契约；GLM 与 OLMoE 可只读探测 metadata、必要资产、tensor 数量、resident/KV/scratch/expert cache 计划和当前内存预算，不读取 resident payload，不执行 forward。
27. `LocalModelCatalog` 已区分候选模型与兼容 API 可见模型；provider、metadata 或必要资产校验失败的 managed 模型不再进入 `/v1/models`、`/api/tags` 和协议模型选择路径，并通过 Runtime/doctor 保留结构化诊断。
28. Ollama `generate` 与 `chat` streaming 已接入 provider 增量 callback，按 NDJSON 输出 `done=false` 文本增量和带 usage/duration 的 `done=true` 终帧；流中失败继续返回 Ollama 风格结构化错误。
29. `SessionManager` 已拆分状态锁与单请求执行门；session unload 会取消活动生成，等待请求退出后再释放 session，并通过 `session_unloading` / `session_unloaded` 保持并发请求和协议错误边界。
30. session 快照已增加 provider、architecture、context、busy、resident/KV/scratch、expert cache、expert I/O、请求/token 统计与最近错误；`tomur ps`、`tomur doctor`、Runtime API 和 Web Runtime 使用同一 readiness/session 状态。
31. M10 独立测试项目已加入 solution，覆盖 OpenAI SSE、Anthropic SSE、Ollama NDJSON 的增量与终帧、流中结构化错误、readiness 内存计划、不完整资产可见性拦截、forward verified 状态与 unload 取消/释放；本轮未执行构建、测试或服务 smoke。
32. managed GLM 已增加可配置的 SIMD/shape-aware kernel dispatch：F32、int8 与 packed int4 matvec 按硬件 `Vector<float>` 宽度执行，F32 大 shape 可有界并行，dense/shared/routed expert gate/up 使用 paired dispatch；`TOMUR_GLM_KERNEL_MODE=scalar` 保留原 scalar oracle 回退。
33. expert cache 已增加 RAM budget 自动 per-layer capacity、usage histogram hot pin 与显式异步 prefetch，并移除 acquire 热路径的 LINQ 排序和 `HashSet` 分配；session 诊断报告 kernel、hot pin 与 prefetch 状态。
34. M11 独立测试项目已加入 solution，覆盖 scalar fallback、SIMD/并行、int8/int4、paired dispatch、cache capacity、prefetch 与 hot eviction；本轮未执行构建、测试或性能测量。
35. managed GLM 已扩展 DSA/MTP 配置与 tensor probe；DSA selection 接收独立 indexer score，在 top-k 覆盖全部 causal key 时保持 dense softmax 等价，未验证的稀疏 runtime 路径明确失败；可选 MTP projection head 纳入 resident 内存计划并提供单步 draft 边界。
36. 已实现 speculative acceptance/rejection residual sampling 与 grammar forced spans；两者作为可选算法边界，不改变未启用时的基础生成路径。
37. expert cache 已增加上一 token router lookahead prefetch、usage 驱动的 live repin 和 repin 诊断；所有预取继续受 slot capacity、RAM budget 与取消约束。
38. compressed KV 已增加版本化快照、模型身份/维度/上下文/有限值校验和 SHA-256 完整性校验，并提供不共享可变 buffer、共同受模型剩余内存预算约束的 isolated KV context fork。
39. M12 独立测试项目已加入 solution，覆盖 DSA dense-equivalent/stable top-k、speculative 接受与拒绝、forced spans、lookahead/repin、KV checksum 恢复、context fork 隔离和共享内存预算；本轮未执行构建、测试、完整模型或跨平台验证。

### R14 当前已接入

1. `tomur native build --backend` 已扩展 `vulkan`、`openvino`、`sycl` 与 `intel` 入口；`intel` 组合构建 llama.cpp 的 SYCL、OpenVINO 与 Vulkan dynamic backend。
2. `native/llama.native` 已增加 `windows-x64-sycl` CMake preset，并安装 `ggml-sycl` 到 RID runtime 输出目录。
3. native bundle manifest 已为 llama.cpp 标注 `cuda13`、`sycl`、`openvino`、`vulkan` 与 `cpu` 变体，Intel backend 作为可选库参与 probe 诊断，并记录 variant 诊断说明与修复动作。
4. 本地配置新增 `runtime.accelerator` 偏好字段，覆盖 `auto|cpu|cuda|vulkan|sycl|openvino`、设备选择键、GPU layers、OpenVINO device、NPU opt-in 与 NPU prefill chunk。
5. llama.cpp backend 初始化前会按本地配置设置 `GGML_OPENVINO_DEVICE` 与 `GGML_OPENVINO_PREFILL_CHUNK_SIZE`。
6. acceleration 诊断会报告偏好、选中设备、Intel backend 可见性、OpenVINO / NPU 设置和 CPU fallback 原因。
7. Web Runtime 面板已展示 accelerator 偏好、OpenVINO / NPU 设置、配置选择键、fallback reason 与 backend library 状态。
8. `/api/runtime/status`、Web Runtime 面板与 `tomur doctor` 已为 SYCL、OpenVINO 和 Vulkan backend 缺失或可见状态提供结构化修复提示。
9. Intel NPU 路径已增加模型加载、context 初始化、prompt prefill、生成 decode 与 embedding decode 的专用错误码和修复建议；不适配时返回协议错误，不伪造推理结果。
10. `docs/r14-intel-acceleration-smoke.md` 已建立 R14 Intel GPU / NPU smoke 记录入口，明确 backend、设备名、模型、上下文、GPU layers、token usage、成功证据和失败证据字段。

### R13 已完成历史

1. Agent runtime、工具地图、tool bindings、events 和 telemetry 已接入 Web 只读查询。
2. Settings 增加 `Agents` 与 `Capabilities` 分组，展示 Agent 工具状态、bindings、事件、telemetry、OpenAI / Ollama / Claude Code / Conversations / Multimodal / Runtime API 能力地图。
3. Chat 欢迎区快捷 Prompt 已注入当前本地状态摘要。
4. Chat 气泡底部已增加图片产物预览和产物打开入口；音频产物继续使用播放器。
5. Agents 分组已提供 `runtime.diagnose`、`tools.inspect` 与 `files.search` 的只读 Web 调用。
6. `image.generate`、`audio.speak` 与 `runtime.repair` 通过副作用工具确认卡片调用，执行前必须显式确认。
7. 纯文本重新生成已替换上一条 assistant 回复；附件和语音回合仍要求重新发送原始输入。
8. `GET /v1/models?limit=1000` 已支持 Claude Code 模型发现形状，为本地文本模型返回 `claude-tomur-*` 别名。
9. `POST /v1/messages` 与 `POST /v1/messages/count_tokens` 已接入 Anthropic Messages 风格请求、非流式响应和 SSE streaming 事件，并映射到 Tomur 本地文本 runtime。
10. Capabilities 分组已为 OpenAI、Ollama、Claude Code、Conversations 与 Agent API 提供最小请求示例复制入口。

### R12 已完成历史

1. `native-aot-audit.pubxml` 已设置 `PublishAot=true`、`SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true` 和 `SuppressTrimAnalysisWarnings=false`。
2. `self-contained-single-file.pubxml` 保留非 AOT 自包含单文件发布路径。
3. RID 发布默认启用 `SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`，并保持 `IncludeAllContentForSelfExtract=false`。
4. `JsonSerializerIsReflectionEnabledByDefault=false` 保持开启；API DTO、配置、catalog、runtime、native、agent、conversation 和多模态响应已登记在 `AppJsonSerializerContext`。
5. Native AOT 发布已确认可通过且无警告，R9 记录的 Native AOT 阻塞已由 R12 承接并清除。
6. `win-x64` / `linux-x64` 随包资产与 checksum 已记录在 `docs/r12-native-bundle-inventory.md`。
7. R12 服务形态 smoke 清单已记录在 `docs/r12-service-smoke.md`。
8. R12 发布包结构说明已记录在 `docs/r12-release-package-structure.md`。

### R11 完成历史

1. 建立 `web/` React + TypeScript 工程，使用 Vite 构建。
2. 接入 `antd`、`@ant-design/x`、`@ant-design/x-markdown` 与 `@ant-design/x-sdk`。
3. 建立 Chat-first 应用外壳，主区域为 Chat，顶部提供模型选择器，右上角提供 Settings 与状态抽屉入口。
4. Runtime、download、model readiness 以紧凑状态条、内联诊断和抽屉呈现，不作为默认一级页面。
5. 前端构建产物输出到 `app/wwwroot`，并由 `Tomur.csproj` 托管；`tomur open`、无参数启动与 `GET /` 使用同一工作台入口。
6. 工作台接入 `/api/version`、`/api/runtime/status`、`/api/runtime/multimodal`、`/api/models/catalog`、`/api/models/installed` 与 `/v1/models`。
7. 工作台接入 `/v1/chat/completions`，支持基础消息发送、OpenAI 文本增量 streaming、停止生成、重新生成与复制。
8. Settings 具备 General、Models、Downloads、Runtime、API、Files 与 Advanced 分组入口。
9. Runtime 分组接入 native bundle 状态、component 状态、诊断提示、`POST /api/runtime/native/prepare`、`POST /api/runtime/session/unload` 和复制 CLI/API 动作。
10. Chat 输入区接入 Ant Design X `Attachments`，支持图片、音频和文本文件随下一轮发送。
11. 语音入口接入按钮式录音，浏览器录音转为 16 kHz mono PCM WAV 后调用语音回合 API。
12. 工作台启动时读取 `/api/conversations` 列表，支持历史详情懒加载和会话软删除。
13. Chat 气泡下方诊断标签可跳转到对应 Settings 分组。

### R10 完成历史

1. 建立会话状态模型，记录用户消息、附件、工具调用、诊断、生成产物和音频结果，默认存储在本地 SQLite / 文件目录。
2. 接入文本会话编排，在同一轮对话中执行 Tomur 确定性工具计划、回填结果并继续生成回答。
3. 支持图片、音频和本地文件作为会话输入，远程 URL 不自动下载并返回会话诊断。
4. 接入语音输入链路：客户端录音或上传音频后，调用 Whisper ASR 生成 transcript，再交给会话编排层处理。
5. 接入语音输出链路：AI 回复生成后，调用 llama.cpp GGUF TTS 生成可播放音频，并把音频结果登记为本地产物。
6. 提供语音回合 API，形成“音频输入 -> ASR -> 会话处理 -> TTS -> 音频输出”的单回合服务。
7. `POST /api/conversations/{conversationId}/messages` 支持追加用户、助手、system 和 tool 消息。
8. `POST /api/conversations/{conversationId}/artifacts` 支持登记本地产物路径、类型、media type、来源、大小和 metadata。
9. `GET /api/conversations/{conversationId}/artifacts/{artifactId}/content` 支持读取 Tomur 数据目录内的会话产物内容。
10. `POST /api/conversations/{conversationId}/diagnostics` 支持记录会话级诊断。
11. `DELETE /api/conversations/{conversationId}` 已接入会话软删除。

### R9 完成历史

1. Tomur 本地 chat runtime 已适配到 `Microsoft.Extensions.AI.IChatClient`。
2. `POST /api/agents/chat` 通过 `Microsoft.Agents.AI.ChatClientAgent` 调用本地 `IChatClient`。
3. `GET /api/agents/runtime` 与 `GET /api/agents/tools` 暴露 chat、image、vision、OCR、ASR、TTS、files 和 runtime diagnostics 的当前状态。
4. `GET /api/agents/tool-bindings` 暴露当前 `Microsoft.Extensions.AI.AITool` 绑定与安全边界字段。
5. `POST /api/agents/tools/invoke` 提供受控工具调用入口。
6. `POST /api/agents/workflows/read-only` 支持受控只读工具步骤和可选摘要。
7. agent chat、受控工具调用、只读 workflow 和失败事件写入 `<data>/logs/agents.jsonl`。
8. Agent telemetry 暴露 `Tomur.Agents` `ActivitySource` span / attribute 约定与 exporter 状态。
9. `image.generate`、`vision.analyze`、`ocr.recognize`、`audio.transcribe`、`audio.speak`、`files.search`、`runtime.diagnose` 和确认式 `runtime.repair` 已作为受控工具接入。
10. opt-in OpenTelemetry exporter 管线已接入，默认保持本地 ActivitySource 与 JSONL 事件日志。

### R8 完成历史

1. `GET /api/runtime/multimodal` 提供 ASR、TTS、OCR、stable-diffusion.cpp image generation 与 VLM backend 的统一诊断面。
2. 多模态诊断同时检查 native component 状态与本地模型资产可见性，并返回修复动作。
3. `/api/vision/analyze`、`/api/ocr/analyze` 与包含 data URI / base64 图片的 `/v1/chat/completions` 已接入 VLM / OCR 托管执行路径。
4. 包含 `image_url` / `input_image` 的 `/v1/chat/completions` 请求不会把图片输入作为普通文本交给文本 runtime；远程图片 URL 当前要求调用方发送 data URI。
5. `/v1/audio/transcriptions` 已接入 Whisper adapter，支持音频文件转写。
6. `/v1/audio/speech` 已接入 llama.cpp GGUF TTS adapter，返回 WAV。
7. `/v1/images/generations` 已接入 stable-diffusion.cpp PNG 生成适配器，并通过内部 image worker 子进程执行。
8. Whisper、PaddleOCR-VL、stable-diffusion.cpp 与 llama.cpp GGUF TTS 已在 manifest 中接入 `cpu` / `cuda13` native 变体。
9. R8 smoke 记录见 `docs/r8-smoke-report.md`。

### R7 完成历史

1. llama.cpp P/Invoke、受管理 native import resolver、进程内单 session manager 与按需模型加载已接入。
2. `/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`、`/api/generate` 和 `/api/chat` 可在模型可见且能力匹配时调用本地 runtime。
3. OpenAI / Ollama 非流式成功响应已返回文本和基础 token usage。
4. OpenAI 文本 streaming 成功路径会随本地生成回调输出文本增量。
5. `/api/runtime/status` 报告 llama native prepared、native 缺失或当前加载的 llama.cpp session。
6. `POST /api/runtime/session/unload` 可卸载当前 session。
7. native runtime 缺失、模型加载失败、上下文超限、模型能力不匹配和 embedding 不可用会返回诊断错误。
8. llama.cpp backend catalog 设备探测、CUDA / NPU / Metal / Vulkan / SYCL / OpenVINO backend 可见性诊断、GPU/NPU 优先 offload 策略和 CPU fallback 已接入。

### R6 完成历史

1. `tomur pull`、`tomur list` 与 `tomur ps` 已接入。
2. 下载支持断点续传、checksum 校验、proxy、license 提示和硬件档位推荐。
3. `GET /api/models/catalog` 与 `GET /api/models/installed` 已接入。
4. 内置 Catalog 覆盖默认聊天、翻译、embeddings、reranker、ASR + VAD、TTS、VLM 和图像生成包。
5. `tomur pull` 支持 `recommended`、`optional`、`all` 与包 ID 选择，支持 `--proxy`、`--no-proxy`、`--force` 和 `--dry-run`。
6. `<data>/models/models.manifest.json` 记录安装包、资产 hash、license notice 与 bundle 资产。
7. `/v1/models`、`/api/tags`、`tomur list` 和 `tomur ps` 会读取安装清单和本地散落模型文件。
8. R6 完成口径限定为模型资产推荐、下载、校验、登记和可见性管理；真实文本、多模态推理执行分别归 R7 / R8。

### R5 完成历史

1. `tomur service install`、`uninstall`、`start`、`stop`、`status` 和 `run` 服务宿主入口已接入。
2. Windows Service 安装与管理代码路径已接入，服务名为 `Tomur`。
3. Linux systemd service 安装与管理代码路径已接入，unit 名为 `tomur.service`。
4. macOS launchd user agent 安装与管理代码路径已接入，label 为 `dev.tomur.service`。
5. 无参数启动与 `tomur open` 作为双击启动路径已接入。
6. Windows 原生托盘图标与打开工作台、Runtime 状态、退出控制已在 Tomur 单体 Windows 交互式外壳中实现。
7. 服务模式和 `tomur serve` 使用同一套 host 逻辑。
8. 服务安装显式固定数据目录、工作目录和 bundle 解压目录。

### R4 完成历史

1. OpenAI 端点已接入：`GET /v1/models`、`POST /v1/chat/completions`、`POST /v1/completions`、`POST /v1/embeddings`、`POST /v1/images/generations`。
2. Ollama 端点已接入：`GET /api/version`、`GET /api/tags`、`POST /api/show`、`POST /api/generate`、`POST /api/chat`。
3. streaming 错误帧行为稳定。
4. 错误响应符合对应 API 风格。
5. 未下载模型、runtime 不可用、上下文超限等情况有清晰错误码。
6. 轻量本地模型文件发现会扫描数据目录下的模型文件，并用于 `/v1/models` 与 `/api/tags`。
7. R4 完成口径限定为首批协议面、请求校验、兼容风格错误响应、streaming 错误帧和轻量本地模型发现；真实推理归 R7 / R8。

### R3 完成历史

1. native bundle manifest、版本化 runtime extraction、library checksum 和 runtime probe 已接入。
2. Windows DLL search path 与 Linux shared object probe 支持已接入。
3. ggml 共享库隔离规则已建立。
4. PaddleOCR / MTMD C++ OCR bridge 作为 OCR 主线边界。
5. llama.cpp TTS / GGUF TTS 作为 TTS 主线。
6. TTS C ABI bridge 骨架已接入。
7. native probe / library resolver / library loader 托管接口已接入。
8. `GET /api/runtime/native`、`POST /api/runtime/native/prepare`、`GET /api/runtime/native/{componentId}/{libraryName}` 与 `POST /api/runtime/native/{componentId}/{libraryName}/load` 已接入。
9. Windows x64 native 构建入口已接入：`tomur native build --rid win-x64 --backend all|cpu|cuda13`。
10. native manifest 支持 `cpu` / `cuda13` 变体目录和优先级选择。

### R2 完成历史

1. 数据目录解析支持默认目录、`TOMUR_DATA_DIR` 与 `--data-dir`。
2. 配置文件 `<data>/config/tomur.json` 已接入。
3. SQLite 初始化 `<data>/tomur.db` 已接入。
4. API key 本地哈希存储已接入，提供 `tomur api-key create/list`。
5. `tomur doctor` 输出 OS、架构、CPU、内存、磁盘、proxy、端口、数据目录、SQLite、API key 与 runtime 状态。
6. `/api/runtime/status` 已接入。
7. 配置损坏时提供恢复路径。

### R1 完成历史

1. `Tomur.csproj` 与 `Program.cs` 建立。
2. `tomur --help`、`tomur serve` 与 `tomur doctor` 接入。
3. `GET /health`、`GET /api/version` 与 `GET /v1/models` 接入。
4. `POST /v1/chat/completions` 与 `POST /api/chat` 协议骨架接入。
5. 聊天端点在 runtime 未配置时返回明确诊断，不伪造推理结果。

### R0 完成历史

1. `README.md` 说明 Tomur 的定位、目标能力和技术架构。
2. `ROADMAP.md` 作为长期路线图。
3. 文档不描述尚未完成的能力为已实现。
4. 文档不引入外部产品背景或非 Tomur 定位。
