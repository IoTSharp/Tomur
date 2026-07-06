# R14 Intel GPU / NPU Smoke 记录

本文记录 R14 Intel GPU / NPU 加速支持的实机 smoke 入口、证据字段和当前执行状态。R14 的实现边界是：backend 可见、设备可枚举、选中 accelerator 和真实推理通过必须分开记录；没有真实请求和 token usage 的设备路径不得标记为推理已通过。

## 当前状态

| 范围 | 状态 | 说明 |
| --- | --- | --- |
| CPU fallback | 已接入 | 缺少 `ggml-sycl`、`ggml-openvino` 或 `ggml-vulkan` 时，Tomur 保持 CPU 文本 API 可用，并通过 doctor、`/api/runtime/status` 和 Web Runtime 返回修复提示。 |
| Intel GPU backend 可见性 | 已接入 | `sycl`、`openvino` 与 `vulkan` backend 会在 runtime 状态中报告库文件可见性和设备枚举结果。 |
| Intel GPU offload 请求 | 已接入 | 当 backend 可见且设备可枚举时，Tomur 会把选中 device list、GPU layers 和 accelerator key 传给 llama.cpp model load。 |
| Intel NPU opt-in | 已接入 | NPU 只在 `runtime.accelerator.allow_npu=true` 且 OpenVINO backend/设备可用时参与选择；`NPU` 与 `NPU.*` 会设置受控 OpenVINO 环境变量。 |
| Intel NPU 不适配诊断 | 已接入 | NPU 上的模型加载、context 初始化、prompt prefill、生成 decode 和 embedding decode 失败会返回 NPU 专用错误码，不伪造输出。 |
| Intel GPU / NPU 真实 smoke | 待实机执行 | 本轮未执行构建、启动或真实模型请求；执行后必须把结果补入本文和证据目录。 |

## 证据目录

实机执行后使用以下目录保存原始响应、日志和摘要：

```text
docs/r14-smoke-evidence/<yyyy-mm-dd>/
  runtime-status.gpu.response.json
  chat-completions.gpu.response.json
  runtime-status.npu.response.json
  chat-completions.npu.response.json
  npu-failure.response.json
  smoke-summary.json
```

## 必填证据字段

每条 Intel GPU / NPU smoke 记录必须包含：

| 字段 | 来源 | 要求 |
| --- | --- | --- |
| backend | `/api/runtime/status.acceleration.effective_backend` | 例如 `sycl`、`openvino`、`vulkan` 或 `cpu` fallback。 |
| backend library | `/api/runtime/status.acceleration.backends` | 记录 `ggml-sycl`、`ggml-openvino` 或 `ggml-vulkan` 的 `available/missing` 状态。 |
| device | `/api/runtime/status.acceleration.selected_accelerator` | 记录设备名、kind、backend、selection key 和显存/内存。 |
| model | 请求体和 `/v1/models` | 记录模型 ID、文件、量化等级和大小。 |
| context | 请求体或 options | 记录 `num_ctx`、prompt token、completion token 和 total token。 |
| GPU layers | `/api/runtime/status.acceleration.effective_gpu_layers` 与成功响应 diagnostics | 记录实际请求的 offload layers。 |
| success evidence | `/v1/chat/completions` 响应 | 成功时必须包含非空文本和 token usage。 |
| failure evidence | 协议错误响应 | 失败时必须包含错误 code、message、actions 和 fallback/不适配说明。 |

## 建议执行路径

以下命令只在用户明确要求验证或在实机验收窗口中执行：

```powershell
tomur native build --rid win-x64 --backend intel
tomur native prepare
tomur doctor
tomur serve --urls http://127.0.0.1:5140
```

Intel GPU smoke：

```powershell
curl.exe http://127.0.0.1:5140/api/runtime/status -o docs/r14-smoke-evidence/<date>/runtime-status.gpu.response.json
curl.exe http://127.0.0.1:5140/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d "{\"model\":\"<local-text-model>\",\"messages\":[{\"role\":\"user\",\"content\":\"Say OK42.\"}],\"max_tokens\":16,\"stream\":false}" `
  -o docs/r14-smoke-evidence/<date>/chat-completions.gpu.response.json
```

Intel NPU smoke：

```powershell
curl.exe http://127.0.0.1:5140/api/runtime/status -o docs/r14-smoke-evidence/<date>/runtime-status.npu.response.json
curl.exe http://127.0.0.1:5140/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d "{\"model\":\"<local-text-model>\",\"messages\":[{\"role\":\"user\",\"content\":\"Say OK42.\"}],\"max_tokens\":16,\"stream\":false}" `
  -o docs/r14-smoke-evidence/<date>/chat-completions.npu.response.json
```

NPU 不适配 smoke 可以通过两种方式验证协议错误响应：

1. 对 `/v1/chat/completions` 发送超过当前 NPU 安全阈值的长 prompt，保留 OpenAI 风格错误响应。
2. 对 `/api/chat` 显式设置超过当前 NPU 安全阈值的 `options.num_ctx`，保留 Ollama 风格错误响应。

```powershell
curl.exe http://127.0.0.1:5140/api/chat `
  -H "Content-Type: application/json" `
  -d "{\"model\":\"<local-text-model>\",\"messages\":[{\"role\":\"user\",\"content\":\"Say OK42.\"}],\"stream\":false,\"options\":{\"num_ctx\":8192,\"num_predict\":16}}" `
  -o docs/r14-smoke-evidence/<date>/npu-failure.response.json
```

## 当前 R14 结论

R14 的代码与诊断面已经覆盖 Intel backend 可见性、设备枚举、accelerator 选择、CPU fallback 和 NPU 不适配错误返回。真实 Intel GPU / NPU 推理通过仍需要实机 smoke 证据；完成前不得把具体 GPU/NPU 路径写成已真实推理通过。
