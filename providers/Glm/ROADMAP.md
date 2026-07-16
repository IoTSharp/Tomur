# Managed GLM Provider 路线图

本文是 Tomur 根目录 `ROADMAP.md` 中 R15 的实现级细化，记录纯 C# GLM / MoE 模型提供器的工程顺序、实现方法、验收门槛和风险控制。根目录路线图继续负责产品阶段排序；本文只负责 `providers/Glm/` 的实现。

## 状态图例

| 标记 | 含义 |
| --- | --- |
| ✅ | 本阶段基础代码已完成；集中测试与验证统一在 M14 执行 |
| 🚧 | 基础代码实现中 |
| ⏭️ | 下一基础代码阶段 |
| ⏳ | 计划中 |

本路线图优先推进 M0-M13 的基础代码。阶段状态按代码落地情况标记，不再因为尚未执行构建、测试或实机验证而长期保持 🚧；所有编译、测试、oracle 对齐、跨平台实跑、性能测量和发布验证统一收敛到 M14。M14 完成前，✅ 只表示对应实现范围已落地，不表示已经 smoke-validated，也不表示 managed GLM provider 已可用于真实聊天。

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
  "quantization_layout": "packed-offset",
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
7. `quantization_layout` 采用 extend-only 可选字段；默认 `separate-scales` 保持现有 `*.scales` 与 two's-complement int4 语义，`packed-offset` 显式选择 `*.qs` per-row scale、offset-binary int4 和量化 resident matrix 语义。
8. 外部转换目录只有增加 Tomur 清单并通过 config、tokenizer、全部必要 tensor、shape、dtype、scale 和内存预算校验后才可被 provider 选择；不得仅按文件名猜测格式。

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
| 01 | M1 | ✅ | provider 边界、模型清单与 safetensors probe |
| 02 | M2 | ✅ | tiny fixture 与 oracle 基线 |
| 03 | M3 | ✅ | 张量存储、读取和量化视图 |
| 04 | M4 | ✅ | scalar reference kernels |
| 05 | M5 | ✅ | tokenizer、prompt 与增量解码 |
| 06 | M6 | ✅ | resident dense model 加载 |
| 07 | M7 | ✅ | MLA attention 与 compressed KV cache |
| 08 | M8 | ✅ | MoE router、shared expert 与 expert streaming |
| 09 | M9 | ✅ | 完整 forward、prefill、decode 与 sampling |
| 10 | M10 | 🚧 | Tomur API、session、streaming 与诊断闭环 |
| 11 | M11 | 🚧 | SIMD、并行、缓存与 I/O 优化 |
| 12 | M12 | ⏳ | DSA、MTP、grammar draft 与 KV 持久化 |
| 13 | M13 | ⏳ | 打包、版本兼容与 Native AOT 策略 |
| 14 | M14 | ⏳ | 集中测试、完整模型验证与发布证据 |

## 00. ✅ M0：分支与工程决策

目标：允许 native 与纯 C# provider 并存。

已落地：

1. 建立 `feature/managed-glm-provider` 分支。
2. 修改工程规则，允许 `providers/` 下的独立托管类库。
3. 明确保留 llama.cpp 等既有 native 路径。
4. 确定程序集名 `Tomur.Providers.Glm` 和 provider ID `managed-glm`。
5. 禁止在代码命名、provider ID、配置键和诊断代码中使用参考项目名称。

## 01. ✅ M1：provider 边界与模型 probe

目标：让 Tomur 能发现独立托管程序集，并在不读取完整权重的前提下判断模型目录是否结构完整。

已完成基础代码：

