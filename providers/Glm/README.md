# Managed GLM Provider

This project contains Tomur's pure C# provider for explicitly marked GLM / MoE model directories. It does not load a third-party inference dynamic library and does not replace the existing llama.cpp provider.

The M1-M4 foundation code is in place: model metadata probing, safetensors indexing, fixed-seed tiny oracle fixture generation, bounded tensor storage, numeric conversion, quantized views, workspaces, expert slabs, and scalar reference kernels. M5 tokenizer work is next. Build, regression, oracle alignment, cross-platform, performance, and release validation are intentionally deferred to the final M14 validation stage; forward execution remains unavailable until that stage passes.

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

For development, point `TOMUR_PROVIDER_PATH` at the directory containing `Tomur.Providers.Glm.dll`. Non-AOT release packaging will later copy approved provider assemblies into the `providers` directory beside the main application.
