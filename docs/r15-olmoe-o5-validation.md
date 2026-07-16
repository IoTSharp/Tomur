# R15 OLMoE O5 验证记录

状态：部分通过；完整 int8 转换、provider 专项回归、Catalog/readiness 与真实非流式 forward 已通过，streaming、Anthropic、完整性能矩阵和 unload 资源复核待执行

本文定义 `managed-olmoe` O5 的完整转换、协议和性能证据口径，并记录 2026-07-16 的 Linux 服务器实跑结果。本轮所有模型加载与 forward 均由 Tomur 单进程内的 `managed-olmoe` 纯 C# provider 执行，不依赖 Ollama 服务、外部推理进程或 native backend。

## 服务器环境

| 项目 | 值 |
| --- | --- |
| OS / architecture | Ubuntu 22.04.3 LTS / x86_64 |
| CPU | Intel Core i7-8550U，12 logical processors |
| RAM | 128 GiB |
| .NET SDK | 10.0.301 |
| 代码基线 | `c844718`，另含本次 `OlmoeConversionTests` namespace 修复 |
| 隔离数据目录 | `/data/tomur/smoke/olmoe/data` |
| provider 目录 | `/data/tomur/work/Tomur-c8447187/providers/Olmoe/bin/Debug/net10.0` |

## 转换边界

转换输入必须是显式声明 `managed-olmoe` / `olmoe` 和 `f32|f16|bf16` 的完整模型目录。转换器保留 resident dense tensor 的原始 dtype，只把每层全部 routed expert `gate_proj`、`up_proj` 和 `down_proj` 矩阵转换为 signed int8，并写入同名 `*.qs` F32 per-row scale。

转换入口：

```powershell
$env:TOMUR_PROVIDER_PATH = '<provider-build-output>'
dotnet run --project app/Tomur.csproj -- internal model-convert `
  --provider managed-olmoe `
  --source '<bf16-model-directory>' `
  --output '<int8-model-directory>'
