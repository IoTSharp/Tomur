# Managed GLM Provider

This project contains Tomur's pure C# provider for explicitly marked GLM / MoE model directories. It does not load a third-party inference dynamic library and does not replace the existing llama.cpp provider.

The M1-M9 foundation code is in place: model metadata probing, safetensors indexing, fixed-seed tiny oracle fixture generation, bounded tensor storage, numeric conversion, scalar reference kernels, tokenizer and prompt pipelines, resident dense model loading, MLA reference/absorbed attention, compressed KV cache, bounded MoE expert streaming, full scalar forward, bounded-batch prefill, incremental decode, sampling, penalties, stop handling, cancellation, and incremental text callbacks. M10 integration is in progress: packed rowwise int4/int8 loading, structured Chat roles, managed-model readiness, compatibility API visibility checks, incremental Ollama NDJSON, cancellable unload, and structured session resources are connected in code. M11 performance work now includes shape-aware SIMD F32/int8/int4 matvec, bounded F32 row parallelism, paired gate/up projection dispatch, automatic expert-cache capacity, usage-based hot pinning, and explicit expert prefetch. Stage timing, activation integer dot-product, prefill-wide expert union, mmap experiments, and all performance validation remain pending. The existing OpenAI, Ollama, and Anthropic Messages paths retain targeted random-tiny smoke evidence, while the new M10/M11 code has not yet been executed. The provider accepts explicit `glm_moe_dsa` and `glm4_moe_lite` manifests only when `config.json:model_type` and the implemented MLA/MoE semantics match. Existing evidence validates format and forward execution, not language quality. Full-model, full-oracle, cross-platform, performance, and release validation remain in M14, so managed generation stays experimental.

The complete implementation sequence, validation gates, performance work, and release criteria are maintained in [ROADMAP.md](./ROADMAP.md).

Each model directory must contain `model.tomur.json`:

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

`architecture` may be `glm_moe_dsa` or `glm4_moe_lite`. The latter uses an architecture-specific prompt path aligned with the published GLM-4.7 template: a newline after `[gMASK]<sop>`, `</think>` for non-thinking assistant turns, and `<tool_response>` wrapping for tool output. Real `glm4_moe_lite` weights have not yet been loaded through Tomur; the reproducible conversion and smoke checklist is maintained in [R15 GLM4 MoE Lite validation](../../docs/r15-glm4-moe-lite-validation.md).

`quantization_layout` is optional and extend-only. The default `separate-scales` layout keeps the original `*.scales` F32 sidecars and two's-complement int4 semantics. The explicit `packed-offset` layout uses `*.qs` F32 per-row scales, U8 int8 payloads, and offset-binary packed int4 values. Payload length selects int8 or int4 per matrix, which permits int8 embedding/lm_head tensors alongside int4 dense and routed-expert tensors. Routed expert payloads remain on disk until selected by the MoE router; resident weights, KV memory, scratch memory, and the configured expert-cache safety margin are deducted before cache slots are allocated.

Targeted format and API evidence is recorded in [R15 packed GLM smoke](../../docs/r15-packed-glm-smoke.md). The pre-converted full GLM-5.2 model is 357.4 GiB and has not been loaded on the current machine because no local disk has enough free space. GLM4 MoE Lite real-model conversion and smoke are also pending on a separate machine.

For development, point `TOMUR_PROVIDER_PATH` at the directory containing `Tomur.Providers.Glm.dll`. Non-AOT release packaging will later copy approved provider assemblies into the `providers` directory beside the main application.

Kernel selection defaults to `auto`. Set `TOMUR_GLM_KERNEL_MODE=scalar` to force the scalar oracle path. `TOMUR_GLM_PARALLELISM` limits F32 matvec parallelism (`0` uses the processor count and `1` disables it); `TOMUR_GLM_PARALLEL_ROW_THRESHOLD` and `TOMUR_GLM_PARALLEL_WORK_THRESHOLD` control shape dispatch. These switches do not change model assets or compatibility API contracts.