1. `ITextGenerationProvider` 和 `ITextGenerationSession` 已建立。
2. `ModelProviderRegistry` 已建立非 AOT 动态程序集发现入口。
3. `SessionManager` 已增加 managed provider 选择；未匹配时继续使用 llama.cpp。
4. `model.tomur.json` reader 已建立 schema、路径和 capability 检查。
5. GLM 配置上限检查已建立。
6. tokenizer JSON 基础结构检查已建立。
7. safetensors header、shape、dtype、offset 和重复 tensor 检查已建立。
8. dense、attention、router、shared expert 和 routed expert 必需 tensor 名称检查已建立。
9. M1 阶段使用 `managed_forward_not_ready` 阻止未完成路径返回 token；该临时边界已在 M9 由真实 forward 替代。
10. provider discovery 已记录搜索目录、已加载 provider ID、程序集版本与路径，并区分坏程序集、契约缺失、激活失败、非法 ID 和重复 ID。
11. provider discovery diagnostics 已接入 `tomur doctor`、`GET /api/runtime/status` 与 Web Runtime 状态合同；managed provider 失败只降低为 warning，不阻断 native provider。
12. Catalog 已隔离包含无效 `model.tomur.json` 的目录，不再把其中的 safetensors shard 误登记为普通模型。
13. safetensors probe 已校验受支持 dtype、shape 对应字节数、连续且不重叠的数据范围，并保持只读取文件长度与 header。
14. M1 测试项目已建立运行时生成的最小合法/非法模型目录 fixture，不在仓库保存模型权重。

## 02. ✅ M2：tiny fixture 与 oracle 基线

目标：先建立可重复的正确性判定，再开始实现算子。

已完成基础代码：

1. tiny 配置使用真实 GLM / MoE 字段，并把 hidden、layer、head、expert、vocab 和 context 缩小到可逐项检查的范围。
2. fixture generator 使用版本固定的 SplitMix64 派生 PRNG 与固定 seed 生成 F32 权重，并写出完整 tensor manifest。
3. oracle 已保存固定文本 token IDs、embedding lookup、RMSNorm、MLA attention、router、MoE、单层输出、teacher-forcing logits 和 greedy decode token 序列。
4. scalar reference graph 在 F32 边界保存 checkpoint，并使用 double accumulator 固定矩阵乘、归一化、softmax 与路由权重计算顺序。
5. `fixture.manifest.json` 对配置、tokenizer、safetensors、tensor manifest 和 oracle 记录长度与 SHA-256；校验器同时检查 schema、配置摘要与逐 tensor checksum。
6. `tomur internal model-fixture generate|verify` 已通过可选 provider 契约接入；M2 独立测试项目已包含重复生成、oracle checkpoint、篡改拒绝与 managed probe 用例。
7. fixture 由 generator 在干净目录按需创建，仓库不提交完整模型权重。

## 03. ✅ M3：张量存储和读取层

目标：建立不依赖模型图的安全张量访问层。

已完成基础代码：

1. `TensorDescriptor` 已统一 safetensors probe 与后续读取所需的 name、dtype、logical shape、physical length、shard 和 offset，并允许量化层在不改变物理范围的前提下指定逻辑 shape。
2. `TensorDataSource` 已按 shard 持有只读随机访问 handle，提供有界整 tensor、slice 与同 shard 相邻 tensor 合并读取，并在 dispose 时关闭全部 handle。
3. F32、F16 与 BF16 已支持读取为 F32 resident buffer；F16/BF16 转换使用固定大小临时 buffer，并在取消或失败时释放未完成的 resident tensor。
4. `ResidentTensor<T>` 已使用独占托管数组表达长期驻留所有权；`TensorWorkspace` 使用固定容量池化 activation、quantization 和 output buffer 表达短期 scratch 所有权。
5. `QuantizedTensorDescriptor` 与 `QuantizedTensorView` 已校验 int8/int4 payload、奇数列 packed int4 长度和 per-row F32 scales，并提供有符号 int8/int4 元素视图。
6. `ExpertSlab` 已使用固定容量池化 slot 同时容纳 gate/up/down payload 与 scales，并在三个 payload 相邻时合并为一次读取。
7. M3 独立测试项目已包含随机 slice、相邻读取、F32/F16/BF16 转换、int4 奇数列、取消、workspace/resident/expert slab dispose、短文件、溢出和重复 handle 释放用例。

## 04. ✅ M4：scalar reference kernels

目标：建立简单、可读、可验证的标量实现，作为所有 SIMD kernel 的正确性基准。

已完成基础代码：

