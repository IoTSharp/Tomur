# Managed GLM Provider 路线图

本文是 Tomur 根目录 `ROADMAP.md` 中 R15 的实现级细化，记录纯 C# GLM / MoE 模型提供器的工程顺序、实现方法、验收门槛和风险控制。根目录路线图继续负责产品阶段排序；本文只负责 `providers/Glm/` 的实现。

## 状态图例

| 标记 | 含义 |
| --- | --- |
| ✅ | 已完成且已有对应代码或文档 |
| 🚧 | 已开始，但尚未完成要求的验证 |
| ⏭️ | 下一阶段 |
| ⏳ | 计划中 |

## 目标

1. 使用 C# 实现模型配置、tokenizer、张量读取、量化算子、KV cache、MoE routing、expert 流式加载和文本生成。
2. 产物保持为独立托管程序集 `Tomur.Providers.Glm.dll`，由 Tomur 的 provider 边界发现和加载。
3. 不调用未声明的 native dynamic library，不在 provider 内部通过 P/Invoke 回退到其他推理引擎。
4. 保留现有 llama.cpp provider；managed GLM provider 是并行选择，不改变既有 GGUF 模型和 embedding 路径。
5. 优先保证模型语义和输出正确，再优化 SIMD、并行、缓存与磁盘吞吐。
6. 未达到对应验收门槛时返回结构化诊断，不返回占位 token，不把“模型可发现”写成“模型可推理”。

## 非目标

1. 不在首批实现中建设通用张量框架、训练框架或任意 Transformer 图执行器。
2. 不把所有 safetensors 文件自动认定为可运行模型。
3. 不在首批实现中支持多租户、远程模型服务、跨机器调度或多节点 expert 存储。
4. 不为了统一接口删除、隐藏或降低现有 native provider 能力。
5. 不在基础 forward 正确前实现 GPU、NPU、MTP、DSA 稀疏选择或复杂 speculative decoding。
6. 不要求字节级复刻其他运行时的内部内存布局；只要求模型语义、量化解释和验证输出一致。

## 纯 C# 边界

允许使用：

1. `unsafe`、指针和固定内存，但所有权与生命周期必须封装。
2. `Span<T>`、`ReadOnlySpan<T>`、`Memory<T>`、`MemoryMarshal` 和 `ArrayPool<T>`。
3. `RandomAccess`、`FileStream`、`SafeFileHandle` 和内存映射等 BCL 文件能力。
4. `System.Numerics.Vector<T>` 与 `System.Runtime.Intrinsics` 的 x86/Arm SIMD。
5. `Parallel.For`、`Thread`、`Task`、`Channel<T>` 等托管并发能力。
6. BCL 提供的本地内存分配能力，但不得借此加载或调用第三方 native 推理库。

禁止使用：

1. provider 内的 `DllImport`、`LibraryImport`、`NativeLibrary.Load` 或手工函数指针绑定。
2. 未在模型清单和诊断中声明的外部服务进程。
3. 通过启动其他推理 CLI 来伪装成托管推理。
4. blanket trimming/AOT suppression。
5. 对模型输入、张量尺寸、偏移、内存预算或上下文长度缺少上限检查。

## 目标结构

```text
Tomur/
  app/
    Providers/
      ModelProviderContracts.cs
      ModelProviderManifest.cs
      ModelProviderRegistry.cs
    Inference/
      SessionManager.cs
  providers/
    Glm/
      ROADMAP.md
      README.md
      Tomur.Providers.Glm.csproj
      ManagedGlmProvider.cs
      ModelDirectoryProbe.cs
      GlmModelConfiguration.cs
      SafeTensorCatalog.cs
      Format/
      Storage/
      Tensors/
      Kernels/
      Tokenization/
      Model/
      Attention/
      Moe/
      Generation/
      Diagnostics/
      Fixtures/
```

目录职责：

