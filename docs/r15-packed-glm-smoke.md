# R15 Packed GLM Smoke 记录

日期：2026-07-14

本文记录 R15 M10 对 packed rowwise GLM / MoE 模型格式的针对性 smoke。结论限定为模型格式、Catalog、provider load、scalar forward 与现有兼容 API 链路；随机 tiny 权重不能证明自然语言质量，完整 GLM-5.2 模型也未在本机加载。

## 环境

| 项目 | 值 |
| --- | --- |
| Tomur 基线 commit | `ca64ee1f741994173c3733d71cf7f457b228255e`，工作树包含本次未提交变更 |
| 上游格式参考 commit | `748787c3afa8ab336bb51bf616f212a04f209bba` |
| OS | Windows 11 x64 `10.0.26200` |
| CPU | Intel Core Ultra 9 185H，22 logical processors |
| RAM | 63.5 GiB |
| .NET SDK | `10.0.301` |
| 隔离数据目录 | `%TEMP%\tomur-packed-smoke-56ad9958ca764e53bbd2afa82397d64a\data` |

provider 由 `TOMUR_PROVIDER_PATH=providers\Glm\bin\Debug\net10.0` 加载。HTTP smoke 使用 Development 环境和短生命周期本地端口，结束后进程已停止；没有使用用户默认 `%LOCALAPPDATA%\Tomur` 数据目录。

## 格式对齐

本轮对齐的外部 packed rowwise 约定如下：

1. int4 payload 使用 offset-binary nibble，解码值为 `nibble - 8`。
2. 每个量化 matrix 使用同名前缀 `*.qs` F32 per-row scale。
3. U8 payload 按 matrix 的实际元素数区分 int8 与 packed int4，支持默认转换参数中的 int8 embedding/lm_head 与 int4 dense/expert 混合精度。
4. resident 量化权重保持压缩存储，在 embedding gather、matvec 和 MLA element access 时按需读取，不在加载时展开成 F32。
5. 只有清单显式声明 `"quantization_layout": "packed-offset"` 时启用该语义；原 `separate-scales` 行为保持不变。
6. Chat 请求把结构化 role/content 直接交给 GLM prompt template；role token 后不增加换行，并在 tokenizer 具备相应 token 时追加 `<think></think>`。

tiny fixture 使用生产张量名和一层 `glm_moe_dsa` 配置生成，再通过上游 `c/tools/convert_fp8_to_int4.py` 转换：

```powershell
python c/tools/convert_fp8_to_int4.py --indir <tiny-source> --outdir <packed> --ebits 4 --io-bits 4 --xbits 4 --n-layers 1
python c/tools/convert_fp8_to_int4.py --indir <tiny-source> --outdir <packed-mixed> --ebits 4 --io-bits 8 --xbits 4 --n-layers 1
```

转换目录随后增加 Tomur `model.tomur.json`，provider ID 仍为 `managed-glm`，没有引入外部 runtime 名称、程序集或 API。

## Tiny Smoke 结果

| 目录 | 转换参数 | 大小 | 验证结果 |
| --- | --- | ---: | --- |
| `packed` | embed/dense/expert 均为 int4 | 7,420 bytes | `tomur list` / Catalog 可见；OpenAI Chat 非流式返回 `hello`，usage 为 5 prompt + 1 completion；SSE 正常以 `[DONE]` 结束；Ollama `/api/chat` 与 Anthropic `/v1/messages` 均返回 `hello`。 |
| `packed-mixed` | embedding/lm_head int8，dense/expert int4 | 7,498 bytes | `/health` 为 `Healthy`，`/v1/models` 可见 `text/packed-glm-mixed-tiny`；OpenAI Chat 返回 `Tomur`，finish reason 为 `stop`，usage 为 6 prompt + 1 completion。 |

这些文本来自固定随机 tiny 权重，只用于确认生成的是模型 forward token 而不是占位结果。它们不应被解释为模型能进行自然语言对话。

## 完整模型容量

上游推荐的预转换 `mateogrgic/GLM-5.2-colibri-int4-with-int8-mtp` 资产元数据固定在 Hugging Face commit `3cc8db99b1b13fc79325d987ba3c1c430766b3b8`：

| 项目 | 数值 |
| --- | ---: |
| 文件数 | 150 |
| safetensors 文件数 | 144 |
| 总大小 | 383,760,077,466 bytes（357.4 GiB） |
| 其中 MTP 文件 | 9.28 GiB |

执行前的本机可用空间为：C 盘 95.6 GiB、D 盘 134.8 GiB、G 盘 58.6 GiB。没有单盘能容纳 357.4 GiB 模型，三盘可用空间合计也不足，因此本轮没有启动完整模型下载，也没有完整模型真实对话、首 token、token/s、expert I/O 或语言质量证据。自行从 FP8 转换时，上游也建议预留约 400 GB 的目标盘空间。

## Oracle 边界

上游 `make_glm_oracle.py` 在当前 Transformers `5.13.1` 环境下生成 fused expert 名称，例如 `mlp.experts.gate_up_proj`；当前上游 converter/C loader 和 Tomur provider 都按 per-expert 张量名读取。该 oracle 目录未能进入可加载状态，因此本轮不声明 Transformers oracle 对齐通过。`safetensors 0.8.0` 与 `transformers 5.13.1` 只安装在验证环境中，没有加入 Tomur 产品依赖。

## 验证命令

```powershell
dotnet build Tomur.slnx --no-restore
dotnet test Tomur.slnx --no-restore
git diff --check
```

| 检查 | 结果 |
| --- | --- |
| `dotnet build Tomur.slnx --no-restore` | 通过；报告既有 Debug Web embedded resource manifest 警告和 `tests/Tomur.Providers.M2.Tests/TinyFixtureBundle.cs:510` nullable 警告。 |
| `dotnet test Tomur.slnx --no-restore` | 通过，9 个测试程序集共 62/62；本次增量构建日志只重新报告 Web manifest 警告。 |
| `git diff --check` | 通过。 |

构建与完整测试验证了 Tomur 主程序、provider 和 M1-M9 测试项目；完整模型、跨平台、性能、MTP、DSA 与发布验证仍归 M14。

## 当前结论

M10 处于进行中。Tomur 已能加载显式标记的 packed-offset tiny 模型，并通过现有 OpenAI、Ollama 和 Anthropic Messages 入口执行真实 scalar forward。完整 744B 模型在本机因磁盘容量不足未运行，因此 managed GLM provider 继续保持实验状态，不标记为可用于真实聊天。