1. `ScalarKernels` 已提供 embedding gather、RMSNorm 与按需 LayerNorm，使用显式 shape、row stride 和 destination stride。
2. F32 matvec / matmul 已使用固定 row-major 循环顺序与 double accumulator，并覆盖带 padding stride 和空 batch。
3. int8 per-row 与 packed int4 per-row dequant matvec 已复用 M3 的 `QuantizedTensorView`，逐元素 F32 解量化后进入 double accumulator。
4. activation quantize-to-int8 已固定为对称 scale、`MidpointRounding.ToEven`、`[-127, 127]` 饱和范围，并处理全零与极小 activation。
5. SiLU、sigmoid、稳定 softmax、elementwise add/multiply、显式原地 residual 与 stable top-k 已建立；top-k 同分时选择较小 source index。
6. 未声明的输入输出 alias、非法 shape/stride/buffer、错误量化格式、非法 scale 与非有限 softmax/top-k/activation 输入会显式失败。
7. M4 独立测试项目已接入 solution，覆盖 tiny fixture embedding/RMSNorm checkpoint、LayerNorm、F32 stride、int8/int4 离线解量化、奇数列、ties-to-even、激活函数、alias、稳定 top-k、空 batch 与非法边界；测试执行统一留在 M14。
8. scalar path 不使用并行或 intrinsics，后续优化不得删除该路径。

## 05. ✅ M5：tokenizer、prompt 与增量解码

目标：在模型 forward 前先锁定文本与 token 的双向语义。

已完成基础代码：

1. `ManagedTokenizer` 已解析 WordLevel/BPE model、vocab、merge rank、added token、special token、unknown token、byte fallback 和 deterministic BPE 选项，并对文件、词表、token ID 和输入长度设置上限。
2. Unicode 文本已通过固定可逆 byte-level 映射转换为 UTF-8 byte sequence；decoder 支持 byte-level、byte fallback、Metaspace、BPE suffix 与 WordPiece 必要子集。
3. normalizer 已覆盖 Unicode normalization、lowercase、strip、accent、prepend、literal/regex replace 与 Bert 必要子集；pre-tokenizer 已覆盖 whitespace、sequence、split、Metaspace 与 ByteLevel 必要子集，未知组件显式失败。
4. added token 区分 normalized/non-normalized、special、single-word 与左右空白语义；普通文本编码不会隐式注入 special token。
5. `GlmPromptTemplate` 直接使用 GLM generation mask、start-of-prompt 和 system/user/assistant/observation role token，不复用通用 llama prompt builder，并暴露 EOS 与全部 role stop token。
6. `IncrementalTextDecoder` 使用有状态 UTF-8 decoder 保留跨 token byte 尾部，只向 streaming callback 发送完整字符；token stop 终止前会安全释放已完成文本。
7. 文本 stop 检查保留各 stop 的最长可能前缀，可识别跨 token stop sequence 且不向回调泄漏 stop 内容。
8. `ModelDirectoryProbe` 已加载完整 tokenizer 并校验最大 token ID 不超过模型 `vocab_size`；session 诊断显示 tokenizer 词表和 stop token 数量。
9. M5 独立测试项目已接入 solution，覆盖 tiny oracle、BPE merge、normalization、special token、byte roundtrip、中英文/代码/emoji、增量 UTF-8、跨 token stop 与 GLM role prompt；测试执行统一留在 M14。

## 06. ✅ M6：resident dense model

目标：加载每个 token 都需要的 dense 权重，并完成 embedding 到首层输入的基础路径。

resident 范围：

1. token embedding 与 lm head。
2. final norm。
3. 每层 input/post-attention norm。
4. MLA q/kv/o projections。
5. dense MLP layers。
6. MoE router、router correction bias 和 shared experts。
7. 可选 DSA/MTP 权重暂不进入首批 ready 条件。

已完成基础代码：

1. `ManagedGlmModel` 已持有配置、tokenizer、tensor catalog、只读 shard data source 和 resident weights，并与 `ManagedGlmSession` 生命周期分离。
2. resident layout 已按配置校验 embedding、lm head、norm、MLA projection、dense MLP、router、correction bias 与 shared expert 的精确 shape；routed expert、DSA 和 MTP 权重不进入 M6 resident 集合。
3. load 前已按 F32 resident 表示计算 resident bytes，按 compressed KV 公式计算 KV bytes，并计算 dense/attention/output scratch bytes；总预算超过进程可用内存时在打开 shard 和读取 payload 前失败。
4. F32、F16 与 BF16 resident 权重复用 M3 conversion 和所有权类型；未声明 payload/scale 布局的 int8/int4 resident 权重显式拒绝，不误读为 F32。
5. 部分加载失败、取消和重复 dispose 会释放已分配 resident buffer 与全部 shard handle；转换临时 buffer 继续由 M3 的 bounded read 路径在 `finally` 中归还。
6. 已接通 embedding gather、逐层 input RMSNorm 和 dense SwiGLU MLP scalar 基础路径；tiny fixture generator 升级到 1.1.0，并增加独立 dense MLP oracle checkpoint，既有 MoE teacher-forcing 基线保持不变。
7. managed session 已报告 resident tensor、resident/KV/scratch bytes、内存预算与 open shard 数；M6 阶段的 forward 禁用边界已在 M9 由真实生成路径替代。
8. M6 独立测试项目已接入 solution，覆盖 resident shape、预计/实际 resident bytes、预算先于 payload 读取、损坏 shard、取消、资源释放、routed expert 非 resident，以及 embedding、RMSNorm、dense MLP oracle；测试执行统一留在 M14。

