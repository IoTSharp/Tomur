# Managed GLM Provider

This project contains Tomur's pure C# provider for explicitly marked GLM / MoE model directories. It does not load a third-party inference dynamic library and does not replace the existing llama.cpp provider.

The M1-M10 foundation code is in place: model metadata probing, safetensors indexing, fixed-seed tiny oracle fixture generation, bounded tensor storage, numeric conversion, scalar reference kernels, tokenizer and prompt pipelines, resident dense model loading, MLA reference/absorbed attention, compressed KV cache, bounded MoE expert streaming, full scalar forward, bounded-batch prefill, incremental decode, sampling, penalties, stop handling, cancellation, and incremental text callbacks. M10 integration is complete in code: packed rowwise int4/int8 loading, structured Chat roles, managed-model readiness, compatibility API visibility checks, incremental Ollama NDJSON, cancellable unload, and structured session resources are connected. M11 performance foundations add shape-aware SIMD kernels, bounded parallelism, automatic expert-cache capacity, hot pinning, prefetch, timing, and an mmap experiment boundary. M12 advanced foundations add DSA/MTP asset probing, stable top-k selection for indexer scores with a dense-equivalent runtime gate, an optional resident MTP head and single-step draft boundary, speculative rejection sampling, forced token spans, router lookahead, live expert repinning, checksummed compressed-KV persistence, and isolated KV forks. Unvalidated sparse DSA never substitutes attention scores for indexer scores. Production MLA now defaults to the absorbed path while retaining the reference path as an explicit oracle. A full GLM-4.7 1-token completion passed on Linux and improved from 186.596971 seconds to 26.595764 seconds with the same generated token; one real non-streaming Web Chat exchange and one active-request unload cancellation also passed. This is targeted evidence, not language-quality, sustained-throughput, full protocol, cross-platform, or release validation, so managed generation remains experimental.

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

`architecture` may be `glm_moe_dsa` or `glm4_moe_lite`. The latter uses an architecture-specific prompt path aligned with the published GLM-4.7 template: a newline after `[gMASK]<sop>`, `</think>` for non-thinking assistant turns, and `<tool_response>` wrapping for tool output. Real `glm4_moe_lite` weights have passed conversion, load, readiness, a shortest non-streaming completion, one Web Chat exchange, and one active-request unload cancellation through Tomur; the evidence and remaining smoke matrix are maintained in [R15 GLM4 MoE Lite validation](../../docs/r15-glm4-moe-lite-validation.md).

`quantization_layout` is optional and extend-only. The default `separate-scales` layout keeps the original `*.scales` F32 sidecars and two's-complement int4 semantics. The explicit `packed-offset` layout uses `*.qs` F32 per-row scales, U8 int8 payloads, and offset-binary packed int4 values. Payload length selects int8 or int4 per matrix, which permits int8 embedding/lm_head tensors alongside int4 dense and routed-expert tensors. Routed expert payloads remain on disk until selected by the MoE router; resident weights, KV memory, scratch memory, and the configured expert-cache safety margin are deducted before cache slots are allocated.

Targeted format and API evidence is recorded in [R15 packed GLM smoke](../../docs/r15-packed-glm-smoke.md). The full GLM-5.2 fixed inventory is 357.4 GiB; a Linux validation directory now contains all 150 formal files and no partial files, but its final size and checksum audit remains pending after one downloader range reported failure. GLM4 MoE Lite has targeted real-model P0 evidence, while its full protocol and performance matrix remains pending.

For development and release builds, `app/Tomur.csproj` references this project directly. `ModelProviderRegistry` registers `ManagedGlmProvider` statically at process startup, so no provider path environment variable, external provider directory, reflection discovery, or post-publish DLL copy is required. The shared host/provider contracts live in `providers/Abstractions` and apply to both Native AOT and non-AOT builds.

Kernel selection defaults to `auto`. Set `TOMUR_GLM_KERNEL_MODE=scalar` to force the scalar oracle path. `TOMUR_GLM_PARALLELISM` limits F32 matvec parallelism (`0` uses the processor count and `1` disables it); `TOMUR_GLM_PARALLEL_ROW_THRESHOLD` and `TOMUR_GLM_PARALLEL_WORK_THRESHOLD` control shape dispatch. These switches do not change model assets or compatibility API contracts.

On AVX2 systems, packed int4/int8 matvec uses batched byte/nibble expansion and 8-lane float accumulation. Other systems retain the portable vector or scalar reference path. Runtime diagnostics distinguish this managed CPU kernel from llama.cpp accelerator state and expose active forward stage/layer progress. M14 now contains a targeted AVX2 full-model P0 measurement and tiny oracle coverage; cold/warm/hot benchmarks, sustained decode, allocation, protocol, resource, and cross-platform validation remain pending.
