# R15 OLMoE Real-Model Smoke 记录

日期：2026-07-15

本文记录 `managed-olmoe` 使用原始 BF16 `allenai/OLMoE-1B-7B-0125-Instruct` 权重执行的真实模型 smoke。结论限定为模型发现、动态 provider 加载、完整权重加载、中文 Ollama 非流式对话、请求取消、session 诊断和卸载；OpenAI、Anthropic Messages、SSE streaming、完整 int8 转换和性能优化仍未验证。

## 环境

| 项目 | 值 |
| --- | --- |
| Tomur 基线 commit | `ca64ee1f741994173c3733d71cf7f457b228255e`，工作树包含本次未提交变更 |
| OS | Windows x64 `10.0.26200` |
| CPU | Intel Core Ultra 9 185H，22 logical processors |
| RAM | 63.5 GiB |
| .NET SDK | `10.0.301` |
| 隔离数据目录 | `D:\temp\tomur-r15-olmoe\data` |
| provider 目录 | `providers\Olmoe\bin\Debug\net10.0` |

provider 通过 `TOMUR_PROVIDER_PATH` 加载到 Tomur 进程中。smoke 使用 `http://127.0.0.1:5164`，没有使用用户默认 `%LOCALAPPDATA%\Tomur` 数据目录；两次启动的进程均在验证结束后卸载 session 并停止。

## 模型资产

模型目录为 `models\text\olmoe-1b-7b-0125-instruct`，包含 `config.json`、`tokenizer.json`、`model.safetensors.index.json`、三个 shard 和显式 `model.tomur.json`。权重文件如下：

| 文件 | bytes | SHA-256 |
| --- | ---: | --- |
| `model-00001-of-00003.safetensors` | 4,997,744,872 | `61874210CA7C360F43F8C622CECC12441083D40190EAE3B56BC9D6E1C0A30C1E` |
| `model-00002-of-00003.safetensors` | 4,997,235,176 | `C523A43B8A17269D5FAB33395048A83633F4D1D89C1958570CEA738E2BBE80C9` |
| `model-00003-of-00003.safetensors` | 3,843,741,912 | `97AE01E3519C52E63A018BCA96AB17A89C4CD5CAB1C6D742EFED0FA5C0E2BB17` |

权重合计 `13,838,721,960` bytes。`tomur list` 报告模型为 `text/olmoe-1b-7b-0125-instruct`、`managed-model`、`olmoe`、12.89 GiB；`GET /v1/models` 和 `GET /api/tags` 均可见该模型。`/api/runtime/status` 报告 `managed-glm` 与 `managed-olmoe` 两个 managed provider 已加载；缺少 llama.cpp native bundle 不阻止本次纯托管 provider 启动或推理。

## 加载与内存

真实模型首次加载日志为 `12,289 ms`，重启后的日志复测为 `8,552 ms`。重启复测的 session 诊断如下：

| 项目 | 数值 |
| --- | ---: |
| context | 512 |
| layers | 16 |
| attention heads / KV heads | 16 / 16 |
| routed experts / top-k | 64 / 8 |
| tensor shards / resident tensors | 3 / 147 |
| resident bytes | 1,906,843,648 |
| KV bytes | 134,217,728 |
| scratch bytes | 739,392 |
| expert cache bytes / slots per layer | 3,221,225,472 / 8 |
| initial expert disk reads / bytes | 128 / 1,610,612,736 |
| Tomur process private memory | 约 5.03 GiB |

expert storage format 为 BF16，运行路径为纯 C# scalar reference。原始 BF16 expert 在 cache 中展开为 F32，因此 3.0 GiB expert cache 是当前主要内存占用；这也是后续完整 rowwise int8 转换的主要动机。

## 对话结果

请求使用 Ollama `POST /api/chat`、`stream=false`、`num_ctx=512`、greedy sampling。

| 输入 | 限制 | 输出 | usage | HTTP wall time |
| --- | --- | --- | --- | ---: |
| `你好` | `num_predict=1` | `你` | 15 prompt + 1 completion | 228.545 s |
| `请只回答：你好` | `num_predict=4` | `你好！有` | 21 prompt + 4 completion | 350.398 s |

第二条输出末尾的“有”由 `num_predict=4` 截断，不作为完整回答质量样本。两次响应均为 HTTP 200，内容来自真实 tokenizer、chat template、完整 16 层 forward、MoE router、streamed experts 和 lm head，不是占位结果。非流式请求没有独立记录首 token latency；当前耗时只用于确认 scalar reference 性能边界，不能作为优化后吞吐指标。

## 诊断与取消

session 生命周期日志已使用中性文案：

```text
loading text generation session model=text/olmoe-1b-7b-0125-instruct ctx=512 gpuLayers=0 runtime=managed-olmoe
text generation session ready model=text/olmoe-1b-7b-0125-instruct elapsedMs=8552
```

重启后的短请求在客户端等待 25 秒后主动取消，服务记录 HTTP 499，进程保持可响应。此时 `GET /api/runtime/status` 返回 `runtime_loaded`、`managed-olmoe-generation` 和上述 session 内存/cache 诊断，不再由未使用的 llama.cpp native bundle 错误覆盖实际已加载的 managed session。

## 验证边界

| 检查 | 结果 |
| --- | --- |
| `dotnet build Tomur.slnx --no-restore` | 通过；报告既有 Debug Web embedded resource manifest 警告。 |
| `dotnet build app\Tomur.csproj --no-restore` | 日志与 Runtime 诊断修正后通过；报告同一既有 Web manifest 警告。 |
| `dotnet test Tomur.slnx --no-restore` | 通过，10 个测试程序集共 68/68；报告既有 Web manifest 与 `TinyFixtureBundle.cs:510` nullable 警告。 |
| `git diff --check` | 通过。 |

三个目标 shard 的 SHA-256 在 smoke 收尾时重新计算并与模型资产表一致。

当前可以声明：原始 BF16 OLMoE 模型已在 Tomur 单进程内完成真实中文非流式对话。当前不能声明：OpenAI、Anthropic Messages、SSE streaming、完整 int8 模型、跨平台或可用性能已验证。