```

输出目录必须不存在。转换过程先写入输出目录同盘的 `.partial-*` 临时目录，完成 safetensors header/tensor probe、源 shard SHA-256、输出 SHA-256 和 `conversion.manifest.json` 后再原子发布。取消或失败不得留下正式输出目录，也不得覆盖既有目录。

## 自动化入口

服务器执行命令：

```bash
dotnet restore tests/Tomur.Providers.Olmoe.Tests/Tomur.Providers.Olmoe.Tests.csproj
dotnet build tests/Tomur.Providers.Olmoe.Tests/Tomur.Providers.Olmoe.Tests.csproj --no-restore
dotnet test tests/Tomur.Providers.Olmoe.Tests/Tomur.Providers.Olmoe.Tests.csproj --no-build --no-restore
```

首次 solution `--no-restore` 构建因导出快照缺少 `project.assets.json` 失败；专项 restore 后又发现 `OlmoeConversionTests.cs` 缺少 `Tomur.Runtime` namespace。修复后专项构建通过，保留一条既有 `xUnit1031` warning，测试结果为 33/33 通过。覆盖范围包括 floating tiny fixture 转换后的 probe/forward、取消清理、不覆盖既有目录、三协议非流式与 streaming 序列化、增量文本、usage 终帧，以及 session 性能字段。

## 真实模型资产

| 项目 | 结果 |
| --- | --- |
| 输入模型 revision | `b89a7c4bc24fb9e55ce2543c9458ce0ca5c4650e` |
| 输入 shard bytes / SHA-256 | 4,997,744,872 / `61874210...A30C1E`；4,997,235,176 / `C523A43B...E80C9`；3,843,741,912 / `97AE01E3...E2BB17` |
| 输出 tensor file bytes / SHA-256 | 7,413,415,685 / `2FB92463F5271AB780A1F480440909A7D6F22F84C46B702816EA26ED794AFE4A` |
| source / output tensor count | 3,219 / 6,291 |
| 转换耗时 | `00:04:36.9376096` |
| dense dtype | 保留 BF16 |
| expert dtype / layout | `I8` / `rowwise-qs` |
| Catalog / provider / readiness | `managed-olmoe` 已加载；metadata/assets ready；1 shard / 6,291 tensors |

转换器写入 `conversion.manifest.json` 后完成输出 probe 与 SHA-256；另行执行的 `sha256sum` 与转换清单一致。正式输出目录由同盘 `.partial-*` 原子发布，没有残留 partial 目录。

## 协议矩阵

使用同一转换后模型、同一 prompt、同一 context 和 greedy sampling，分别记录非流式完整文本、streaming 增量、终帧 usage 与错误形状。

| 协议 | 非流式 | Streaming | 终帧 | 结果 |
| --- | --- | --- | --- | --- |
| OpenAI `POST /v1/chat/completions` | HTTP 200，`Hello`，14 prompt + 1 completion | 未执行 | 未执行 | 非流式通过 |
| Tomur `POST /api/chat`（Ollama-compatible） | HTTP 200，`Hello`，14 prompt + 1 completion | 未执行 | 非流式 `done=true` + counts 通过 | 非流式通过 |
| Anthropic `POST /v1/messages` | 未执行 | 未执行 | 未执行 | 待执行 |

两次非流式请求都由同一 `managed-olmoe` int8 session 完成真实 forward，不使用随机 fixture 或占位 completion。Tomur Chat 请求的 HTTP wall time 为 58.091571 秒，服务报告 generation 约 46.267 秒；OpenAI 非流式请求为 56.454379 秒。本轮按用户要求停止扩展协议矩阵，因此 streaming 与 Anthropic 保持待执行。另行记录模型未下载、资产损坏、context 超限、取消和流中失败的协议风格诊断。

## 性能口径

`/api/runtime/status` 的 session 快照提供以下字段：

| 字段 | 定义 | 结果 |
| --- | --- | --- |
| `load_elapsed_milliseconds` | provider model、resident tensor 与最小 expert cache 建立耗时 | 11,236 ms |
| `last_first_token_milliseconds` | generation 开始至首次采样 token | 46,167.7069 ms |
| `last_generation_milliseconds` | prompt forward 与全部输出 token 的总生成耗时 | 46,170.499 ms |
| `last_output_tokens_per_second` | completion tokens / 总生成秒数 | 0.0216589 token/s |
| `last_decode_tokens_per_second` | 首 token 后剩余 completion tokens / decode 秒数；至少两个输出 token 时有效 | 未产生；本次只生成 1 token |

session 使用 512 context、14 prompt token 和 1 completion token。快照报告 resident 1,906,843,648 bytes、KV 134,217,728 bytes、scratch 739,392 bytes、expert cache 807,403,520 bytes；首次请求结束时 expert cache hit/miss/eviction 为 572/1,220/1,092，disk reads/bytes 为 1,220/7,695,564,800。同期另一项 GLM 验证占用约 6.5 个 CPU core，因此这些数据只证明诊断字段和真实 forward，不作为独占性能基准。冷启动加三次同配置热请求、至少 2 token decode 和进程峰值仍待执行。

## 服务与前端入口

隔离服务运行于远端 `127.0.0.1:5175`，`GET /`、`GET /health`、`GET /api/version`、`GET /v1/models`、`GET /api/models/installed` 和 `GET /api/runtime/status` 均返回 HTTP 200。通过本机 SSH 隧道映射到 `127.0.0.1:8189` 后，Web Chat 页面、health 与 int8 模型列表可访问；session 诊断明确报告 `provider: managed-olmoe` 和 `mode: managed-olmoe-generation`。

## 完成条件

本轮可以声明：`providers/Olmoe` 专项回归通过，完整 int8 转换产物通过 checksum/probe/readiness，转换后真实模型完成非流式 forward，Web Chat 入口可使用。O5 仍保持进行中，直到三协议 streaming、Anthropic、完整冷/热性能矩阵、至少 2 token decode 与 session unload 后 shard handle 独占访问均形成证据。
