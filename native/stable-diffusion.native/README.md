# stable-diffusion.native

本目录是 Tomur 对 `stable-diffusion.cpp` 的 CMake 包装层。

职责：

- 构建本地图像生成动态库。
- 暴露 Tomur 需要的最小 C ABI：上下文创建、PNG 生成、buffer 释放和诊断辅助。
- `tomur_sd_generate_png` 会把 prompt、negative prompt、size、steps、CFG、seed、sampler、scheduler、finite `distilled_guidance` 和 finite `flow_shift` 转发给 stable-diffusion.cpp；sampler / scheduler 未指定或无法识别时回到 upstream 默认值。
- 编码后的 PNG buffer 由 `tomur_sd_free_buffer` 释放；stable-diffusion.cpp 生成的 `sd_image_t` 由 bridge 内部通过 `free_sd_images` 释放，避免跨 CRT 手动释放。
- 将消费者运行时隔离到 `native/runtimes/<rid>/native/stable-diffusion/<backend>/`。
- 默认复用 `llama.native` 发布的顶层共享 `ggml`。

CPU 构建入口：

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64 --target install
```

```bash
cmake --preset linux-x64
cmake --build --preset linux-x64 --target install
```
