# R15 GLM4 MoE Lite 异机验证

状态：进行中

本文定义 `managed-glm` 对 `glm4_moe_lite` 的异机转换与 smoke 边界。完整 GLM-4.7 已完成转换、资产校验、provider 加载和 readiness，最短真实 forward 仍在执行；实时服务器状态、下载任务和接手步骤见 [R15 远程 GLM 验证交接记录](./r15-remote-validation-handoff.md)。在真实 token 和完整响应证据形成前，本文不构成真实对话已通过或性能可用的证据。

## 固定输入

| 项目 | 值 |
| --- | --- |
| 源模型 | `cerebras/GLM-4.7-Flash-REAP-23B-A3B` |
| Hugging Face revision | `da315d1a734ba8501a014eb3ff53ca38cbcf63e5` |
| 模型类型 | `glm4_moe_lite` |
| 参数规模 | 23B total / 3B active，47 layers，48 routed experts，top-k 4 |
| License | MIT |
| 转换器参考 commit | `748787c3afa8ab336bb51bf616f212a04f209bba` |
| 转换布局 | int8 embedding/lm_head；int4 dense/shared/routed expert；`packed-offset` + `*.qs` |

源权重共 `45,993,145,128` bytes（42.834 GiB）：

| 文件 | bytes | SHA-256 |
| --- | ---: | --- |
| `model-00001-of-00009.safetensors` | 5,363,354,288 | `b230d13ddfce3d53835b9b3b97c7219321be8e9f3465d84de787e4a6b6459720` |
| `model-00002-of-00009.safetensors` | 5,364,819,824 | `642adcd78a9d886f6300135a629cf74245996e5fc58ff10add693f0e43b5dd90` |
| `model-00003-of-00009.safetensors` | 5,365,136,840 | `6d32b9b4173bb8179e25a66ce0c70ff3f10684d9120ffa7ba0ffb38011437431` |
| `model-00004-of-00009.safetensors` | 5,364,820,504 | `9c82d622e113410c959e165e698e87a9d0fd3730a9e99be5acc610ee747dff73` |
| `model-00005-of-00009.safetensors` | 5,365,136,840 | `d160458b9dab6c72f379948724d582981a78ce5d964db308f95046c3cacd7b64` |
| `model-00006-of-00009.safetensors` | 5,364,820,504 | `0f1259f8393bdd3cec42f1eb6ab8b91fa5b1693a69dd0a401a27de47e5c16ef4` |
| `model-00007-of-00009.safetensors` | 5,365,136,840 | `bd83eec1cccf4bf3d3f4af0e61d0035d9c395fbf18849baf7f7d01366ee29b95` |
| `model-00008-of-00009.safetensors` | 5,364,820,488 | `dff1b3e5fd3dcd5a6c2f01342b0e2ae609a022b73d4d8fce8555e26bc253e0f0` |
| `model-00009-of-00009.safetensors` | 3,075,099,000 | `c8233be0ef2f6327e34b9ce892dc141c9a9aa86f784fb616d1587557fa88cc4e` |

转换前必须逐项核对 size 与 SHA-256。任一 shard 不匹配时停止，不生成可用清单。

## 转换步骤

验证机应使用独立源目录、输出目录和 Tomur data directory。建议预留至少 80 GiB 可用空间；如使用转换器的远程逐 shard 模式，还必须固定源 revision 或在记录中保存开始时解析到的 revision。

```powershell
git clone https://github.com/JustVugg/colibri.git D:\work\colibri
git -C D:\work\colibri checkout 748787c3afa8ab336bb51bf616f212a04f209bba

huggingface-cli download cerebras/GLM-4.7-Flash-REAP-23B-A3B `
  --revision da315d1a734ba8501a014eb3ff53ca38cbcf63e5 `
  --local-dir D:\models\glm4-reap-source

python D:\work\colibri\c\tools\convert_fp8_to_int4.py `
  --indir D:\models\glm4-reap-source `
  --outdir D:\smoke\tomur-glm4\data\models\text\glm-4.7-flash-reap-23b-a3b `
  --ebits 4 --io-bits 8 --xbits 4 --n-layers 47
```

输出目录必须增加：

```json
{
  "schema_version": 1,
  "provider": "managed-glm",
  "architecture": "glm4_moe_lite",
  "display_name": "GLM 4.7 Flash REAP 23B A3B",
  "config": "config.json",
  "tokenizer": "tokenizer.json",
  "tensor_pattern": "out-*.safetensors",
  "quantization": "int4",
  "quantization_layout": "packed-offset",
  "capabilities": ["completion", "chat"]
}
```

转换完成后记录每个输出 shard 的 bytes 与 SHA-256、总大小、tensor 数量和转换 wall time。源目录可以在 checksum、转换和产物记录全部完成后清理。

## 代码与模型 smoke

以下命令仅在验证机执行；本机未运行：

```powershell
dotnet build Tomur.slnx --no-restore
dotnet test Tomur.slnx --no-restore

$env:TOMUR_PROVIDER_PATH = "<repo>\providers\Glm\bin\Debug\net10.0"
dotnet run --project app\Tomur.csproj -- serve `
  --data-dir D:\smoke\tomur-glm4\data `
  --urls http://127.0.0.1:5174
```

必须覆盖：

1. `tomur list`、`GET /v1/models` 与 `GET /api/tags` 只在清单、config、tokenizer、全部 tensor 和 scale 校验通过后展示模型。
2. manifest architecture 与 `config.json:model_type` 不一致时返回 `managed_model_invalid`。
3. OpenAI Chat 非流式与 SSE、Ollama Chat、Anthropic Messages 均使用 GLM4 MoE Lite prompt；至少记录一个中文、一个英文和一个代码请求。
4. 默认非 thinking prompt 使用 `[gMASK]<sop>` 后换行、assistant `</think>` closure；tool 消息使用 `<tool_response>` 包装。
5. context 超限、取消、损坏 shard 和缺少 `*.qs` 必须返回结构化诊断，不生成 token。
6. unload 后释放 session、shard handles、resident buffers 与 expert cache，服务仍可响应健康检查。

## 证据字段

异机记录至少包含 Tomur commit、OS/RID、CPU、RAM、存储介质、.NET SDK、隔离 data directory、模型 revision、转换器 commit、输出 checksum、context、resident/KV/scratch/expert-cache bytes、首次加载时间、首 token、token/s、expert hit/miss/eviction、disk bytes/wait 和进程峰值内存。

在上述证据形成前，只能声明 `glm4_moe_lite` 代码契约已接入；不能声明完整模型真实推理通过或性能可用。
