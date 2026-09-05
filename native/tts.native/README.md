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
## R20 Resident TTS ABI

既有 `tomur-tts` 动态库新增 `tomur_realtime_tts_abi`（返回 1）、`create/reset/cancel/synthesize/destroy`，完整前缀均为 `tomur_realtime_tts_`。acoustic/WavTokenizer 模型按 session 加载，contexts 在首句创建后复用；普通 `tomur_tts_synthesize_to_pcm` 保持文件级接口。

Realtime synthesize 接受最大 2048 UTF-8 bytes 的短句，提供同步 PCM callback，每次最多 2400 个 24 kHz PCM16 样本；callback 指针只在调用期间有效，返回 0 终止输出，异常不得越过 ABI。宿主将 callback 复制到有界队列，native 不等待网络。模型 decode、vocoder 和 DSP 检查取消/30 秒截止；每句输出上限 30 秒，不使用固定 250 ms 静音，句首尾应用 5 ms ramp。

波形重叠相加仅遍历实际音频列；频谱必须匹配 1280 点逆变换的 1282 个实虚部值，非有限 PCM 拒绝输出。DSP 工作线程捕获异常、通知其他工作线程停止并 join 后再向调用方返回错误。共享批处理路径的数值一致性仍待回归确认。

cancel 可以与执行并发，reset/destroy 必须在执行已退出后调用。Realtime 当前使用保守 CPU 参数，RTF、AEC、真实模型、native 编译和发布证据均为 pending，不能宣称全双工性能达标。现有模型及其许可约束保持不变。
