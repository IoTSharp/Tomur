# R15 远程 GLM 验证交接记录

快照时间：2026-07-16 14:49 CST（2026-07-16 06:49 UTC）

状态：GLM-4.7 已完成转换、资产校验、provider 加载、readiness 和最短真实 completion。生产 attention 默认切换到 Absorbed MLA 后，固定 1-token completion 从 Reference 基线 `186.596971s` 降至 `26.595764s`，返回 HTTP 200 和相同生成 token。该结果只证明 P0 与最短真实推理通过，不构成性能可用或完整协议矩阵通过。GLM-5.2 目录已达到 150 个正式文件且没有 `.part`，但主下载状态为失败，最终 inventory、size 和 SHA-256 审计仍待执行。

## 安全连接与本机入口

| 项目 | 值 |
| --- | --- |
| SSH 目标 | `root+vm-sT7XQqUJeZBAMQVN@140.207.205.81` |
| SSH 端口 | `32222` |
| 固定 host key | `SHA256:slYxO3acVQSZKqRD2XynFczxMcNFuIALD7AfZ8GD+5k` |
| 本机连接助手 | `%USERPROFILE%\.ssh\connect-vm-sT7XQqUJeZBAMQVN.ps1` |
| 本机加密凭据 | `%USERPROFILE%\.ssh\vm-sT7XQqUJeZBAMQVN.credential` |
| 本机 UI | `http://127.0.0.1:8188` |
| 远端服务 | `http://127.0.0.1:5174` |

加密凭据由 Windows DPAPI 绑定当前用户。仓库不保存明文密码，也不应把密码写入命令、日志或本文。交互式连接使用：

```powershell
& "$HOME\.ssh\connect-vm-sT7XQqUJeZBAMQVN.ps1"
```

包含 Bash 变量、管道、重定向或引号的远程命令必须遵守仓库 `AGENTS.md`，通过单引号 here-string 作为一个参数交给连接助手：

```powershell
$remoteScript = @'
set -eu
date -u
df -h /data
'@
& "$HOME\.ssh\connect-vm-sT7XQqUJeZBAMQVN.ps1" $remoteScript
```

本次快照中本机 `127.0.0.1:8188` 隧道未监听；远端 `127.0.0.1:5174/health` 返回 HTTP 200。需要访问 UI 时，应从 DPAPI 凭据重新建立隧道，或使用用户提供的 OpenSSH 转发命令并在交互提示中输入凭据。不要复用历史临时 `-pwfile`，也不要把服务直接绑定到公网地址。

## 验证机环境

| 项目 | 值 |
| --- | --- |
| Hostname | `09b5c508e9ab` |
| OS | Ubuntu 22.04.3 LTS x86_64 |
| RAM | 128 GiB |
| `/data` | 492 GiB；快照时约 21.7 GiB free |
| .NET SDK | `10.0.301`，安装于 `/data/tomur/dotnet` |
| Node.js | `v26.5.0`，安装于 `/data/tomur/node-v26.5.0` |
| Python venv | `/data/tomur/venvs/glm52` |
| Python 包 | numpy `2.2.6`、safetensors `0.8.0`、torch `2.13.0+cpu` |

转换器 FP8 selftest 相对误差为 `0.0225`，已通过。Web 已通过项目构建并嵌入 Tomur app。

## 代码与服务

本地分支为 `feature/managed-glm-provider`。本次验证修复已提交为：

```text
5734d37 fix(providers): enable GLM-4.7 validation
```

该提交包含 BPE `ignore_merges=true`、int4/int8 单投影与 paired 投影 row parallel、对应 tokenizer/kernel 测试，以及重复 switch pattern 编译修复。P0 继续基于本地未提交工作树构建，只把生产 MLA 默认模式改为 Absorbed；本机 M7 7/7、M9 6/6，Linux 隔离副本 M7 6/6、M9 6/6 均通过。最终 provider SHA-256 为 `4f4f251b438ff0b0695f8b561de108b5e0cffc837e2a1609972e3ebb6659f620`。

远端当前服务：