1. `Format/`：模型清单、配置、tokenizer 和 safetensors 元数据解释。
2. `Storage/`：分片文件、随机读取、resident tensor、expert slab 和缓存所有权。
3. `Tensors/`：逻辑 shape、dtype、量化布局、张量视图和工作区。
4. `Kernels/`：scalar reference kernel 与 SIMD kernel。
5. `Tokenization/`：文本编码、特殊 token、解码和增量 UTF-8 输出。
6. `Model/`：模型加载、层结构、resident 权重和 session 生命周期。
7. `Attention/`：MLA、RoPE、compressed KV cache 和 attention 工作区。
8. `Moe/`：router、shared expert、routed expert、LRU 和磁盘流水线。
9. `Generation/`：prefill、decode、sampling、stop 条件和 streaming。
10. `Diagnostics/`：稳定错误码、统计、内存与 I/O 报告。
11. `Fixtures/`：tiny 模型、tokenizer 输入和 oracle 结果，不保存完整模型权重。

在实际复杂度出现前不预先创建空目录；新增文件必须对应当前阶段的可执行工作。

## 模型目录契约

每个 managed GLM 模型目录必须包含 `model.tomur.json`，由该文件显式声明 provider 和模型架构：

```json
{
  "schema_version": 1,
  "provider": "managed-glm",
  "architecture": "glm_moe_dsa",
  "display_name": "Local GLM MoE",
  "config": "config.json",
  "tokenizer": "tokenizer.json",
  "tensor_pattern": "*.safetensors",
  "quantization": "int4",
  "capabilities": ["completion", "chat"]
}
```

实现要求：

1. 所有路径必须是模型目录内的相对路径，拒绝 rooted path、`..` 穿越和越界解析。
2. `schema_version` 采用只扩展演进；新 reader 继续读取旧 schema，旧字段不改变含义。
3. provider ID 与 architecture 必须显式匹配，不按目录名猜测。
4. tensor pattern 只在模型目录顶层展开，避免无边界递归。
5. Catalog 可以展示通过基础清单验证的模型，但只有 provider 完成完整 probe 后才能创建 session。
6. checksum 失败、张量缺失或 bundle 不完整时不得登记为 ready。

## 张量与量化约定

首批支持以下存储：

1. `F32`：norm、router、bias 和 tiny oracle 权重。
2. `BF16` / `F16`：输入输出边界或未量化 fixture。
3. `I8` / `U8`：int8 权重或已编码 payload。
4. packed int4：每字节两个 4-bit 值，每行一条 `F32` scale。
5. int2 只在 int4/int8 forward 完整通过后加入。

逻辑维度来自模型配置和算子约定，不能只相信 packed tensor 的物理 shape。每个量化张量必须验证：

1. 输出行数与输入列数为正数并受配置上限约束。
2. int8 payload 长度等于 `rows * columns`。
3. int4 payload 长度等于 `rows * ceil(columns / 2)`。
4. scale tensor 长度等于 `rows * sizeof(float)`。
5. payload 和 scale 均位于对应 shard 的合法偏移范围内。
6. 所有乘法和偏移计算使用 checked arithmetic。

## 内存层级

目标内存层级：

```text
CPU cache
  -> 当前算子工作区与当前 expert
RAM resident
  -> embedding、attention、norm、router、shared expert、KV cache、hot experts
RAM cache
  -> 每层 LRU expert slots
OS page cache
  -> 最近访问的模型页
Disk
  -> routed expert 的不可变来源
```

首批不把磁盘读取隐藏成“模型已驻留”。运行状态必须分别报告：

1. resident bytes。
2. expert cache bytes。
3. KV cache bytes。
4. scratch/workspace bytes。
5. 本轮磁盘读取字节数和等待时间。
6. cache hit、miss 和 eviction。

## 阶段总览