## 07. ✅ M7：MLA attention 与 compressed KV cache

目标：实现模型要求的 MLA attention，并避免按完整 K/V head 保存上下文。

已完成基础代码：

1. `KvCache` 已按 layer 使用独立连续 buffer 保存 normalized KV latent 与旋转后的共享 RoPE key；实际字节数与 M6 的 compressed KV 预算公式一致，不保存完整 K/V head。
2. `SequenceState` 已按 attention layer 维护 position、有效 token 数、上下文上限与 cache 起点，并在写入前拒绝 layer 不匹配、越过 context limit 或状态不一致的追加。
3. `AttentionWorkspace` 已使用固定容量池化 activation 与 score buffer，覆盖 q latent、q heads、KV expansion、context 和原子 output projection；容量由 `ModelMemoryPlan` 按配置预计算。
4. q_a/q_b 与 kv_a/kv_b projection、q_a/kv_a RMSNorm、nope/rope/value 拆分和 interleaved partial RoPE 已接通；奇数 RoPE 维度在配置 probe 阶段拒绝。
5. reference path 已从 compressed latent 逐位置重建 K/V，执行逐 head causal score、稳定 softmax、context 聚合与 output projection，并对非有限 score/output 显式失败。
6. absorbed path 已把 nope query 投影到 KV latent 空间，并先在 latent 空间聚合 value，再执行 value projection；reference path 继续保留为后续 SIMD 与优化的正确性基线。
7. 单 token decode 与多 token prefill 已复用同一事务边界；prefill 后 decode、一次性 prefill 和 absorbed/reference 模式共享一致的 cache/state 语义。
8. cancellation 只在完整 KV entry 写入前后或计算安全点生效；token 失败回滚当前 entry，prefill 失败回滚本轮全部新增 entry，destination 只在单 token 计算完成后写回。
9. M7 独立测试项目已接入 solution，覆盖 tiny oracle 全部 attention checkpoint、compressed KV 内容/字节公式、prefill/decode 与 absorbed/reference 一致性、interleaved RoPE、上下文上限、非有限输入、取消和回滚；测试执行统一留在 M14。

## 08. ✅ M8：MoE 与 expert streaming

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
6. 复用 M3 的 descriptor、data source、量化视图和 expert slab，不在 expert streaming 中重复解析 safetensors header。

已完成基础代码：