| 项目 | 值 |
| --- | --- |
| PID | `28881`（PID 只用于当前快照，恢复时以命令行和端口判断） |
| artifact | `/data/tomur/artifacts/glm47-p0-absorbed-20260716-1445c/Tomur.dll` |
| provider | `/data/tomur/artifacts/glm47-p0-absorbed-20260716-1445c/providers/Tomur.Providers.Glm.dll` |
| data directory | `/data/tomur/smoke/glm47-managed-wt-20260716-1315-bomfix/data` |
| listen URL | `http://127.0.0.1:5174` |
| service log | `/data/tomur/smoke/glm47-p0-absorbed-20260716-1445c/service/service.log` |

P0 artifact 以已经通过真实请求的 App/Web/native 产物为基线，只替换 checksum 已更新的 managed provider DLL，避免覆盖本地工作树中尚未提交的 kernel、BOM 和状态修复。最终 App SHA-256 为 `22b3fefc76170d3dbb0ef3e324b8f3782e5b3f043215a4f71106c55522666e7a`。

## GLM-4.7 资产

| 项目 | 值 |
| --- | --- |
| 源模型 | `cerebras/GLM-4.7-Flash-REAP-23B-A3B` |
| revision | `da315d1a734ba8501a014eb3ff53ca38cbcf63e5` |
| architecture | `glm4_moe_lite` |
| 转换目录 | `/data/tomur/smoke/glm47/data/models/text/glm-4.7-flash-reap-23b-a3b` |
| 转换结果 | 9 shards、14,285 tensors、11,904,654,629 bytes |
| 量化 | int4 expert/dense；int8 embedding/lm_head；`packed-offset` |

9 个源 shard 已按固定 revision 下载并通过 SHA-256。转换产物归档已完成并验证：

| 项目 | 值 |
| --- | --- |
| 归档 | `/data/tomur/artifacts/models/glm-4.7-flash-reap-23b-a3b-packed-offset.tar.zst` |
| 大小 | 9,160,754,234 bytes |
| SHA-256 | `796194f016a4ffb9ba3a290f5c350594b4339b9e5baff524f4c97e12ee4fd68a` |
| 恢复说明 | 同目录 `*.RESTORE.txt` |

归档使用长距离 Zstandard 窗口；解压必须带 `--long=31`，例如：

```bash
zstd --long=31 -d -c \
  /data/tomur/artifacts/models/glm-4.7-flash-reap-23b-a3b-packed-offset.tar.zst \
  | tar -xf - -C <target-directory>
```

## GLM-4.7 当前验证状态

`managed-glm` 已加载，模型已进入 `/v1/models` 和 `/api/models/installed`。readiness 已确认 9 shards、14,285 tensors、1,418,884,480 resident bytes、443,547,648 planned KV bytes 和 3,191,200 planned scratch bytes。当前 session 使用 `AVX2 packed int4/int8 and Vector256 F32`、parallelism `12`。

最短真实推理已通过，但性能与完整协议矩阵仍未通过：

1. 原单线程量化路径的 1-token completion 在 `585.228s` 后仍未完成；unload 后返回结构化 `503 session_unloaded`。正式 unload 约耗时 `1.58-2.48s`，资源释放路径有效。
2. 修正量化 kernel 与启动状态后，Reference MLA 的 `prompt="a"`、`max_tokens=1` completion 返回 HTTP 200，耗时 `186.596971s`，输出 `" br"`。
3. 生产默认切换到 Absorbed MLA 后，同一模型、请求、AVX2 kernel 和数据目录返回 HTTP 200，耗时 `26.595764s`，输出仍为 `" br"`；端到端改善 `7.02x`，耗时降低 `85.7%`。
4. Absorbed 请求报告 forward active elapsed `24.6s`、3 prompt tokens、1 completion token；完成后 session 为 `busy=false`、`forward_verified=true`、request count 为 1。
5. 该请求累计 464 次 expert disk reads、2,198,929,408 bytes，cache hit/miss/eviction 为 94/464/188。P1 expert cache、批量 prefill、kernel 和请求调度尚未实施。

P0 最终证据目录为 `/data/tomur/smoke/glm47-p0-absorbed-20260716-1445c/service`，包含 health、doctor、completion 响应、curl timing、服务日志和请求前后 runtime 快照。历史失败证据继续保留在 `/data/tomur/smoke/glm47/service`，不得覆盖或删除。

## GLM-5.2 下载状态

