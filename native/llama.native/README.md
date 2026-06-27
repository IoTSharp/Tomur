# llama.native

本目录是 Tomur 对 `llama.cpp` 的 CMake 包装层。

职责：

- 构建 `llama` 与顶层共享 `ggml` 动态库。
- 构建 `tomur-llama-mtmd` 与 `tomur-llama-vlm` bridge。
- 作为 `native/runtimes/<rid>/native/` 下 `llama` / `ggml` 共享库的唯一发布者。

CPU 构建入口：

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64 --target install
```

```bash
cmake --preset linux-x64
cmake --build --preset linux-x64 --target install
```

安装输出：

```text
native/runtimes/<rid>/native/
```