1. `GlmModelConfiguration` 已读取 `norm_topk_prob`，并保持 `n_group=1` / `topk_group=1` 的首批边界；router 使用 resident F32 matvec、sigmoid、correction bias、稳定 top-k、可选概率归一化和 routed scaling factor。
2. `ExpertDescriptorLayout` 已在模型加载阶段验证全部 routed expert 的 layer/expert ID、gate/up/down shape、manifest 量化格式和物理长度；int8/int4 固定使用同前缀 `*.scales` 的 per-row F32 sidecar。
3. F32/F16/BF16 routed expert 使用固定容量池化 F32 buffer；int8/int4 routed expert 复用 M3 `ExpertSlab` 与 M4 dequant matvec，不把 routed expert 加入 resident weight 集合。
4. `MoeWorkspace` 已为 router score、top-k ID/weight、shared/routed activation 和原子 output 提供固定池化容量，其字节数进入 `ModelMemoryPlan` scratch 预算。
5. `ExpertCache` 已按 MoE layer 预分配相同容量的固定 slot；`ExpertKey` 包含 layer、expert ID 和格式，lease 在并发 forward 期间阻止 slot 被复用，未租用 slot 只在 layer 获取安全点按 LRU 淘汰。
6. expert cache RAM 配额会先扣除 resident、KV、scratch 和配置的安全余量，并要求每个 MoE layer 至少容纳一个完整 top-k working set；预算不足时返回包含请求值、最小值和可用值的诊断。
7. miss 已进入固定 worker 数的 bounded `Channel`；同 layer 重复 expert ID 在入队前合并，并发同 key miss 共享 completion，不为单个 expert 创建线程或无界任务。
8. shared expert 会在 routed expert I/O 进行期间先执行；取消会释放尚未开始读取的 lease，已经开始的读取完成后保留在 slot，destination 只在全部 expert 成功且输出有限时提交。
9. cache snapshot 已记录 hit、miss、eviction、disk reads/bytes、磁盘耗时、前台等待时间和长期 usage histogram；session 诊断显示 MoE workspace、expert 格式和单 slot 字节数。
10. M8 独立测试项目已接入 solution，覆盖 tiny router/MoE oracle、cold/hot cache、batch expert union、lease 阻塞、LRU eviction、RAM 配额、取消、非归一化 routing 和 workspace accounting；测试执行统一留在 M14。

## 09. ✅ M9：完整 forward 与生成

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

已完成基础代码：

1. `ManagedForwardContext` 已按 embedding、逐层 input RMSNorm、MLA attention、attention residual、post-attention RMSNorm、dense MLP 或 MoE、第二次 residual、final RMSNorm 和 lm head 的顺序接通完整 scalar forward。
2. prompt prefill 使用最大 32 token 的有界批次，跨批次和单 token decode 复用按层 compressed KV cache 与 sequence position，不按完整上下文分配 hidden-state 矩阵。
3. dense 层复用 resident SwiGLU 路径；MoE 层复用 router、shared expert、streamed routed expert 和 session 级最小 top-k expert cache，不新增 native 依赖。
4. `ModelMemoryPlan` 已纳入 tensor、attention、forward batch、sampling 和 MoE workspace；expert cache 继续在 resident、KV 和 scratch 预算之后校验每层最小 top-k working set。
5. `TokenSampler` 已实现稳定 greedy、temperature、top-k、top-p、repeat/frequency/presence penalty 和固定 seed 的确定性伪随机序列，并保持 token ID 稳定 tie-breaking。
6. `ManagedTextGenerator` 已接通 prompt prefill、增量 decode、多 EOS、跨 token 文本 stop、max output token、context limit、cancellation 和增量文本 callback。
7. prompt token 与最大输出 token 会在创建 forward context 和读取 expert 前完成总上下文预检；取消、上下文超限和 forward 异常不会返回占位 completion。
8. managed session 已从 resident-only 状态切换为 `managed-glm-generation`，只在成功后累计 request、prompt token 和 completion token，并报告 forward、sampling、expert cache 与 I/O 诊断。
9. `managed_context_length_exceeded` 与 `managed_inference_failed` 已作为 M9 生成失败边界；现有 llama.cpp provider 的选择和执行路径不变。
10. M9 独立测试项目已接入 solution，覆盖 tiny teacher forcing logits、多 token prefill、greedy token oracle、固定 seed sampling、penalty、session usage、上下文写入前失败、取消前零 expert I/O 和失败后 context 禁止复用；测试执行统一留在 M14。

## 10. 🚧 M10：Tomur 集成闭环

目标：让 managed provider 通过现有 OpenAI、Ollama 和 Anthropic Messages 入口工作，不增加另一套 API。

实现：

