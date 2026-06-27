# TTS native bridge

R3 的 TTS 路线固定为 llama.cpp GGUF TTS。Tomur 不引入额外 TTS native 子模块。

上游入口是 `native/llama.cpp/tools/tts`，用于跟随 llama.cpp 的 GGUF TTS 行为和模型约定。`tts.native` 目录承载 Tomur 自己的 C ABI bridge、CMake 边界和打包清单。

当前 R3 bridge 导出：

- `tomur_tts_bridge_version`
- `tomur_tts_runtime_info`
- `tomur_tts_synthesize_to_pcm`
- `tomur_tts_result_free`

`tomur_tts_synthesize_to_pcm` 在 R3 阶段只固定 ABI 并返回明确的待接入诊断；实际 GGUF TTS 合成逻辑在后续多模态阶段接入 llama.cpp `tools/tts`。

目标运行时布局：

```text
native/runtimes/<rid>/native/tts/<backend>/
```

该 bridge 复用 `llama.native` 在顶层 runtime 目录发布的 `llama` / `ggml` 共享库。
