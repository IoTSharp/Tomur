# Managed OLMoE Provider Roadmap

本文记录 OLMoE 小模型在 Tomur R15 中的实现与验证边界。provider 使用独立 `Tomur.Providers.Olmoe` 程序集、`Tomur.Providers.Olmoe` 命名空间和 `managed-olmoe` provider ID；它复用 Tomur 已有的纯 C# safetensors、tokenizer、scalar kernel 与 expert-cache 基础层，不引入 native dynamic library、独立服务或另一套 HTTP API。

## 当前目标

首个真实模型为 `allenai/OLMoE-1B-7B-0125-Instruct`，属于 `olmoe` 架构的 7B total / 1B active instruct 模型。原始 BF16 权重约 12.89 GiB，已完成中文 Ollama 非流式真实对话 smoke；转换后计划保持 dense 权重为 BF16，并让 routed expert 使用 signed int8 payload 与 `*.qs` F32 per-row scale。实跑数据见 [R15 OLMoE real-model smoke](../../docs/r15-olmoe-smoke.md)。

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
| 04 | O4 | 🚧 | tiny fixture、错误边界、内存核算与自动化回归 |
| 05 | O5 | 🚧 | 真实 BF16 对话已通过；完整 int8 转换、协议矩阵、streaming 与性能证据仍待完成 |

## 实现边界

1. config 必须显式校验 `model_type=olmoe`、hidden/layer/head/KV-head/expert/top-k/intermediate/vocab/context、RoPE 与 RMSNorm 参数。
2. resident 权重包含 embedding、lm head、final/layer norms、q/k/v/o projections、q/k norms 和 router；routed experts 保持磁盘按需读取。
3. attention 使用 causal full K/V cache；首批模型要求 `num_attention_heads` 可被 `num_key_value_heads` 整除，并按 KV-head 映射 query heads。
4. router 对全部 expert logits 执行稳定 softmax，再取 top-k；只在 `norm_topk_prob=true` 时对已选权重再次归一化。
5. `rowwise-qs` 表示 signed int8 payload、`*.qs` F32 row scale 和 two's-complement 解码，不复用 GLM `packed-offset` 的 offset-binary int4 语义。
6. Chat prompt 必须遵循 tokenizer 声明的 system/user/assistant 换行模板、BOS/EOS 和 generation prompt，不复用 GLM generation-mask 模板。
7. 不支持的 config、张量 shape/dtype、tokenizer、上下文或内存预算必须在读取完整 payload或 forward 前返回明确诊断，不生成占位 token。

## 验收

1. tiny OLMoE fixture 通过 embedding、attention、router、MoE、teacher forcing 与 greedy oracle。
2. 真实 instruct 模型通过 Catalog、provider load、OpenAI Chat、Ollama Chat、Anthropic Messages 和 SSE streaming。
3. 记录原始/转换后资产大小、resident/KV/cache/scratch 预算、加载时间、prompt/completion token、首 token 和 token/s。
4. unload 后释放 shard handles、resident buffers、KV cache、workspace 与 expert cache。
5. `managed-olmoe` 失败不影响 `managed-glm` 与现有 llama.cpp provider。
6. 真实模型与 API smoke 完成前，README 只能标记为进行中，不得写成已可用于真实聊天。