| 项目 | 值 |
| --- | --- |
| 模型 | `mateogrgic/GLM-5.2-colibri-int4-with-int8-mtp` |
| revision | `3cc8db99b1b13fc79325d987ba3c1c430766b3b8` |
| 固定清单 | 150 files、144 safetensors、145 LFS objects |
| 固定总大小 | 383,760,077,466 bytes |
| 目标目录 | `/data/tomur/data/models/text/glm-5.2-colibri-int4-with-int8-mtp` |
| 下载控制目录 | `/data/tomur/downloads/glm-5.2-colibri-int4-with-int8-mtp` |

快照时目标目录包含 150 个正式文件和 0 个 `.part` 文件，`du -sb` 为 383,760,089,754 bytes（包含目录元数据）。文件数量和 partial 清理已达到固定清单边界，但主下载状态以 exit 1 结束，尚不能据此声明全部 LFS size 与 SHA-256 通过。三个区间状态为：

| 区间 | 状态文件 | 快照状态 |
| --- | --- | --- |
| 主段 | `status` | `FAILED exit=1 at=2026-07-16T04:58:02Z` |
| tail | `tail.status` | `COMPLETE files=48` |
| mid-high | `mid-high.status` | `COMPLETE files=14` |

下载器对每个 LFS 对象执行 size 和 SHA-256 校验，只在校验成功后把 `.part` 原子改名为正式文件。当前没有下载进程；应先复核主段失败原因，并对固定 revision 的 150-file inventory、144 个 safetensors、145 个 LFS size 与 SHA-256 执行最终审计，不要仅根据无 `.part` 判定完成。

GLM-5.2 只有在以下条件全部满足后才算下载完成：固定 revision 不变；150 个文件 inventory 完整；144 个 safetensors 和 145 个 LFS 对象齐全；不存在 `.part`；所有 LFS size 与 SHA-256 通过。当前 `/data` 可用空间约 21.7 GiB；在审计和清理前不得启动新的大体积转换副本。

## 接手续查命令

本机 API：

```powershell
Invoke-RestMethod http://127.0.0.1:8188/health
Invoke-RestMethod http://127.0.0.1:8188/api/runtime/status
Invoke-RestMethod http://127.0.0.1:8188/api/models/installed
Invoke-RestMethod http://127.0.0.1:8188/v1/models
```

远端下载与推理状态：

```powershell
$remoteScript = @'
set -eu
root=/data/tomur/data/models/text/glm-5.2-colibri-int4-with-int8-mtp
find "$root" -maxdepth 1 -type f ! -name '*.part' | wc -l
find "$root" -maxdepth 1 -type f -name '*.part' | wc -l
du -sb "$root"
tail -n 3 /data/tomur/downloads/glm-5.2-colibri-int4-with-int8-mtp/status
tail -n 3 /data/tomur/downloads/glm-5.2-colibri-int4-with-int8-mtp/tail.status
tail -n 3 /data/tomur/downloads/glm-5.2-colibri-int4-with-int8-mtp/mid-high.status
ps -eo pid,etimes,%cpu,%mem,rss,args | grep -E 'Tomur.dll|completion-parallel|glm-5.2' | grep -v grep || true
ls -l /data/tomur/smoke/glm47/service/completion-parallel-*
'@
& "$HOME\.ssh\connect-vm-sT7XQqUJeZBAMQVN.ps1" $remoteScript
```

## 后续顺序

1. 保留 P0 的 Reference/Absorbed completion、timing、runtime/session 快照和 artifact checksum；失败证据不得覆盖或删除。
2. 在决定 P1 前，补充同一请求的 warm/hot 复测以及最短 Chat 请求，分离模型加载、attention、MoE 和 expert I/O 时间。
3. 对 GLM-5.2 执行固定 revision 的最终 inventory、size 与 SHA-256 审计，查明主段 `FAILED exit=1`；审计通过前不标记下载完成。
4. 审计完成后增加并核对 `model.tomur.json`：provider 为 `managed-glm`、architecture 为 `glm_moe_dsa`、quantization 为 `int4`、layout 为 `packed-offset`。tensor pattern 必须覆盖正式 `out-*.safetensors`，并先确认 MTP shard 与当前 probe 契约。
5. 以 GLM-5.2 执行 doctor/readiness、最短真实 forward、兼容 API 和 UI 验证，再形成最终验证报告。未完成前不声明 GLM-5.2 可用。
6. 远端 GLM-4.7 服务当前继续运行；本机 8188 隧道需要按安全连接步骤重新建立。