| 顺序 | 阶段 | 状态 | 主题 |
| --- | --- | --- | --- |
| 00 | M0 | ✅ | 分支、工程规则与 provider 方向 |
| 01 | M1 | 🚧 | provider 边界、模型清单与 safetensors probe |
| 02 | M2 | 🚧 | tiny fixture 与 oracle 验证基线 |
| 03 | M3 | 🚧 | 张量存储、读取和量化视图 |
| 04 | M4 | ⏳ | scalar reference kernels |
| 05 | M5 | ⏳ | tokenizer、prompt 与增量解码 |
| 06 | M6 | ⏳ | resident dense model 加载 |
| 07 | M7 | ⏳ | MLA attention 与 compressed KV cache |
| 08 | M8 | ⏳ | MoE router、shared expert 与 expert streaming |
| 09 | M9 | ⏳ | 完整 forward、prefill、decode 与 sampling |
| 10 | M10 | ⏳ | Tomur API、session、streaming 与诊断闭环 |
| 11 | M11 | ⏳ | SIMD、并行、缓存与 I/O 优化 |
| 12 | M12 | ⏳ | DSA、MTP、grammar draft 与 KV 持久化 |
| 13 | M13 | ⏳ | 打包、版本兼容与 Native AOT 策略 |
| 14 | M14 | ⏳ | 完整模型运行和长期回归证据 |

## 00. ✅ M0：分支与工程决策

目标：允许 native 与纯 C# provider 并存。

已落地：

1. 建立 `feature/managed-glm-provider` 分支。
2. 修改工程规则，允许 `providers/` 下的独立托管类库。
3. 明确保留 llama.cpp 等既有 native 路径。
4. 确定程序集名 `Tomur.Providers.Glm` 和 provider ID `managed-glm`。
5. 禁止在代码命名、provider ID、配置键和诊断代码中使用参考项目名称。

验收：

1. 根 README、ROADMAP 和 AGENTS 口径一致。
2. managed provider 不改变既有 GGUF 与 embedding 路径。

## 01. 🚧 M1：provider 边界与模型 probe

目标：让 Tomur 能发现独立托管程序集，并在不读取完整权重的前提下判断模型目录是否结构完整。

当前代码：

1. `ITextGenerationProvider` 和 `ITextGenerationSession` 已建立。
2. `ModelProviderRegistry` 已建立非 AOT 动态程序集发现入口。
3. `SessionManager` 已增加 managed provider 选择；未匹配时继续使用 llama.cpp。
4. `model.tomur.json` reader 已建立 schema、路径和 capability 检查。
5. GLM 配置上限检查已建立。
6. tokenizer JSON 基础结构检查已建立。
7. safetensors header、shape、dtype、offset 和重复 tensor 检查已建立。
8. dense、attention、router、shared expert 和 routed expert 必需 tensor 名称检查已建立。
9. forward 未接通时返回 `managed_forward_not_ready`。
10. provider discovery 已记录搜索目录、已加载 provider ID、程序集版本与路径，并区分坏程序集、契约缺失、激活失败、非法 ID 和重复 ID。
11. provider discovery diagnostics 已接入 `tomur doctor`、`GET /api/runtime/status` 与 Web Runtime 状态合同；managed provider 失败只降低为 warning，不阻断 native provider。
12. Catalog 已隔离包含无效 `model.tomur.json` 的目录，不再把其中的 safetensors shard 误登记为普通模型。
13. safetensors probe 已校验受支持 dtype、shape 对应字节数、连续且不重叠的数据范围，并保持只读取文件长度与 header。
14. M1 测试项目已建立运行时生成的最小合法/非法模型目录 fixture，不在仓库保存模型权重。

仍需完成：

1. 编译主程序、provider 与 M1 测试项目，解决编译器和 analyzer 发现的问题。
2. 执行 M1 回归测试，确认合法 probe、坏 manifest、越界 tensor offset、缺 tensor、坏 DLL、非法 ID、重复 ID 与缺 provider 诊断。
3. 实跑 provider DLL 从默认 `providers` 目录和 `TOMUR_PROVIDER_PATH` 加载，并记录两条发现路径的结果。
4. 实跑 `tomur doctor` 与 `GET /api/runtime/status`，确认 provider 状态、路径和修复动作可见。

验收：

1. llama.cpp 模型仍进入原 session 路径。
2. managed 模型缺 provider 时返回 `managed_provider_unavailable`。
3. 非法模型目录不会让整个 Catalog 或服务进程崩溃。
4. probe 期间不读取 tensor payload，不分配完整模型内存。
5. 本阶段完成前仍保持 🚧，不得标记为可聊天。

