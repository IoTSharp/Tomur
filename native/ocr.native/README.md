# ocr.native

本目录是 Tomur 对 OCR native runtime 的 CMake 包装层。

R3 主线固定为 PaddleOCR-VL。bridge 通过 `llama.cpp` 的 MTMD 能力加载视觉模型与 mmproj，并将 OCR 消费者运行时隔离到：

```text
native/runtimes/<rid>/native/ocr/<backend>/
```

当前 `<backend>` 包含 `cpu` 与 `cuda13`。CUDA13 变体依赖顶层 `ggml-cuda`，Tomur 托管层会在探测到 CUDA backend 且变体可用时启用 GPU layer offload；否则回退 CPU 变体。

该目录只放 Tomur 自己的 C ABI bridge 与 CMake 边界；上游源码位于 `native/paddleocr` 与 `native/llama.cpp`。当前 bridge 采用 PaddleOCR-VL / MTMD 路线，不把 Paddle Inference demo 作为默认主流程。
