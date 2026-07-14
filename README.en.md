# Tomur

[中文](./README.md)

Tomur is a local AI infrastructure that integrates local model serving, OpenAI / Ollama-compatible APIs, model asset management, runtime diagnostics, and a Chat-first web workspace. Built with .NET 10 and C#, it runs as a single local `tomur` process that hosts the CLI, local HTTP service, system service modes, native runtime management, and web static assets for offline-first, privacy-sensitive developer environments.

Tomur manages model weights, SQLite data, logs, user files, and generated artifacts as local assets. It is designed to download, verify, prepare, diagnose, and serve local models through one entry point without requiring users to understand native backend internals or model directory layouts.

## 🧭 Why Tomur

Local AI workflows often involve model files, native dynamic libraries, compatibility APIs, a local service, a web chat workspace, logs, and diagnostics. Tomur brings these pieces together into one local program:

1. 🔌 Start a local service with OpenAI / Ollama-compatible APIs.
2. 📦 Manage model downloads, verification, and local visibility from one entry point.
3. 💬 Use the built-in web workspace for chat, attachments, and runtime status.
4. 🩺 Diagnose native libraries, models, ports, proxy settings, SQLite, and hardware through `tomur doctor`, runtime APIs, and the UI.
5. 🚀 Follow a self-contained, single-file, Native AOT-friendly release path.

Tomur focuses on local AI runtime experience. It is not a multi-tenant server product, enterprise administration shell, or complex workflow governance platform.

## 🚀 Quick Start

Show available commands:

```powershell
tomur --help
```

Start the local service and open the workspace:

```powershell
tomur open
```

Prepare the native runtime and install the recommended model packages:

```powershell
tomur native prepare
tomur pull recommended
```

Run the local HTTP API service:

```powershell
tomur serve --open
```

When developing from source, run the main application project directly:

```powershell
dotnet run --project app -- --help
dotnet run --project app -- serve --open
```

The default local service URL is `http://127.0.0.1:5137`.

## 🧩 Target Capabilities

1. 💬 Local text generation.
2. 🧠 Local embeddings and reranking.
3. 🔌 OpenAI-compatible HTTP API.
4. 🔁 Ollama-compatible HTTP API.
5. 📦 Model catalog, download, verification, and local asset management.
6. 🩺 Runtime diagnostics for CPU, memory, disk, proxy, ports, models, and native libraries.
7. ⚙️ Native runtime support for llama.cpp, Whisper, OCR native, stable-diffusion.cpp, and llama.cpp TTS / GGUF TTS.
8. 🧮 Optional pure C# model providers that extend local inference by model architecture.
9. 🖥️ System service mode.
10. 🧑‍💻 React + Ant Design X web workspace.

Tomur does not fabricate inference results when the local runtime is unavailable. Missing models, unavailable native runtime or managed providers, damaged bundle assets, context length limits, capability mismatches, and insufficient memory are reported as diagnosable errors through the API, CLI, and UI.

## 🔌 API Examples

Health check:

```powershell
curl.exe http://127.0.0.1:5137/health
```

List locally visible models:

```powershell
curl.exe http://127.0.0.1:5137/v1/models
```

Call the OpenAI-style chat API:

```powershell
curl.exe http://127.0.0.1:5137/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{
    "model": "qwen35-9b-q4km",
    "messages": [
      { "role": "user", "content": "Introduce Tomur in one sentence." }
    ],
    "stream": false
  }'
```

Call the Ollama-style chat API:

```powershell
curl.exe http://127.0.0.1:5137/api/chat `
  -H "Content-Type: application/json" `
  -d '{
    "model": "qwen35-9b-q4km",
    "messages": [
      { "role": "user", "content": "List the current runtime status." }
    ],
    "stream": false
  }'
```

Actual model IDs come from the local install manifest and model directory. Inspect them with:

```powershell
tomur list
tomur ps
tomur list --catalog
```

## 🚧 Current Status

Tomur has completed the main R1-R11 loops, is converging the R12 Native AOT / self-contained release matrix, continuing the R13 web capability aggregation loop, implementing R14 Intel GPU / NPU acceleration, and advancing the R15 pure C# GLM / MoE provider experiment. Completed history is tracked in [CHANGELOG.md](./CHANGELOG.md).

| Stage | Status |
| --- | --- |
| R1-R4 | Single-project API skeleton, configuration and local state, native bundle boundary, and first OpenAI / Ollama-compatible APIs are connected. |
| R5-R7 | System service code paths, model catalog and download, and first local llama.cpp text inference path are connected. |
| R8 | Whisper ASR, GGUF TTS, VLM, OCR, and stable-diffusion.cpp image generation have real-model smoke evidence within the current public API scope. |
| R9-R10 | Microsoft AI abstractions, controlled Agent Framework orchestration, SQLite local file search, conversation state, attachment entry points, and voice turn service are connected. |
| R11 | React + Ant Design X Chat-first web workspace is connected and served by Tomur through `app/wwwroot`. |
| R12 | Native AOT publishing currently passes without warnings; Linux/macOS release logs, macOS native bundle assets, and real-machine service smoke remain in progress. |
| R13 | Web capability aggregation has connected Agent / Capabilities views, read-only Agent tool calls, explicit side-effect tool confirmation, protocol capability maps, and Claude Code / Anthropic Messages compatibility; visual download queue and editable Settings remain in progress. |
| R14 | Intel GPU / NPU support is being connected through the existing ggml dynamic backend path; `vulkan`, `sycl`, `openvino`, and `intel` native build entries, runtime accelerator preferences, OpenVINO / NPU environment setup, CPU fallback diagnostics, NPU incompatibility errors, Web Runtime display, and the smoke evidence entry are in place. Real Intel GPU / NPU smoke still needs machine evidence. |
| R15 | The M1-M8 foundation code is complete: the independent pure C# GLM / MoE provider library, provider selection boundary, model format probing, fixed-seed tiny fixture/oracle baseline, tensor storage/read layer, scalar reference kernels, tokenizer and incremental decoding, resident dense model loading, MLA with compressed KV cache, MoE routing, shared experts, per-layer leased LRU slots, and bounded expert streaming are in place. M9 full forward and generation are next. Build, regression, oracle alignment, cross-platform, performance, and real-forward validation are deferred to M14. The existing llama.cpp path remains available and is still the default. |