1. `SessionManager` 继续按模型 provider 选择 session。
2. `GET /v1/models` 与 `/api/tags` 只展示清单和资产校验通过的模型。
3. `/v1/chat/completions`、`/api/chat` 和 `/v1/messages` 复用现有请求/streaming 响应。
4. `tomur ps` 显示 provider、模型、resident bytes、KV bytes、expert cache 和请求统计。
5. `tomur doctor` 显示 provider DLL、manifest、配置、tokenizer、shards、内存预算和格式诊断。
6. Runtime API/UI 区分 provider discovered、model metadata valid、model assets complete、forward verified 和 session loaded。
7. unload 同时释放 session、file handles、pooled buffers 和 expert cache。
8. 通过可选 Chat session 契约把结构化 role/content 直接交给 `GlmPromptTemplate.BuildChat`；不先套用 llama/ChatML 文本模板。
9. 对显式声明 `packed-offset` 的目录加载量化 resident matrix 与 routed expert，统一解释 `*.qs` per-row scale、U8 int8 payload 和 offset-binary packed int4。
10. 使用转换后的 tiny `glm_moe_dsa` 目录完成 Catalog、provider load、completion/chat、streaming callback 和兼容 API smoke；tiny 随机权重只证明格式与调用链，不作为自然语言质量证据。
11. 完整 GLM-5.2 转换模型仍需独立记录约 370 GB 资产、内存预算、真实 prompt、首 token、token/s 和 expert I/O 证据。
12. 以 extend-only 方式增加 `glm4_moe_lite` manifest architecture，并要求其与 `config.json:model_type` 一致；配置必须显式符合当前 MLA、interleaved RoPE、SwiGLU、sigmoid/noaux router 与 dense/sparse layer 边界。
13. `GlmPromptTemplate` 按 architecture 保留既有 GLM-5.2 行为，并为 `glm4_moe_lite` 对齐官方 Jinja 的 prefix 换行、历史 assistant thinking closure、默认非 thinking generation prompt 与 tool response 包装。
14. `cerebras/GLM-4.7-Flash-REAP-23B-A3B` 的 BF16 到 packed rowwise int4/int8 转换与完整模型 smoke 不在当前本机执行；异机证据形成前，`glm4_moe_lite` 保持代码已接入但完整模型待验证状态。

本轮基础代码：

1. ✅ 已增加可选 `IModelReadinessProvider` 契约；GLM 与 OLMoE 可在不读取 resident payload、不执行 forward 的情况下报告 architecture、quantization、tensor 数量、resident/KV/scratch/expert cache 计划和当前内存预算。
2. ✅ `LocalModelCatalog` 已区分候选模型与兼容 API 可见模型；provider、metadata 或必要资产校验未通过的 managed 模型不会进入 `GET /v1/models`、`/api/tags` 和协议模型选择路径，但仍保留在 Runtime/doctor 诊断中。
3. ✅ OpenAI 与 Anthropic 继续使用现有 SSE 增量回调；Ollama `generate` 与 `chat` 已从单条最终 NDJSON 调整为增量 `done=false` 与最终 usage/duration `done=true` 帧，流中错误保持 Ollama 结构化诊断。
4. ✅ `SessionManager` 已拆分状态锁与单请求执行门；unload 会先取消活动生成，再释放 session、tensor file handle、KV/workspace 与 expert cache，并以 `session_unloaded` 返回仍连接的协议请求。
5. ✅ session 快照已结构化报告 provider、architecture、context、resident/KV/scratch、expert cache hit/miss/eviction、expert I/O、busy、请求/token 计数与最近错误；`tomur ps`、`tomur doctor`、Runtime API 和 Web Runtime 复用同一状态。
6. ✅ M10 独立测试项目已加入 solution，覆盖 Ollama 增量/终帧、readiness 内存计划、不完整 managed 模型可见性拦截、成功 session 的 forward verified 状态和 unload 取消/释放；本轮未执行构建或测试，验证仍归 M14 与 14.4。

当前证据：`packed-offset` 格式、量化 resident/expert forward、OpenAI Chat 非流式与 SSE、Ollama Chat、Anthropic Messages 已通过转换后的随机 tiny 模型 smoke；默认 int8 embedding/lm_head 与 int4 dense/expert 的混合精度目录也已完成 OpenAI Chat smoke。完整模型、自然语言质量、完整 oracle、跨平台和性能验证仍未执行，记录见 [R15 packed GLM smoke](../../docs/r15-packed-glm-smoke.md)。

错误码：

1. `managed_provider_unavailable`
2. `managed_provider_load_failed`
3. `managed_provider_probe_failed`
4. `managed_model_invalid`
5. `managed_model_assets_incomplete`
6. `managed_quantization_unsupported`
7. `managed_model_memory_budget_exceeded`
8. `managed_model_out_of_memory`
9. `managed_context_length_exceeded`
10. `managed_inference_failed`
11. `session_unloading`
12. `session_unloaded`

## 11. 🚧 M11：性能优化

目标：在不改变模型语义的前提下提高 CPU、RAM 和磁盘利用率。