## 02. 🚧 M2：tiny fixture 与 oracle 基线

目标：先建立可重复的正确性判定，再开始实现算子。

当前代码：

1. tiny 配置使用真实 GLM / MoE 字段，并把 hidden、layer、head、expert、vocab 和 context 缩小到可逐项检查的范围。
2. fixture generator 使用版本固定的 SplitMix64 派生 PRNG 与固定 seed 生成 F32 权重，并写出完整 tensor manifest。
3. oracle 已保存固定文本 token IDs、embedding lookup、RMSNorm、MLA attention、router、MoE、单层输出、teacher-forcing logits 和 greedy decode token 序列。
4. scalar reference graph 在 F32 边界保存 checkpoint，并使用 double accumulator 固定矩阵乘、归一化、softmax 与路由权重计算顺序。
5. `fixture.manifest.json` 对配置、tokenizer、safetensors、tensor manifest 和 oracle 记录长度与 SHA-256；校验器同时检查 schema、配置摘要与逐 tensor checksum。
6. `tomur internal model-fixture generate|verify` 已通过可选 provider 契约接入；M2 独立测试项目覆盖重复生成、oracle checkpoint、篡改拒绝与 managed probe。
7. fixture 由 generator 在干净目录按需创建，仓库不提交完整模型权重。

仍需完成：

1. 编译主程序、provider 与 M2 测试项目，处理编译器和 analyzer 结果。
2. 执行 M2 回归测试，并确认两个干净目录的全部生成文件逐字节一致。
3. 实跑隐藏 CLI 的 generate 与 verify 路径，记录 checksum 和失败诊断。
4. 在首个生产 kernel 合入前，用独立实现复核 reference graph 的 attention、router、teacher-forcing 与 greedy 结果。

验收：

1. fixture 可从干净目录重复生成或下载并校验 checksum。
2. oracle 文件具有 schema version、模型配置摘要和生成工具版本。
3. 所有后续 kernel 都能单独与 oracle 比较。
4. greedy 输出必须在固定 seed、固定 kernel path 下可重复。

## 03. 🚧 M3：张量存储和读取层

目标：建立不依赖模型图的安全张量访问层。

计划类型：

1. `TensorDescriptor`：name、dtype、logical shape、physical length、shard、offset。
2. `TensorDataSource`：持有只读 shard handle，提供有界 `ReadExactly`。
3. `ResidentTensor<T>`：拥有长期驻留内存。
4. `QuantizedTensorView`：描述 int8/int4 payload 与 per-row scales。
5. `TensorWorkspace`：复用临时 activation、quantization 和 output buffer。
6. `ExpertSlab`：一次容纳 gate/up/down 和 scale 的可复用 slot。

实现顺序：

1. 支持 F32、BF16、F16 转 F32。
2. 支持按 tensor 整体读取和按字节区间读取。
3. 支持 packed int4 逻辑 shape 校验。
4. 支持合并读取同一 shard 内相邻 payload。
5. 为长期 resident 和短期 scratch 建立不同所有权。
6. 所有大数组在 session 创建时预算，decode 热路径不反复进入 LOH。
7. session dispose 后关闭 handles 并归还 pooled buffers。

验收：

1. 随机 tensor slice 与原始文件字节完全一致。
2. EOF、短读、偏移溢出和 disposed handle 返回明确异常。
3. 重复 load/unload 不保留打开文件句柄。
4. probe、resident load 和 expert stream 使用同一 tensor descriptor，不重复解析 header。

## 04. ⏳ M4：scalar reference kernels

目标：建立简单、可读、可验证的标量实现，作为所有 SIMD kernel 的正确性基准。

首批 kernel：

1. embedding gather。
2. RMSNorm。
3. LayerNorm，仅用于模型配置需要的位置。
4. F32 matvec / matmul。
5. int8 per-row dequant matvec。
6. packed int4 per-row dequant matvec。
7. activation quantize-to-int8。
8. SiLU、sigmoid、softmax。
9. elementwise add、multiply 和 residual。
10. top-k 与稳定 tie-breaking。

