# Native 资产目录

本目录用于承载 Tomur native backend 的源码、构建边界和发布前资产，不属于独立 .NET 项目。

## 目录约定

```text
native/
  llama.cpp/
  llama.native/
  whisper.cpp/
  whisper.native/
  stable-diffusion.cpp/
  stable-diffusion.native/
  runtimes/
    win-x64/
      native/
    linux-x64/
      native/
```

`*.cpp/` 目录用于上游 backend 源码或子模块，`*.native/` 目录用于 Tomur 自己的 CMake、编译选项、清单和发布打包边界。

## 单文件发布策略

Tomur 的 RID 发布默认使用 `PublishSingleFile=true`、`SelfContained=true` 和 `IncludeNativeLibrariesForSelfExtract=true`。这会让 .NET 单文件发布把必要 native 依赖打入程序，并在启动前自解压。

`IncludeAllContentForSelfExtract` 默认保持 `false`。模型文件、SQLite 数据库、日志、用户文件和大体积 backend 资产不应作为普通内容文件整体塞进可执行文件。

.NET 单文件自解压目录由运行时决定，通常位于用户临时目录或 `.net` 缓存目录；该目录不是 Tomur 的稳定 runtime 根目录。服务模式后续需要显式处理 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，避免系统服务账号缺少可用解压目录。

推理 backend 动态库需要由 R3 的 native bundle manifest 管理，发布后进入 Tomur 受管理 runtime 目录：

1. Windows：`%LOCALAPPDATA%\Tomur\runtime`
2. Linux：`~/.local/share/tomur/runtime`

Tomur 运行时应对这些文件做版本、checksum、存在性和加载探测诊断，而不是依赖临时自解压目录作为稳定 runtime 根目录。

## ggml 隔离

`llama.native` 是顶层 `runtimes/<rid>/native/ggml*` 的唯一发布者。

`whisper.native` 的消费者运行时位于 `runtimes/<rid>/native/whisper/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。

`stable-diffusion.native` 的消费者运行时位于 `runtimes/<rid>/native/stable-diffusion/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。
