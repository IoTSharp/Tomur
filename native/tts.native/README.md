# TTS native bridge

R3 的 TTS 路线固定为 llama.cpp GGUF TTS。Tomur 不引入额外 TTS native 子模块。

上游入口是 `native/llama.cpp/tools/tts`，用于跟随 llama.cpp 的 GGUF TTS 行为和模型约定。`tts.native` 目录承载 Tomur 自己的 C ABI bridge、CMake 边界和打包清单。

当前 bridge 导出：

- `tomur_tts_bridge_version`
- `tomur_tts_runtime_info`
- `tomur_tts_synthesize_to_pcm`
- `tomur_tts_result_free`

`tomur_tts_synthesize_to_pcm` 在 R8 阶段接入 llama.cpp `tools/tts` 路线：加载 OuteTTS GGUF 生成 audio code tokens，再用 WavTokenizer GGUF sidecar 生成 embedding，并在 bridge 内转换为 24 kHz mono PCM16。托管层会把 PCM16 封装为 `/v1/audio/speech` 的 WAV 响应。

`speaker_prompt_utf8` 当前约定为可选的绝对 speaker JSON 文件路径；普通 OpenAI voice 名称会使用内置默认 speaker，不会直接传入 native bridge。

目标运行时布局：

```text
native/runtimes/<rid>/native/tts/<backend>/
```

该 bridge 复用 `llama.native` 在顶层 runtime 目录发布的 `llama` / `ggml` 共享库。