实现原则：

1. scalar path 不使用并行和 intrinsics。
2. 循环顺序、accumulator 类型和 rounding 必须固定。
3. kernel 输入输出不得在未声明时 alias。
4. 每个 kernel 检查 shape、stride 和 buffer 长度。
5. 性能优化不得删除 scalar path；scalar path 用于诊断和跨平台 fallback。

验收：

1. F32 kernel 与 oracle 达到预定绝对/相对误差。
2. int8/int4 kernel 与离线解量化结果一致。
3. 奇数列数、非 SIMD 对齐、空 batch 和最小 shape 有覆盖。
4. 非法 shape 不得造成越界访问。

## 05. ⏳ M5：tokenizer、prompt 与增量解码

目标：在模型 forward 前先锁定文本与 token 的双向语义。

实现：

1. 解析 tokenizer model、vocab、merge rules、added tokens 和 special tokens。
2. 实现 Unicode 到 UTF-8 byte sequence 的稳定映射。
3. 实现 normalizer、pre-tokenizer 和 BPE merge 的必要子集。
4. 支持 EOS、user、assistant、observation 等多个 stop token。
5. 建立 GLM chat template，不把通用 llama prompt builder 直接套用到该模型。
6. decode 维护未完成 UTF-8 尾部，只有完整字符才发送给 streaming callback。
7. stop sequence 检查保留可能跨 token 的尾部窗口。

验收：

1. 固定中英文、代码、emoji、空白和控制字符输入与 oracle token IDs 一致。
2. encode/decode 对可逆样本保持 byte roundtrip。
3. special token 不被普通 BPE 拆分。
4. streaming 不输出损坏 UTF-8，也不泄漏 stop sequence。

## 06. ⏳ M6：resident dense model

目标：加载每个 token 都需要的 dense 权重，并完成 embedding 到首层输入的基础路径。

resident 范围：

1. token embedding 与 lm head。
2. final norm。
3. 每层 input/post-attention norm。
4. MLA q/kv/o projections。
5. dense MLP layers。
6. MoE router、router correction bias 和 shared experts。
7. 可选 DSA/MTP 权重暂不进入首批 ready 条件。

实现：

1. `ManagedGlmModel` 持有配置、tokenizer、tensor catalog 和 resident weights。
2. load 前计算预计 resident bytes、KV bytes 和 scratch bytes。
3. 预算超过可用内存时在读取 payload 前失败。
4. 读取完成后释放临时 conversion buffer。
5. model 与 session 分离，为后续多 context 共享只读权重保留边界；首批仍只允许一个活动 session。

验收：

1. resident bytes 与实际分配误差在可解释范围内。
2. 缺失或损坏 dense tensor 时不进入 Loaded 状态。
3. load cancellation 能停止后续读取并释放已分配资源。
4. embedding、norm 和 dense MLP 单层输出与 oracle 对齐。

## 07. ⏳ M7：MLA attention 与 compressed KV cache

目标：实现模型要求的 MLA attention，并避免按完整 K/V head 保存上下文。

数据结构：

1. `KvCache`：按 layer 保存压缩 KV latent 和 RoPE key 部分。
2. `AttentionWorkspace`：q latent、q heads、scores、context 和 output projection。
3. `SequenceState`：position、有效 token 数、上下文上限和 cache 起点。

实现顺序：

1. q_a projection 与 q_a norm。
2. q_b projection，拆分 nope/rope head。
3. kv_a projection，拆分 compressed latent 与 RoPE key。
4. interleaved partial RoPE。
5. kv_b 重建 K/V 的 reference path。
6. causal attention、softmax 和 output projection。
7. 单 token decode。
8. 多 token prefill。
9. decode 正确后再加入 MLA weight absorption 优化。

安全边界：

1. position 和 context size 必须在分配上限内。
2. attention score 计算检查 NaN/Infinity。
3. cancellation 只在不会留下半写 KV 的安全点生效。
4. forward 失败时回滚本轮新增 KV 位置。

验收：

