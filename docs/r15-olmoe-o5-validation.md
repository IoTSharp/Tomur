# R15 OLMoE O5 验证记录

状态：待执行

本文定义 `managed-olmoe` O5 的完整转换、协议和性能证据口径。当前仓库已接入转换、协议矩阵和性能诊断代码；本记录中的构建、测试、完整模型转换、服务启动与 API 请求尚未执行，表格不得在实跑前填写为通过。

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

待执行命令：

```powershell
dotnet build Tomur.slnx --no-restore
dotnet test tests/Tomur.Providers.Olmoe.Tests/Tomur.Providers.Olmoe.Tests.csproj --no-build
```

测试范围包括 floating tiny fixture 转换后的 probe/forward、取消清理、不覆盖既有目录、OpenAI/Ollama/Anthropic 非流式序列化、OpenAI/Anthropic SSE、Ollama NDJSON、增量文本、usage 终帧，以及 session 性能字段。

## 真实模型资产

| 项目 | 结果 |
| --- | --- |
| 输入模型 revision | 待记录 |
| 输入 shard bytes / SHA-256 | 待从 `conversion.manifest.json` 记录 |
| 输出 tensor file bytes / SHA-256 | 待记录 |
| source / output tensor count | 待记录 |
| dense dtype | 待确认 |
| expert dtype / layout | 待确认 `I8` / `rowwise-qs` |
| Catalog / provider / readiness | 待执行 |

## 协议矩阵

使用同一转换后模型、同一 prompt、同一 context 和 greedy sampling，分别记录非流式完整文本、streaming 增量、终帧 usage 与错误形状。

| 协议 | 非流式 | Streaming | 终帧 | 结果 |
| --- | --- | --- | --- | --- |
| OpenAI `POST /v1/chat/completions` | JSON completion | SSE delta | usage + `[DONE]` | 待执行 |
| Ollama `POST /api/chat` | JSON message | NDJSON `done=false` | `done=true` + counts | 待执行 |
| Anthropic `POST /v1/messages` | content block | SSE content delta | message delta/stop + usage | 待执行 |

每项必须证明输出来自真实模型 forward，不使用随机 fixture 或占位 completion。另行记录模型未下载、资产损坏、context 超限、取消和流中失败的协议风格诊断。

## 性能口径

`/api/runtime/status` 的 session 快照提供以下字段：

| 字段 | 定义 | 结果 |
| --- | --- | --- |
| `load_elapsed_milliseconds` | provider model、resident tensor 与最小 expert cache 建立耗时 | 待记录 |
| `last_first_token_milliseconds` | generation 开始至首次采样 token | 待记录 |
| `last_generation_milliseconds` | prompt forward 与全部输出 token 的总生成耗时 | 待记录 |
| `last_output_tokens_per_second` | completion tokens / 总生成秒数 | 待记录 |
| `last_decode_tokens_per_second` | 首 token 后剩余 completion tokens / decode 秒数；至少两个输出 token 时有效 | 待记录 |

同时记录 context、prompt/completion token、进程 private/working-set 峰值、resident/KV/scratch/expert cache bytes、expert cache hit/miss/eviction、disk reads/bytes、CPU、RAM、存储介质、OS/RID、.NET SDK 和 Tomur commit。性能至少执行一次冷启动和三次同配置热请求；不得用客户端 timeout 或单次 HTTP wall time代替上述口径。

## 完成条件

O5 只有在以下证据全部落盘后才能标记完成：完整模型转换产物通过 checksum 与 probe；转换后模型完成真实对话；三协议非流式与 streaming 全部通过；性能和资源数据按统一口径记录；session unload 后 shard handle 与模型目录可独占访问。未完成项继续保留为待执行，不以代码存在替代实跑证据。
