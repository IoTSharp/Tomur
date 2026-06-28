# stable-diffusion.native

本目录是 Tomur 对 `stable-diffusion.cpp` 的 CMake 包装层，不是上游源码目录。

职责：

- 构建本地图像生成动态库。
- 暴露 Tomur 需要的最小 C ABI：上下文创建、PNG 生成、buffer 释放和诊断辅助。
- `tomur_sd_create_ctx` 会把 Tomur / 旧上层参数语义翻译到当前 stable-diffusion.cpp C API：
  - `backend` 和 `params_backend` 会直接转发。
  - 未显式传入 `backend` 时，`keep_clip_on_cpu` / `keep_vae_on_cpu` 会转换为 `te=cpu` / `vae=cpu`。
  - 未显式传入 `params_backend` 时，`offload_params_to_cpu` 会转换为 `*=cpu`。
- `tomur_sd_generate_png` 会转发 prompt、negative prompt、size、steps、CFG、seed、sampler、scheduler、finite `distilled_guidance` 和 finite `flow_shift`；sampler / scheduler 未指定或无法识别时回到 upstream 默认值。
- bridge 会向 stderr 输出 Tomur 前缀诊断，包括 upstream version / commit、sidecar 传入状态、backend assignment、采样参数和 PNG 编码结果，便于 `/v1/images/generations` worker 失败时定位。
- 编码后的 PNG buffer 由 `tomur_sd_free_buffer` 释放；stable-diffusion.cpp 生成的 `sd_image_t` 由 bridge 内部通过 `free_sd_images` 释放，避免跨 CRT 手动释放。
- 消费者运行时隔离到 `native/runtimes/<rid>/native/stable-diffusion/<backend>/`。
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