1. 单层 attention 中间值与 oracle 对齐。
2. teacher forcing 的每个位置 logits 对齐。
3. prefill 后逐 token decode 与纯逐 token 路径一致。
4. compressed KV 实际字节数与配置公式一致。

## 08. ⏳ M8：MoE 与 expert streaming

目标：只让 dense/shared 权重常驻，由磁盘按路由结果读取 routed experts。

router 实现：

1. F32 router matvec。
2. sigmoid score。
3. correction bias。
4. group selection；首批只接受 `n_group=1`。
5. top-k expert IDs。
6. 可选 top-k 概率归一化。
7. routed scaling factor。
8. 稳定 tie-breaking，避免不同排序实现导致 expert 漂移。

expert 执行：

1. 读取 gate/up/down payload 与 scales。
2. gate 与 up projection。
3. `SiLU(gate) * up`。
4. down projection。
5. 乘 routing weight 并累加。
6. shared expert 与 routed expert 结果相加。

缓存设计：

1. 每层独立固定容量 LRU，slot 不跨并发 forward 被复用。
2. `ExpertKey = (layer, expertId, quantization)`。
3. hit 更新访问时钟；miss 进入 bounded I/O queue。
4. eviction 只发生在 layer 安全点。
5. 支持配置 RAM budget，先减去 resident、KV、scratch 和安全余量。
6. 记录长期 usage histogram，为后续 hot pin 提供依据。

I/O 流水线：

1. 使用固定 worker 数和 bounded `Channel<ExpertReadRequest>`。
2. 同一 layer 的唯一 expert 只读取一次，再服务 batch 内所有 token。
3. 主线程先执行 resident/shared 工作，再等待缺失 expert。
4. cancellation 停止未开始的读取；已开始读取完成后归还 slot。
5. 不允许无界 Task 创建或每 expert 新建线程。

验收：

1. router IDs、权重和单层 MoE 输出与 oracle 对齐。
2. 冷 cache 与热 cache 输出一致。
3. cache eviction 不改变 token 结果。
4. 缺 shard、短读和 checksum 失败返回模型资产诊断。
5. 内存预算不能因并发 miss 超出上限。

## 09. ⏳ M9：完整 forward 与生成

目标：完成可从 prompt 生成 token 的最小正确路径。

forward 顺序：

1. token embedding。
2. 对每层执行 input norm、attention、residual。
3. post-attention norm。
4. dense MLP 或 MoE。
5. residual。
6. final norm。
7. lm head。

生成能力：

1. prompt prefill。
2. greedy decode。
3. temperature sampling。
4. top-k / top-p。
5. repeat、frequency、presence penalty。
6. 多 EOS token。
7. 文本 stop sequences。
8. max output token、context limit 和 cancellation。

实现顺序：

1. `batch=1`、greedy、F32 reference。
2. `batch=1`、量化权重。
3. 多 token prefill。
4. sampling。
5. streaming callback。

验收：

1. tiny teacher forcing 所有位置达到误差门槛。
2. tiny greedy token 序列完全一致。
3. 固定 seed sampling 可重复。
4. context 超限在写 KV 前失败。
5. 任何异常都不产生伪造 completion。

## 10. ⏳ M10：Tomur 集成闭环

目标：让 managed provider 通过现有 OpenAI、Ollama 和 Anthropic Messages 入口工作，不增加另一套 API。

实现：

1. `SessionManager` 继续按模型 provider 选择 session。
2. `GET /v1/models` 与 `/api/tags` 只展示清单和资产校验通过的模型。
3. `/v1/chat/completions`、`/api/chat` 和 `/v1/messages` 复用现有请求/streaming 响应。
4. `tomur ps` 显示 provider、模型、resident bytes、KV bytes、expert cache 和请求统计。
5. `tomur doctor` 显示 provider DLL、manifest、配置、tokenizer、shards、内存预算和格式诊断。
6. Runtime API/UI 区分 provider discovered、model metadata valid、model assets complete、forward verified 和 session loaded。
7. unload 同时释放 session、file handles、pooled buffers 和 expert cache。

错误码：

