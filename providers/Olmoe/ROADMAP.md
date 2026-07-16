# Managed OLMoE Provider Roadmap

本文记录 OLMoE 小模型在 Tomur R15 中的实现与验证边界。provider 使用独立 `Tomur.Providers.Olmoe` 程序集、`Tomur.Providers.Olmoe` 命名空间和 `managed-olmoe` provider ID；它复用 Tomur 已有的纯 C# safetensors、tokenizer、scalar kernel 与 expert-cache 基础层，不引入 native dynamic library、独立服务或另一套 HTTP API。

## 当前目标

首个真实模型为 `allenai/OLMoE-1B-7B-0125-Instruct`，属于 `olmoe` 架构的 7B total / 1B active instruct 模型。原始 BF16 权重约 12.89 GiB，已完成中文 Ollama 非流式真实对话 smoke；转换代码保持 resident dense 权重的原始 dtype，并将全部 routed expert gate/up/down 投影写为 signed int8 payload 与 `*.qs` F32 per-row scale。既有实跑数据见 [R15 OLMoE real-model smoke](../../docs/r15-olmoe-smoke.md)，O5 待验证矩阵见 [O5 validation record](../../docs/r15-olmoe-o5-validation.md)。

模型清单使用显式架构和量化布局：

```json
{
  "schema_version": 1,
  "provider": "managed-olmoe",
  "architecture": "olmoe",
  "display_name": "Local OLMoE Instruct",
  "config": "config.json",
  "tokenizer": "tokenizer.json",
  "tensor_pattern": "*.safetensors",
  "quantization": "int8",
  "quantization_layout": "rowwise-qs",
  "capabilities": ["completion", "chat"]
}
```

## 阶段

| 顺序 | 阶段 | 状态 | 范围 |
| --- | --- | --- | --- |
| 01 | O1 | ✅ | 独立 provider、config/probe、resident 与 expert tensor layout |
| 02 | O2 | ✅ | 标准 causal attention、full KV cache、softmax top-k router 与完整 forward |
| 03 | O3 | ✅ | tokenizer、官方 chat template、生成与兼容 API 接线 |
| 04 | O4 | ✅ | tiny fixture、错误边界、内存核算与自动化回归代码 |
| 05 | O5 | 🚧 | 转换、协议矩阵、streaming 与性能诊断代码已接入；完整 int8 真实模型证据仍待执行 |

## 实现边界

1. config 必须显式校验 `model_type=olmoe`、hidden/layer/head/KV-head/expert/top-k/intermediate/vocab/context、RoPE 与 RMSNorm 参数。
2. resident 权重包含 embedding、lm head、final/layer norms、q/k/v/o projections、q/k norms 和 router；routed experts 保持磁盘按需读取。
3. attention 使用 causal full K/V cache；首批模型要求 `num_attention_heads` 可被 `num_key_value_heads` 整除，并按 KV-head 映射 query heads。
4. router 对全部 expert logits 执行稳定 softmax，再取 top-k；只在 `norm_topk_prob=true` 时对已选权重再次归一化。
5. `rowwise-qs` 表示 signed int8 payload、`*.qs` F32 row scale 和 two's-complement 解码，不复用 GLM `packed-offset` 的 offset-binary int4 语义。
6. Chat prompt 必须遵循 tokenizer 声明的 system/user/assistant 换行模板、BOS/EOS 和 generation prompt，不复用 GLM generation-mask 模板。
7. 不支持的 config、张量 shape/dtype、tokenizer、上下文或内存预算必须在读取完整 payload或 forward 前返回明确诊断，不生成占位 token。
8. O5 转换按行读取 expert matrix，使用有界 buffer 生成 signed int8 与 F32 scale；输出先写入同盘临时目录，完成 safetensors probe、输入/输出 SHA-256 清单后再原子发布，不覆盖既有目录。
9. 性能口径固定为模型加载耗时、首个采样 token 延迟、端到端 output token/s，以及至少生成两个 token 时可计算的首 token 后 decode token/s。

## 验收

1. ✅ tiny OLMoE fixture 已建立独立 scalar reference，覆盖 embedding、attention、router、MoE、逐 token teacher forcing 与 greedy oracle；F32 和 signed-int8 expert 共用同一逻辑权重基线。
2. 🚧 tiny OLMoE session 的 OpenAI、Ollama 与 Anthropic 非流式响应、OpenAI/Anthropic SSE、Ollama NDJSON 增量与 usage 终帧回归代码已接入；真实 instruct 模型协议 smoke 尚未执行。
3. 🚧 readiness、session 与 fixture 回归已统一核对 resident/KV/scratch/minimum expert cache 总账；session 快照已增加加载、首 token、总生成、output token/s 与 decode token/s 字段，真实转换产物的数值证据仍待执行。
4. ✅ 已建立预算先于 payload 读取、损坏 shard、shape/quantization/tokenizer、context/token/cancellation、faulted forward 和重复 dispose 回归代码，并检查 shard handle 可独占重开。
5. ✅ OLMoE 模型错误保持 provider 级结构化诊断，不修改 `managed-glm` 与现有 llama.cpp provider 的选择和运行路径。
6. 真实模型与 API smoke 完成前，README 只能标记为进行中，不得写成已可用于真实聊天。

O4/O5 自动化回归代码已接入现有 `Tomur.Providers.Olmoe.Tests` 项目；本轮未执行构建、测试、模型转换或服务 smoke，以上状态只表示代码完成，不表示新增回归或真实模型矩阵已有执行证据。