已落地：

1. ✅ 保留 `ScalarKernels` oracle 基线，并增加 `OptimizedKernels` shape dispatch；`TOMUR_GLM_KERNEL_MODE=scalar` 可强制回退原路径。
2. ✅ 使用硬件加速的 `Vector<float>` 宽度选择覆盖 `Vector128/256/512` 的 F32、int8 与 packed int4 matvec；量化权重直接按 row scale 计算，不展开完整矩阵。
3. ✅ F32 matvec 根据 rows、总 work 与并行度阈值选择同步 `Parallel.For`，小 shape 和禁用配置保持单线程。
4. ✅ resident dense、shared expert 与 routed expert 的 gate/up projection 已合并为 paired dispatch，固定 tensor/attention/MoE workspace 继续位于 token loop 外。
5. ✅ expert cache 已按 RAM budget 在 top-k 最小 working set 之上计算有界 per-layer capacity，使用 usage histogram 保护 hot experts，并提供不累计模型 usage 的显式异步 prefetch 边界。
6. ✅ cache acquire 热路径已移除 LINQ 排序和 `HashSet`，使用有界数组去重与单次 slot 扫描；session 诊断增加 kernel、hot pin 与 prefetch 状态。
7. ✅ M11 独立测试项目已加入 solution，覆盖 scalar fallback、F32 SIMD/强制并行、int8/int4、paired dispatch、自动 cache capacity、prefetch 与 hot eviction；本轮未执行构建或测试。

剩余顺序：

1. 建立 scalar 基准和阶段耗时分解。
2. 评估 activation int8 quantization 与 integer dot-product；未经 oracle 与性能证据不接入默认 projection。
3. 对 prefill 的 unique experts 做 batch union，并将显式 prefetch 边界接到下一批已知 expert。
4. 建立内存映射和复杂 I/O 的可切换实验边界，未经 M14 性能证据不设为默认路径。
5. 在 M14 对 SIMD、并行、cache 与 I/O 路径逐项执行 oracle、吞吐、allocation 和跨平台验证。

## 12. ⏳ M12：高级能力

代码依赖：M9 与 M11 的基础实现完成；正确性和性能门槛统一在 M14 验证。

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

## 14. ⏳ M14：集中测试、完整模型验证与发布证据

目标：在 M0-M13 基础代码全部完成后，集中执行编译、analyzer、自动化测试、oracle 对齐、跨平台实跑、性能测量、完整模型推理和发布验证，并形成可复现证据。

启动条件：

1. M0-M13 的基础代码均已标记为 ✅。
2. 用户明确要求开始验证。
3. 验证失败只回退对应实现阶段修复代码，不把未通过能力标记为 ready。

当前状态：⏳ 尚未执行集中验证。

### 14.1 ⏳ 构建与静态检查

1. 编译主程序、provider 和全部 Managed GLM 测试项目。
2. 逐项处理编译器、nullable、trimming、AOT 和 analyzer 问题，不使用 blanket suppression。
3. 确认 provider 项目不包含 `DllImport`、`LibraryImport`、`NativeLibrary.Load` 或第三方 native 推理依赖。
4. 核对根 README、README.en、ROADMAP、AGENTS、provider README 与实现状态一致。

### 14.2 ⏳ M1-M3 基础层验证

1. 执行 M1 回归，覆盖合法 probe、坏 manifest、越界 offset、缺 tensor、坏 DLL、契约缺失、激活失败、非法 ID、重复 ID 和缺 provider 诊断。
2. 从默认 `providers/` 目录与 `TOMUR_PROVIDER_PATH` 分别实跑 provider discovery，并通过 `tomur doctor`、`GET /api/runtime/status` 和 Web Runtime 核对状态、路径与修复动作。
3. 确认 llama.cpp 模型仍进入原 session 路径，managed provider 或非法模型失败不会中断 Catalog、服务进程或 native provider。
4. 确认 probe 只读取文件长度与 safetensors header，不读取 tensor payload，也不分配完整模型内存。
5. 执行 M2 回归，在两个干净目录生成 fixture 并逐字节比较全部文件；实跑隐藏 CLI 的 generate/verify 成功与篡改失败路径。
6. 使用独立实现复核 oracle 的 attention、router、teacher-forcing 与 greedy checkpoint，并确认 schema、配置摘要、工具版本和 checksum 完整。
7. 执行 M3 回归，覆盖随机 slice、相邻读取、F32/F16/BF16 转换、int4 奇数列、量化 shape、取消、短文件、溢出和重复 dispose。
8. 在 Windows 与 Linux 重复创建/销毁多 shard data source 和 session，确认 EOF、短读、偏移溢出、disposed handle 诊断以及文件句柄释放。
9. 确认 probe、resident load 与 expert streaming 共用同一 tensor descriptor，不重复解析 safetensors header。