1. `managed_provider_unavailable`
2. `managed_provider_load_failed`
3. `managed_provider_probe_failed`
4. `managed_model_invalid`
5. `managed_model_assets_incomplete`
6. `managed_quantization_unsupported`
7. `managed_memory_budget_exceeded`
8. `managed_context_length_exceeded`
9. `managed_forward_not_ready`
10. `managed_inference_failed`

验收：

1. managed 与 llama.cpp 模型能在同一 API 表面被正确路由。
2. managed provider 失败不影响 llama.cpp 请求。
3. streaming 断开能够触发取消并回收本轮状态。
4. 未验证模型在 UI 中不显示为 ready。

## 11. ⏳ M11：性能优化

目标：在不改变模型语义的前提下提高 CPU、RAM 和磁盘利用率。

优化顺序：

1. 建立 scalar 基准和阶段耗时分解。
2. 使用 `Vector128/256/512` 实现 F32、int8 和 int4 matvec。
3. 评估 activation int8 quantization 与 integer dot-product。
4. 按 shape 选择 kernel，不假定单 token 与 batch 使用同一最优路径。
5. 将固定工作区移出 token loop。
6. 合并 gate/up dispatch。
7. 对 unique experts 做 batch union。
8. 异步预读下一批已知 expert。
9. 根据 RAM budget 自动计算每层 cache capacity。
10. 根据 usage histogram pin hot experts。
11. 只在 benchmark 证明收益后引入内存映射或更复杂的 I/O 策略。

每项优化必须：

1. 与 scalar path 比较中间输出。
2. 记录 CPU 架构和指令集。
3. 记录 prompt、context、batch、cache 状态和磁盘状态。
4. 分别报告 cold 与 warm 结果。
5. 可通过配置关闭并回退。

性能指标：

1. 模型 probe 时间。
2. resident load 时间。
3. prompt tokens/s。
4. decode tokens/s。
5. 首 token 延迟。
6. 每 token expert reads。
7. 磁盘读取 GB/s 与 foreground wait。
8. cache hit rate。
9. RSS、managed heap、pinned bytes 和 pooled bytes。
10. GC pause 和每 token allocation。

## 12. ⏳ M12：高级能力

前置条件：M9 正确性完成，M11 有稳定基准。

计划顺序：

1. DSA indexer 权重探测和 dense-equivalent 验证。
2. DSA top-k causal key selection。
3. MTP head 加载和单步 draft。
4. speculative verification 与 rejection sampling。
5. grammar-constrained forced spans。
6. router lookahead prefetch。
7. live expert repin。
8. compressed KV 持久化和恢复。
9. isolated KV contexts。

验收：

1. 禁用高级能力时回到已验证基础路径。
2. DSA 在保留全部 key 时与 dense attention 一致。
3. speculative decoding 不改变采样分布。
4. KV 恢复与不中断会话输出一致。
5. 任一高级能力不能绕过内存预算、取消或模型资产校验。

## 13. ⏳ M13：发布与兼容

非 AOT 自包含发布：

1. 将批准的 provider DLL 放入主程序旁的 `providers/`。
2. 锁定 provider contract assembly version。
3. provider 加载失败只影响对应模型。
4. 发布清单记录 provider DLL checksum。

Native AOT 发布：

1. 不假定能动态加载任意托管程序集。
2. 评估静态引用 provider 的 profile。
3. 若静态引用不可接受，保留清晰的 `dynamic_managed_providers_unavailable` 诊断。
4. 不通过 blanket suppression 绕过 trimming 或反射警告。

兼容策略：

1. provider contract 采用 extend-only 设计。
2. 新能力通过新接口或可选 capability 增加，不向现有接口添加必须实现的方法。
3. `model.tomur.json` schema 先扩 reader，再启用 writer。
4. 诊断 code 一经公开不改名，只增加新 code。

验收：

1. 非 AOT 包可从默认目录发现 provider。
2. 缺 provider DLL 时主程序和 native providers 正常运行。
3. AOT 包行为与文档一致。
4. provider 升级不会破坏旧模型 manifest。

## 14. ⏳ M14：完整模型验证

目标：证明完整模型可以在受控资源下加载并生成真实 token。

