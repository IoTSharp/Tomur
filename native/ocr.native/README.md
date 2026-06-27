# ocr.native

本目录是 Tomur 对 OCR native runtime 的 CMake 包装层。

R3 主线固定为 PaddleOCR-VL。bridge 通过 `llama.cpp` 的 MTMD 能力加载视觉模型与 mmproj，并将 OCR 消费者运行时隔离到：

```text
native/runtimes/<rid>/native/ocr/<backend>/
```

该目录只放 Tomur 自己的 C ABI bridge 与 CMake 边界；上游源码位于 `native/paddleocr` 与 `native/llama.cpp`。