### 14.3 ⏳ M4-M9 正确性验证

1. 对 M4 scalar kernels 执行 F32 oracle 误差、int8/int4 离线解量化一致性、奇数列、非 SIMD 对齐、空 batch、最小 shape 和非法 shape 边界测试。
2. 对 M5 tokenizer 执行中英文、代码、emoji、空白、控制字符、special token、byte roundtrip、增量 UTF-8 和跨 token stop sequence 测试。
3. 对 M6 resident model 核对预计与实际内存、损坏 dense tensor、加载取消、资源释放，以及 embedding、norm、dense MLP 单层 oracle 输出。
4. 对 M7 核对单层 MLA 中间值、teacher forcing logits、prefill 后 decode 与纯逐 token 路径，以及 compressed KV 字节公式。
5. 对 M8 核对 router ID、路由权重、单层 MoE、shared/routed expert 合并、cold/hot cache 输出、eviction 稳定性、I/O 失败诊断和并发 miss 内存上限。
6. 对 M9 核对 tiny teacher forcing、greedy token 序列、固定 seed sampling、context 写入前检查、取消与异常路径，确认不产生伪造 completion。

### 14.4 ⏳ M10 集成验证

1. 通过 `/v1/chat/completions`、`/api/chat` 与 `/v1/messages` 验证 managed 与 llama.cpp 模型在同一 API 表面正确路由。
2. 验证非流式、streaming、客户端断开、取消、unload 与服务重启，并确认 file handle、pooled buffer、KV cache 和 expert cache 被回收。
3. 核对 `GET /v1/models`、`/api/tags`、`tomur ps`、`tomur doctor`、Runtime API 与 Web UI 的 provider、模型 readiness、内存、缓存、I/O 和诊断状态。
4. 未通过完整验证的模型不得显示为 ready；managed provider 失败不得影响 llama.cpp 请求。
5. 覆盖缺模型、缺 provider、坏 manifest、缺 shard、量化不支持、内存不足、上下文超限、磁盘错误和 forward 失败的稳定错误码。

### 14.5 ⏳ M11 性能验证

1. 所有 SIMD、并行、缓存和 I/O 优化均与 scalar path 比较中间输出，并可通过配置关闭和回退。
2. 记录 CPU 架构、指令集、prompt、context、batch、cache 状态、磁盘状态，以及 cold/warm/hot 三类结果。
3. 记录 probe、resident load、首 token、prompt tokens/s、decode tokens/s、每 token expert reads、磁盘吞吐、foreground wait 与 cache hit rate。
4. 记录 RSS、managed heap、pinned bytes、pooled bytes、GC pause 和每 token allocation。
5. 内存映射或复杂 I/O 路径只有在证据显示收益且不改变输出时才能设为默认。

### 14.6 ⏳ M12 高级能力验证

1. 禁用高级能力时回到基础路径。
2. DSA 在保留全部 key 时与 dense attention 一致。
3. speculative decoding 不改变采样分布。
4. KV 恢复与不中断会话输出一致。
5. DSA、MTP、grammar、prefetch 和 KV persistence 均不得绕过内存预算、取消或模型资产校验。

### 14.7 ⏳ M13 发布与兼容验证

1. 非 AOT 自包含包可从默认目录发现带 checksum 的 provider DLL。
2. 缺 provider DLL 或 provider 版本不匹配时，主程序与 native providers 继续运行并返回明确诊断。
3. Native AOT 包按最终策略静态包含 provider，或返回 `dynamic_managed_providers_unavailable`，行为与文档一致。
4. 使用旧版 `model.tomur.json` fixture 验证 extend-only contract、manifest schema 和稳定诊断 code。

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
| tokenizer 不一致 | 所有输出失真 | 在 M14 中独立验证 tokenizer 后再开放 forward |
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
