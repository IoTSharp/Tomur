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
## R20 Session ABI

`realtime_bridge.cpp` 随既有 whisper 动态库编译，不增加独立运行进程或新的 ggml 资产目录。新增 `tomur_realtime_speech_abi`（返回 1）、`speech_create/reset/cancel/destroy`、`vad_process/reset` 与 `transcribe` 导出，完整前缀均为 `tomur_realtime_`。

create 通过受取消回调和 60 秒截止控制的 model loader 加载 Whisper 与 Silero；process 接受 512 个 float 样本并保留 Silero recurrent state。transcribe 的 30 秒窗口、30 秒截止、16 KiB 文本缓冲和 abort callback 由 bridge 与宿主共同约束。VAD 与 ASR 使用不同 context；同一个 ASR context 不允许两个 transcribe 同时执行。

模型读取按最多 1 MiB 分块检查取消。`patches/realtime-load-cleanup.patch` 修复 Whisper 加载失败路径，释放已分配的权重缓冲；VAD loader 使用 RAII 回收部分构建的 context，并在 C ABI 内捕获加载异常。取消不会通过返回未初始化的模型字节来模拟 EOF。CMake 将补丁应用到构建目录中的源码副本并用于 whisper target，保留上游子模块工作区不变；补丁不匹配时配置阶段明确失败。

宿主取消后必须等待调用返回，再 destroy；不能通过并发释放句柄强制中断 native。旧库没有这些导出时，Realtime 返回 `realtime_native_abi_unavailable`。源码接入尚未经过 native 编译、模型或设备 smoke，现有文件级导出保持兼容。
