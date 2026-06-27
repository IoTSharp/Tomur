# whisper.native

本目录是 Tomur 对 `whisper.cpp` 的 CMake 包装层。

职责：

- 构建 Whisper ASR 动态库。
- 默认复用 `llama.native` 在顶层 runtime 目录发布的共享 `ggml`。
- 将 Whisper 消费者运行时隔离到 `native/runtimes/<rid>/native/whisper/<backend>/`。

CPU 构建入口：

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64 --target install
```

```bash
cmake --preset linux-x64
cmake --build --preset linux-x64 --target install
```
