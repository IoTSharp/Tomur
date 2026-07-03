# R12 发布包结构说明

记录时间：2026-07-03

本文说明 Tomur R12 发布产物的目录结构、单文件边界、native runtime 随包方式和本地数据目录关系。发布包应保持一个 `tomur` 入口，不要求用户理解 native backend 的内部实现。

## 发布 profile

| Profile | 用途 | 关键属性 |
| --- | --- | --- |
| `native-aot-audit` | Native AOT 发布入口 | `PublishAot=true`, `SelfContained=true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `SuppressTrimAnalysisWarnings=false` |
| `self-contained-single-file` | 非 AOT 自包含单文件兼容发布 | `PublishAot=false`, `SelfContained=true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true` |

## 发布目录

RID 发布后，发布目录应保持以下结构：

```text
publish/
  tomur(.exe)
  native/
    bundle.manifest.json
    runtimes/
      <rid>/
        native/
          ...
```

说明：

1. `tomur(.exe)` 是用户面对的唯一程序入口。
2. `native/bundle.manifest.json` 随发布目录保留，用于定位 bundle id、version、runtime root、组件和库。
3. `native/runtimes/<rid>/native` 是随包 native runtime 源目录，不进入单文件。
4. `IncludeAllContentForSelfExtract=false` 保持不变，避免把大体积内容整体塞进可执行文件。
5. Web 工作台构建产物嵌入 `app/wwwroot` 并由 Tomur 本地 HTTP 服务托管。

## 首次运行目录

首次运行或 runtime 版本变化时，Tomur 会把随包 native runtime 准备到数据目录：

```text
<data>/
  runtime/
    tomur.native.r8.cuda13/
      0.8.0-cuda13/
        runtimes/
          <rid>/
            native/
              ...
  models/
  logs/
  config/
    tomur.json
  tomur.db
```

说明：

1. Windows 数据目录默认是 `%LOCALAPPDATA%\Tomur`。
2. Linux 数据目录默认是 `~/.local/share/tomur`。
3. macOS 数据目录默认是 `~/Library/Application Support/Tomur`。
4. `--data-dir <path>` 与 `TOMUR_DATA_DIR` 可以覆盖默认目录。
5. 服务安装时会显式固定数据目录，并将 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指向 `<data>/bundle-cache`。

## 不进入发布二进制的内容

以下内容不硬编码进程序二进制：

1. 模型权重。
2. SQLite 数据库。
3. 日志。
4. 用户文件。
5. 会话产物、生成图片和 TTS 音频。
6. 下载中的 `.part` 文件。

## Native Runtime 随包规则

1. 发布目录必须包含 `native/bundle.manifest.json`。
2. 发布目录必须包含目标 RID 的 `native/runtimes/<rid>/native`。
3. `tomur native prepare` 应能把随包 native runtime 复制到 `<data>/runtime/<bundle-id>/<version>/runtimes/<rid>/native`。
4. `GET /api/runtime/native` 应能报告当前 RID、source runtime root、managed runtime root、component 状态和 library checksum 状态。
5. 缺失或损坏的 required library 必须返回 error 诊断；缺失 optional accelerator library 不得阻塞 CPU runtime。
6. Linux `.so` alias 可以在源码随包目录中表现为 symlink 或零字节 alias；prepare 会从版本化库 materialize 到 managed runtime 目录。

## 最小发布回归

发布包完成后至少记录以下命令结果：

```powershell
.\tomur.exe --help
.\tomur.exe doctor --data-dir .tmp\r12-release-data
.\tomur.exe native prepare --data-dir .tmp\r12-release-data
.\tomur.exe serve --data-dir .tmp\r12-release-data --urls http://127.0.0.1:5149
```

HTTP 回归：

```powershell
Invoke-RestMethod http://127.0.0.1:5149/health
Invoke-RestMethod http://127.0.0.1:5149/api/version
Invoke-RestMethod http://127.0.0.1:5149/v1/models
Invoke-RestMethod http://127.0.0.1:5149/api/runtime/native
```

通过标准：

1. `tomur --help` 能输出 CLI 帮助。
2. `tomur doctor` 能报告 OS、架构、数据目录、SQLite、runtime 和 native bundle 状态。
3. `tomur native prepare` 能准备当前 RID 的 runtime，且返回 copied、repaired、aliased、unchanged 或 ok 的文件结果。
4. `tomur serve` 能启动本地 HTTP 服务。
5. Web 静态入口可由本地 HTTP 服务托管。
6. 模型未下载或 native backend 缺失时返回结构化诊断，不伪造推理结果。

## 发布缺口

1. `osx-x64` 和 `osx-arm64` native runtime 随包目录仍需补齐。
2. Linux x64 当前 native runtime 随包记录为 CPU 资产，CUDA13 变体需要后续补齐或明确发布限制。
3. Windows Service、Linux systemd、macOS launchd 和 Windows 托盘需要使用发布产物完成实机 smoke。