验证矩阵：

1. Windows x64。
2. Linux x64。
3. macOS arm64 在基础 kernel 支持后加入。
4. CPU 指令集：scalar、AVX2、可用时 AVX-512、Arm AdvSimd。
5. RAM 档位：最小可运行、推荐；全 resident 不作为首批要求。
6. cold disk、warm page cache、hot expert cache。

必须记录：

1. commit、provider version 和模型 checksum。
2. CPU、RAM、磁盘、文件系统和 OS。
3. 模型 resident/expert/KV/scratch 预算。
4. probe、load、prefill、decode 时间。
5. teacher forcing 或固定 prompt 正确性。
6. greedy 与 sampling 输出。
7. expert hit/miss、读取字节和等待时间。
8. 峰值 RSS、managed heap、GC pause。
9. 成功或失败诊断。

最终验收：

1. 完整模型能从 `model.tomur.json` 被 Catalog 发现。
2. provider 完成配置、tokenizer、tensor 和内存预算校验。
3. `/v1/chat/completions` 能返回真实生成 token。
4. streaming、取消、unload 和服务重启行为可重复。
5. 输出正确性通过固定 oracle 或可信对照。
6. 失败时不伪造结果，且 llama.cpp provider 仍可独立使用。

## 诊断与可观测性

每次 session 应保留以下只读状态：

1. provider ID 与 provider version。
2. model ID、architecture、quantization 和 shard count。
3. context size、KV used/capacity。
4. resident/cache/scratch bytes。
5. current kernel path。
6. request、prompt token 和 completion token 计数。
7. expert hit、miss、eviction 和 disk bytes。
8. prefill、decode、attention、MoE、I/O 和 lm head 耗时。
9. 最近一次结构化错误。

统计不得把磁盘等待、page-cache 命中或 speculative draft 记成模型计算吞吐。

## 风险清单

| 风险 | 影响 | 控制方式 |
| --- | --- | --- |
| 纯 C# kernel 性能不足 | decode 过慢 | scalar 基线后按热点引入 SIMD，按 shape 选择 kernel |
| 大数组造成 LOH/GC 压力 | pause、内存峰值 | session 级预分配、buffer pool、零 token-loop 分配 |
| expert I/O 吞吐不足 | 每 token 等待磁盘 | bounded 并行读取、LRU、hot pin、page-cache 指标 |
| 量化 rounding 不一致 | token 分叉 | 固定 accumulator 和 tie-breaking，保存 teacher-forcing oracle |
| tokenizer 不一致 | 所有输出失真 | tokenizer 先于 forward 独立验收 |
| 恶意模型元数据 | 越界或内存耗尽 | checked arithmetic、尺寸上限、目录边界和 header 上限 |
| provider DLL 版本不匹配 | 加载失败 | extend-only contract、版本诊断、隔离失败 |
| Native AOT 无动态加载 | provider 不可用 | 非 AOT 独立 DLL；AOT 静态引用或明确诊断 |
| 过早优化 | 难以定位错误 | reference path 永久保留，逐阶段 oracle gate |
| 完整模型验证成本高 | 回归缓慢 | tiny fixture 覆盖日常正确性，完整模型作为独立 smoke |

## Definition of Done

managed GLM provider 只有同时满足以下条件才可以从实验状态转为可用：

1. 模型目录、config、tokenizer 和全部必要 tensor 校验通过。
2. scalar reference path 通过 tiny teacher forcing 和 greedy decode。
3. 默认优化 kernel 与 scalar path 在约定误差内一致。
4. 完整模型至少在一个支持平台完成真实 prompt 和 streaming generation。
5. 记录内存预算、首 token 延迟、token/s、expert I/O 和输出正确性。
6. 缺模型、缺 provider、内存不足、上下文超限和磁盘错误都有结构化诊断。
7. unload 后释放文件句柄和大块内存。
8. managed provider 的失败不会影响 llama.cpp provider。
9. README、根 ROADMAP、Runtime API 和 UI 不夸大未验证能力。
10. 发布方式明确区分非 AOT 动态 provider 与 Native AOT 行为。
