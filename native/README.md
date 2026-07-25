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
  hyperlpr3/
  plate.native/
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
          cuda129/
          cuda13/
        stable-diffusion/
          cpu/
          cuda129/
          cuda13/
        ocr/
          cpu/
          cuda129/
          cuda13/
        plate/
          cpu/
        tts/
          cpu/
          cuda129/
          cuda13/
    linux-x64/
      native/
        ggml-cuda.so
        whisper/cuda129/
        stable-diffusion/cuda129/
        ocr/cuda129/
        plate/cpu/
        tts/cuda129/
    linux-arm64/
      native/
        llama.so
        ggml*.so
        whisper/cpu/
        stable-diffusion/cpu/
        ocr/cpu/
        plate/cpu/
        tts/cpu/
```

上游 backend 源码使用 `*.cpp/`、`paddleocr/` 和 `hyperlpr3/` 等子模块目录，`*.native/` 目录用于 Tomur 自己的 CMake、编译选项、清单和发布打包边界。

OCR 主线固定为 PaddleOCR C++ runtime；R3 不设计第二 OCR runtime。

通用文档 OCR 与业务车牌识别是两个独立能力。车牌识别通过 `plate.native` 的稳定 C ABI 调用 HyperLPR3 C++/MNN，不复用 `ocr.native`，也不把车牌模型权重放入 native bundle。

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

`all` 是默认后端，会先构建顶层 llama.cpp / ggml 共享 runtime，再分别构建 Whisper、PaddleOCR-VL、stable-diffusion.cpp 和 llama.cpp GGUF TTS 的 `cpu` 与 `cuda129` 变体，并构建 HyperLPR3 车牌识别的 CPU 变体。只需要单一变体时可使用：

```powershell
tomur native build --rid win-x64 --backend cpu
tomur native build --rid win-x64 --backend cuda129
tomur native build --rid win-x64 --backend cuda13
tomur native build --rid win-x64 --backend vulkan
tomur native build --rid win-x64 --backend sycl
tomur native build --rid win-x64 --backend openvino
tomur native build --rid win-x64 --backend intel
```

`vulkan`、`sycl` 与 `openvino` 当前只构建 llama.cpp dynamic backend；`intel` 会按顺序构建 `sycl`、`openvino` 与 `vulkan`。构建产物安装到 `native/runtimes/win-x64/native`。随后执行 `tomur native prepare`，Tomur 会把这些资产复制到受管理 runtime 目录，并由 `tomur doctor`、`GET /api/runtime/status` 与 `GET /api/runtime/multimodal` 报告 CPU、CUDA 12.9、Vulkan、SYCL 与 OpenVINO 可见性。

## Linux CUDA 12.9 构建入口

Linux x64 的 CUDA 构建固定使用 CUDA Toolkit 12.9，并为 RTX 4090 固定生成 compute capability 8.9 代码：

```bash
tomur native build --rid linux-x64 --backend cuda129
```

完整构建使用 `--backend all`，同时生成 CPU fallback 与 CUDA 12.9 变体。推荐在 `nvidia/cuda:12.9.1-devel-ubuntu24.04` 容器中执行构建；宿主机只需兼容的 NVIDIA 驱动，不要求安装 CUDA Toolkit。

## Linux ARM64 构建入口

Linux ARM64 当前只开放 CPU 构建：

```bash
tomur native build --rid linux-arm64 --backend cpu
```

`--backend all` 在该 RID 上等价于构建所有组件的 CPU 变体。预设使用 `aarch64-linux-gnu-gcc/g++`，并显式设置目标系统为 Linux/aarch64；所有外部依赖也必须是 ARM64 产物。Tomur 不在目标机上构建，发布流程应在本机或构建机完成交叉编译，再随应用部署。

## Docker CPU 发布

仓库顶层 `Dockerfile` 从固定子模块构建 llama.cpp、HyperLPR3、MNN 和 OpenCV，并发布自包含 Tomur CPU 镜像。BuildKit 的目标平台决定 RID，同一份 Dockerfile 支持 `linux/amd64` 与 `linux/arm64`：

```bash
git submodule update --init --recursive
docker buildx build --platform linux/amd64 --load -t tomur:cpu-amd64 .
docker buildx build --platform linux/arm64 --load -t tomur:cpu-arm64 .
```

ARM64 默认使用 `armv8-a`。已确认目标 CPU 指令集时，可以用 `--build-arg CPU_ARM64_ARCH=armv8.2-a+dotprod` 显式提高基线；AMD64 构建不会接收该参数。部署仓库可以通过 Compose 的 `platform` 和 `build.context` 选择架构与 Tomur 仓库路径，但不得复制或维护 Tomur 的编译步骤。

## 车牌识别资产

HyperLPR3、MNN 2.2.0 和 OpenCV 4.12.0 源码分别通过 `native/hyperlpr3`、`native/mnn` 和 `native/opencv` 子模块固定。`plate.native` 依赖从这些源码为目标 RID 构建的 HyperLPR3 安装产物及同架构 OpenCV 4；MNN 可以静态链接进 HyperLPR3，也可以作为显式动态依赖随 bundle 发布。构建前将 `TOMUR_HYPERLPR3_ROOT` 指向 HyperLPR3 安装根目录并设置 `OpenCV_DIR`；Windows 还必须设置 `TOMUR_HYPERLPR3_RUNTIME_LIBRARY`，动态 MNN/OpenCV 文件通过 `TOMUR_MNN_RUNTIME_LIBRARY` 或 `TOMUR_PLATE_RUNTIME_DEPENDENCIES` 明确加入安装结果。发布构建不得让 HyperLPR3 在线下载 MNN，也不得回退到构建机的系统 OpenCV。

上游 HyperLPR3 子模块包含 `resource/models/r2_mobile` 下的 `.mnn` 文件，但 Tomur 不会将它们自动视为已安装模型，也不会复制进程序或 native bundle。运行时模型目录仍固定为 `<data>/models/plate/hyperlpr3/r2_mobile`，`tomur-plate` 在每次调用前检查目录完整性；使用或分发这些权重前必须单独复核其来源与许可条款。

## ggml 隔离

`llama.native` 是顶层 `runtimes/<rid>/native/llama*` 与 `ggml*` 的唯一发布者。CUDA 12.9、CUDA 13、Vulkan、SYCL 与 OpenVINO 构建会把对应 `ggml-*` 作为可选 accelerator backend 发布到同一顶层目录；缺失时 CPU 运行时仍可诊断和加载。

`whisper.native` 的消费者运行时位于 `runtimes/<rid>/native/whisper/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。

`stable-diffusion.native` 的消费者运行时位于 `runtimes/<rid>/native/stable-diffusion/<backend>/`，并从同一 runtime 根目录解析共享 `ggml`。

`ocr.native` 的消费者运行时位于 `runtimes/<rid>/native/ocr/<backend>/`。PaddleOCR 自身依赖按 backend 隔离，不向顶层发布 `ggml*`。

`plate.native` 的消费者运行时位于 `runtimes/<rid>/native/plate/cpu/`。HyperLPR3、动态 MNN 与 OpenCV 依赖保持在同一目录，模型文件仍位于 Tomur 模型目录。

`tts.native` 的消费者运行时位于 `runtimes/<rid>/native/tts/<backend>/`，并从同一 runtime 根目录解析 `llama.native` 发布的共享 `llama` / `ggml`。
