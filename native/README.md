# Native 资产目录

本目录用于承载 Tomur native backend 的源码、构建边界和发布前资产，不属于独立 .NET 项目。

## 目录约定

```text
native/
  llama.cpp/
  llama.native/
  whisper.cpp/
  whisper.native/
  paddleocr/
  ocr.native/
  stable-diffusion.cpp/
  stable-diffusion.native/
  tts.native/
  runtimes/
    win-x64/
      native/
        llama.dll
        ggml*.dll
        whisper/
          cpu/
          cuda13/
        stable-diffusion/
          cpu/
          cuda13/
        ocr/
          cpu/
          cuda13/
        tts/
          cpu/
          cuda13/
    linux-x64/
      native/
```

`*.cpp/` 目录用于上游 backend 源码或子模块，`*.native/` 目录用于 Tomur 自己的 CMake、编译选项、清单和发布打包边界。

OCR 主线固定为 PaddleOCR C++ runtime；R3 不设计第二 OCR runtime。

TTS 主线固定为 llama.cpp TTS / GGUF TTS runtime，并作为 R3 已敲定方向。

## 单文件发布策略

Tomur 的 RID 发布默认使用 `PublishSingleFile=true`、`SelfContained=true` 和 `IncludeNativeLibrariesForSelfExtract=true`。这会让 .NET 单文件发布把必要 native 依赖打入程序，并在启动前自解压。

`IncludeAllContentForSelfExtract` 默认保持 `false`。模型文件、SQLite 数据库、日志、用户文件和大体积 backend 资产不应作为普通内容文件整体塞进可执行文件。

.NET 单文件自解压目录由运行时决定，通常位于用户临时目录或 `.net` 缓存目录；该目录不是 Tomur 的稳定 runtime 根目录。服务模式后续需要显式处理 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，避免系统服务账号缺少可用解压目录。

推理 backend 动态库需要由 R3 的 native bundle manifest 管理，发布后进入 Tomur 受管理 runtime 目录：

1. Windows：`%LOCALAPPDATA%\Tomur\runtime`
2. Linux：`~/.local/share/tomur/runtime`

Tomur 运行时应对这些文件做版本、checksum、存在性和加载探测诊断，而不是依赖临时自解压目录作为稳定 runtime 根目录。

R3 当前使用发布包中的 `native/runtimes/<rid>/native` 作为 source bundle。首次 `tomur serve` 或显式 `tomur native prepare` 会把 source bundle 准备到版本化目录：

```text
<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native
```

如果目标文件缺失、陈旧或 checksum 不一致，prepare 会从 source bundle 复制或替换；`POST /api/runtime/native/prepare` 提供同一套修复动作给后续 Runtime UI 使用。

## Windows native 构建入口

Windows x64 的 native 构建由 Tomur CLI 统一触发：

```powershell
tomur native build --rid win-x64 --backend all
```

`all` 是默认后端，会先构建顶层 llama.cpp / ggml 共享 runtime，再分别构建 Whisper、PaddleOCR-VL、stable-diffusion.cpp 和 llama.cpp GGUF TTS 的 `cpu` 与 `cuda13` 变体。只需要单一变体时可使用：

```powershell
tomur native build --rid win-x64 --backend cpu
tomur native build --rid win-x64 --backend cuda13
tomur native build --rid win-x64 --backend vulkan
tomur native build --rid win-x64 --backend sycl
tomur native build --rid win-x64 --backend openvino
tomur native build --rid win-x64 --backend intel
```

`vulkan`、`sycl` 与 `openvino` 当前只构建 llama.cpp dynamic backend；`intel` 会按顺序构建 `sycl`、`openvino` 与 `vulkan`。构建产物安装到 `native/runtimes/win-x64/native`。随后执行 `tomur native prepare`，Tomur 会把这些资产复制到受管理 runtime 目录，并由 `tomur doctor`、`GET /api/runtime/status` 与 `GET /api/runtime/multimodal` 报告 CPU、CUDA13、Vulkan、SYCL 与 OpenVINO 可见性。

## ggml 隔离

`llama.native` 是顶层 `runtimes/<rid>/native/llama*` 与 `ggml*` 的唯一发布者。CUDA13、Vulkan、SYCL 与 OpenVINO 构建会把对应 `ggml-*` 作为可选 accelerator backend 发布到同一顶层目录；缺失时 CPU 运行时仍可诊断和加载。

`whisper.native` 的消费者运行时位于 `runtimes/<rid>/native/whisper/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。

`stable-diffusion.native` 的消费者运行时位于 `runtimes/<rid>/native/stable-diffusion/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。

`ocr.native` 的消费者运行时位于 `runtimes/<rid>/native/ocr/<backend>/`。PaddleOCR 自身依赖按 backend 隔离，不向顶层发布 `ggml*`。

`tts.native` 的消费者运行时位于 `runtimes/<rid>/native/tts/<backend>/`，并从同一 runtime 根目录解析 `llama.native` 发布的共享 `llama` / `ggml`。
