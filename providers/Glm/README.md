# Managed GLM Provider

This project contains Tomur's pure C# provider for explicitly marked GLM / MoE model directories. It does not load a third-party inference dynamic library and does not replace the existing llama.cpp provider.

The M1-M9 foundation code is in place: model metadata probing, safetensors indexing, fixed-seed tiny oracle fixture generation, bounded tensor storage, numeric conversion, scalar reference kernels, tokenizer and prompt pipelines, resident dense model loading, MLA reference/absorbed attention, compressed KV cache, bounded MoE expert streaming, full scalar forward, bounded-batch prefill, incremental decode, sampling, penalties, stop handling, cancellation, and incremental text callbacks. M10 Tomur API, session, streaming, and diagnostic integration is next. Build, regression, oracle alignment, cross-platform, performance, and release validation are intentionally deferred to the final M14 validation stage; the managed generation path remains experimental until those gates pass.

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
  "capabilities": ["completion", "chat"]
}
```

For `int8` and packed `int4` routed experts, each `*.weight` payload requires a same-prefix `*.scales` tensor containing one F32 scale per output row. F32, F16, and BF16 routed experts do not use scale sidecars. Routed expert payloads remain on disk until selected by the MoE router; resident weights, KV memory, scratch memory, and the configured expert-cache safety margin are deducted before cache slots are allocated.

For development, point `TOMUR_PROVIDER_PATH` at the directory containing `Tomur.Providers.Glm.dll`. Non-AOT release packaging will later copy approved provider assemblies into the `providers` directory beside the main application.