Planned follow-up work includes Intel GPU / NPU real smoke (tracked through `docs/r14-intel-acceleration-smoke.md`), a visual download queue, editable Settings, model deletion, VAD / interruption, streaming voice turns, multi-model residency, Linux/macOS release records, and real-machine service smoke.

See [ROADMAP.md](./ROADMAP.md) for detailed stage plans and acceptance boundaries, and [CHANGELOG.md](./CHANGELOG.md) for completed history.

## 🏗️ Architecture Overview

The main application stays concentrated while pure managed model providers are isolated in class libraries:

```text
Tomur/
  README.md
  README.en.md
  CHANGELOG.md
  ROADMAP.md
  app/
    Tomur.csproj
    Program.cs
    Api/
    Cli/
    Config/
    Native/
    Providers/
    Runtime/
    Services/
    Web/
  providers/
    Glm/
      Tomur.Providers.Glm.csproj
  tests/
    Tomur.Providers.M1.Tests/
    Tomur.Providers.M2.Tests/
    Tomur.Providers.M3.Tests/
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
  web/
    package.json
    src/
```

`Tomur.csproj` hosts the CLI, local HTTP API, service-mode startup, runtime management, and web static assets. `Program.cs` owns process entry, top-level command dispatch, and global help; concrete CLI implementations live under `app/Cli/`. `providers/` is limited to independent pure C# model providers and does not create a second service or product entry point; `tests/` only contains stage-specific validation projects.

`native/` contains native backend source code, CMake projects, and release packaging boundaries. `app/Native/` only contains C# dynamic library loading, P/Invoke, and native adapter code. Pure managed providers do not replace the existing llama.cpp path and are selected explicitly by model format and architecture. The web source lives under `web/`; its build output goes to `app/wwwroot` and is served by the Tomur local HTTP service.

## 📁 Local State

Tomur stores configuration, models, runtime cache, SQLite data, logs, and generated artifacts under a stable data directory.

| Platform | Default data directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Tomur` |
| Linux | `~/.local/share/tomur` |
| macOS | `~/Library/Application Support/Tomur` |

Key paths inside the data directory:

| Path | Purpose |
| --- | --- |
| `<data>/config/tomur.json` | Local configuration file |
| `<data>/tomur.db` | SQLite database |
| `<data>/runtime` | Versioned native runtime cache |
| `<data>/models` | Local model directory and install manifest |
| `<data>/logs` | Log directory |

Override the data directory with `--data-dir <path>` or `TOMUR_DATA_DIR`. If the configuration file is damaged, the diagnostic flow moves it to `.damaged-<timestamp>` and writes a default configuration.

## 📦 Runtime Assets

Tomur release artifacts should carry the required C++ native dynamic libraries and prepare them into Tomur's managed runtime directory on first run or version change. Model weights are not packaged into the executable; `tomur pull` downloads them into the local model directory and records them in `<data>/models/models.manifest.json`.

Independent managed provider DLLs belong to the non-AOT self-contained release surface. Native AOT releases must statically reference compatible providers or clearly report that dynamic managed providers are unavailable. This distinction does not remove or downgrade existing native providers.

`tomur native prepare` extracts or repairs the native runtime bundle. `tomur doctor` checks runtime, models, SQLite, ports, proxy, and hardware status. Missing or damaged native libraries are reported through clear CLI, API, and UI diagnostics.

Windows x64 native build entry point:

```powershell
tomur native build --rid win-x64 --backend all
tomur native build --rid win-x64 --backend vulkan
tomur native build --rid win-x64 --backend sycl
tomur native build --rid win-x64 --backend openvino
tomur native build --rid win-x64 --backend intel
```

Use `--backend cpu` or `--backend cuda13` to build a single variant. `--backend intel` builds the llama.cpp `sycl`, `openvino`, and `vulkan` dynamic backend entries. When an Intel backend is missing or no device can be enumerated, Tomur keeps CPU fallback and reports the reason through `tomur doctor`, `/api/runtime/status`, and the Web Runtime panel.

## 🗺️ Roadmap

Long-term stage plans, completion scope, and follow-up work are maintained in [ROADMAP.md](./ROADMAP.md); completed history is maintained in [CHANGELOG.md](./CHANGELOG.md). This README keeps the project positioning, usage path, and current boundaries concise.
