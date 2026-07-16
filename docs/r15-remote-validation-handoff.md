# R15 远程 GLM 验证交接记录

快照时间：2026-07-16 12:31 CST（2026-07-16 04:31 UTC）

状态：GLM-4.7 已完成转换、资产校验、provider 加载和 readiness；并行版最短真实 forward 仍在执行，尚未产生 token。GLM-5.2 仍在下载。本记录用于后续会话继续同一台验证机上的工作，不构成完整模型真实推理通过或性能可用的结论。

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

当前本机隧道由 `plink` PID `57120` 提供，监听 `127.0.0.1:8188` 和 `::1:8188`，转发到远端 `127.0.0.1:5174`。本次快照中 `/` 与 `/health` 均返回 HTTP 200。隧道启动时使用的临时 `-pwfile` 不能作为后续恢复入口；隧道失效时应从 DPAPI 凭据重新建立，或使用用户提供的 OpenSSH 转发命令并在交互提示中输入凭据。不要把服务直接绑定到公网地址。

## 验证机环境

| 项目 | 值 |
| --- | --- |
| Hostname | `09b5c508e9ab` |
| OS | Ubuntu 22.04.3 LTS x86_64 |
| RAM | 128 GiB；快照时约 115 GiB available |
| `/data` | 492 GiB；快照时 359 GiB used、108 GiB free |
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

该提交包含 BPE `ignore_merges=true`、int4/int8 单投影与 paired 投影 row parallel、对应 tokenizer/kernel 测试，以及重复 switch pattern 编译修复。远端针对性测试已通过，最终构建为 0 warnings / 0 errors。

远端当前服务：

| 项目 | 值 |
| --- | --- |
| PID | `15599`（PID 只用于当前快照，恢复时以命令行和端口判断） |
| artifact | `/data/tomur/artifacts/c8447187-fixes/bin/Tomur/release/Tomur.dll` |
| provider | `/data/tomur/artifacts/c8447187-fixes/bin/Tomur.Providers.Glm/release/Tomur.Providers.Glm.dll` |
| data directory | `/data/tomur/smoke/glm47/data` |
| listen URL | `http://127.0.0.1:5174` |
| service log | `/data/tomur/smoke/glm47/service/service.log` |

artifact 目录名保留了构建时的基线 `c8447187`；其中的五项验证修复现已对应本地提交 `5734d37`。不要只根据目录名判断远端代码状态。

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

`managed-glm` 已加载，模型已进入 `/v1/models` 和 `/api/models/installed`。readiness 已确认 9 shards、14,285 tensors、1,418,884,480 resident bytes、443,547,648 planned KV bytes 和 3,191,200 planned scratch bytes。当前 session 使用 `SIMD Vector256`、parallelism `12`，服务进程快照约使用 11 个 CPU core。

当前不能写成“真实推理通过”：

1. 原单线程量化路径的 1-token completion 在 `585.228s` 后仍未完成；unload 后返回结构化 `503 session_unloaded`。正式 unload 约耗时 `1.58-2.48s`，资源释放路径有效。
2. 并行修复部署后，`prompt="a"`、`max_tokens=1` 的请求仍在执行。快照时 session 为 `busy=true`、`forward_verified=false`、request count 为 0。
3. 当前请求进程为远端 `curl` PID `15681`，服务 PID `15599`；快照时已执行约 714 秒。服务 CPU 约 `1100%`。
4. 结果应写入 `/data/tomur/smoke/glm47/service/completion-parallel-response.json`，耗时写入 `/data/tomur/smoke/glm47/service/completion-parallel-timing.txt`。快照时 response 尚不存在，timing 文件为 0 bytes。
5. 当前 session 已产生 35 次 expert disk reads、165,867,520 bytes，cache hit/miss/eviction 为 1/35/17。这只能证明 forward 正在执行，不能证明首 token 成功。

服务证据目录为 `/data/tomur/smoke/glm47/service`，已包含 health、version、models、runtime-before、runtime-busy、unload 响应和早期 completion 证据。后续会话必须保留成功与失败两类证据。

## GLM-5.2 下载状态

| 项目 | 值 |
| --- | --- |
| 模型 | `mateogrgic/GLM-5.2-colibri-int4-with-int8-mtp` |
| revision | `3cc8db99b1b13fc79325d987ba3c1c430766b3b8` |
| 固定清单 | 150 files、144 safetensors、145 LFS objects |
| 固定总大小 | 383,760,077,466 bytes |
| 目标目录 | `/data/tomur/data/models/text/glm-5.2-colibri-int4-with-int8-mtp` |
| 下载控制目录 | `/data/tomur/downloads/glm-5.2-colibri-int4-with-int8-mtp` |

快照时目标目录为 314,236,997,407 bytes，包含 113 个完成文件和 12 个 `.part` 文件。该目录大小包含 partial 数据，不能作为完成判据。三个无重叠区间状态为：

| 区间 | 状态文件 | 快照状态 |
| --- | --- | --- |
| 主段 | `status` | `RUNNING`，脚本 PID `4849` |
| tail | `tail.status` | `RUNNING`，脚本 PID `8760` |
| mid-high | `mid-high.status` | `COMPLETE files=14` |

下载器对每个 LFS 对象执行 size 和 SHA-256 校验，只在校验成功后把 `.part` 原子改名为正式文件。快照时主段正在下载 `out-00065` 至 `out-00070`，tail 正在下载 `out-00128` 至 `out-00133`。不要手工改名 `.part`，也不要把状态文件的 `RUNNING` 当作失败。

GLM-5.2 只有在以下条件全部满足后才算下载完成：固定 revision 不变；150 个文件 inventory 完整；144 个 safetensors 和 145 个 LFS 对象齐全；不存在 `.part`；所有 LFS size 与 SHA-256 通过。当前 `/data` 可用空间约 108 GiB，按清单剩余字节名义上足够，但仍应持续监控空间。

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

1. 继续轮询当前 GLM-4.7 请求。只有 response、HTTP 状态、耗时、生成 token 和 session counters 均形成证据后，才判定最短真实 forward 是否通过。
2. 保留当前服务日志、completion 响应、timing 和 runtime/session 快照；失败不得覆盖或删除。
3. 继续下载 GLM-5.2，直到固定 inventory 与全部 checksum 完成。除非下载器异常退出，不要重启正在工作的区间。
4. 下载完成后增加并核对 `model.tomur.json`：provider 为 `managed-glm`、architecture 为 `glm_moe_dsa`、quantization 为 `int4`、layout 为 `packed-offset`。tensor pattern 必须覆盖正式 `out-*.safetensors`，并先确认 MTP shard 与当前 probe 契约。
5. 以 GLM-5.2 执行最终 doctor/readiness、最短真实 forward、兼容 API 和 UI 验证，再形成最终验证报告。未完成前不声明 GLM-5.2 可用。
6. 全部工作结束后再确认是否停止远端服务和本机 SSH 隧道；当前用户仍通过 `http://127.0.0.1:8188` 访问 UI。
