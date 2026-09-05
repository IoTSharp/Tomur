# R20 Realtime 双向语音 smoke 记录

> 当前结论：全部 R20 smoke 项均为 `pending`。本文只是证据入口和验收矩阵，不包含已执行结果，不表示 P0 或 P1 完成。VAD、增量 ASR、增量 TTS、full duplex、barge-in 和 AudioWorklet 尚未接入；gateway、构建或 fake engine 单独通过也不能改写这一结论。

## 当前状态

| 范围 | 状态 | 说明 |
| --- | --- | --- |
| 原生协议 v1 契约 | pending | 协议字段已记录在 [r20-realtime-protocol-v1.md](./r20-realtime-protocol-v1.md)，尚无执行证据。 |
| WebSocket gateway | pending | 尚未记录 upgrade、认证、事件顺序、binary frame、背压或清理 smoke。 |
| Push-to-talk pipeline | pending | `input_audio_buffer.commit` 当前只允许返回 `realtime_pipeline_unavailable`，不得产生伪 transcript/audio。 |
| VAD | pending | Silero sidecar 资产存在不代表 VAD ABI、session 或 speech event 已接入。 |
| 增量 ASR | pending | 现有文件级 Whisper smoke 不证明常驻 session、partial/final 或 native abort。 |
| 增量文本 | pending | 现有 token callback 不证明网络解耦、response epoch 或慢客户端背压。 |
| 增量 TTS | pending | 现有整段 WAV smoke 不证明模型常驻、PCM callback、audio delta 或实时系数。 |
| AudioWorklet 与连续播放 | pending | 固定帧采集、重采样、jitter buffer、漂移和 underrun 尚未验证。 |
| Full duplex 与 barge-in | pending | AEC、误触发、插话召回和停止播放延迟尚无真实设备证据。 |
| Soak、发布与跨平台 | pending | 100 回合、30 分钟、浏览器矩阵、发布产物和 Native AOT 均未执行。 |

已有 [R8 Multimodal Smoke Report](./r8-smoke-report.md) 只证明文件级 ASR 和整段 WAV TTS 的公开接口曾执行成功，其中 ASR 为 `30016 ms`、TTS 为 `9574 ms`。这些结果不是 R20 实时延迟、常驻 session 或全双工证据。

## 冻结测试对象

| 项目 | 值 |
| --- | --- |
| WebSocket path | `/api/realtime/v1` |
| Subprotocol | `tomur.realtime.v1` |
| Ticket endpoint | `POST /api/realtime/tickets` |
| Status endpoint | `GET /api/realtime/status` |
| MVP 暴露范围 | loopback only |
| 输入音频 | PCM16LE、16 kHz、mono、20 ms、640 bytes/frame |
| Binary header | 44 bytes，详见协议文档 |
| 当前 commit 结果 | `realtime_pipeline_unavailable` |

测试不得把凭据放入 URL、文件名、控制台命令历史或普通日志。证据中的 API key、ticket、session token、cookie、Authorization header 和未来 reconnect token 必须写成 `[REDACTED]`。

## 证据目录

未来每次执行使用独立日期与运行 ID 保存原始证据：

```text
docs/r20-smoke-evidence/<yyyy-mm-dd>/<run-id>/
  environment.json
  realtime-status.before.json
  realtime-status.after.json
  handshake-results.json
  auth-results.json
  control-events.ndjson
  binary-frame-results.json
  limit-results.json
  persistence-audit.json
  cleanup-results.json
  legacy-regression.json
  latency-summary.json
  quality-summary.json
  soak-summary.json
  browser-device-matrix.json
  smoke-summary.json
```

不要创建空证据文件或用手写 `passed` 代替原始输出。每个摘要必须能追溯到带时间、输入身份和结果的原始记录。

## 环境证据

每次运行开始前记录：

| 字段 | 要求 |
| --- | --- |
| commit | 精确 Git commit；若工作树有修改，另记 dirty 文件清单 |
| build | Debug/Release、framework-dependent/self-contained/Native AOT 和 RID |
| host | OS、版本、CPU、RAM、GPU/NPU、驱动与电源模式 |
| service | 实际 loopback URL、Tomur 版本、协议版本和启动参数；不含凭据 |
| browser/client | 名称、完整版本、WebSocket 客户端实现 |
| audio device | 麦克风、扬声器/耳机、默认 sample rate、AEC/NS/AGC 可用性 |
| models | ASR、文本、TTS、VAD 的 ID、文件 checksum、量化和 license |
| native runtime | component、variant、library checksum 和 selected accelerator |
| clocks | 客户端/服务端单调时钟来源和 trace ID 关联方式 |
| warmup/sample | 预热次数、样本数、percentile 算法和异常值规则 |

## Gateway 与安全 smoke

以下每项当前均为 `pending`。

| ID | 场景 | 预期结果 | 状态 |
| --- | --- | --- | --- |
| `R20-GW-001` | loopback、正确 path 和 subprotocol upgrade | upgrade 成功；未认证状态不分配 native 资源 | pending |
| `R20-GW-002` | 缺少或错误 subprotocol | upgrade 前拒绝，code 为 `subprotocol_required` | pending |
| `R20-GW-003` | 非 loopback 对端或非 loopback 监听 | upgrade 前拒绝，code 为 `realtime_remote_disabled` | pending |
| `R20-GW-004` | Host 不在允许集合 | upgrade 前拒绝，code 为 `host_not_allowed` | pending |
| `R20-GW-005` | 浏览器 Origin 与服务 origin 不完全一致 | upgrade 前拒绝，code 为 `origin_not_allowed` | pending |
| `R20-AUTH-001` | exact same-origin ticket + 首事件认证 | 5 秒内认证成功，ticket 仅消费一次 | pending |
| `R20-AUTH-002` | 无 Origin + 有效 upgrade Bearer API key | 非浏览器客户端认证成功 | pending |
| `R20-AUTH-003` | 无 Origin、无 Bearer + 一次性 ticket | 允许先 upgrade；5 秒内以首个 `session.authenticate` 事件认证成功 | pending |
| `R20-AUTH-004` | 无 Origin、无 Bearer，且未及时提交 ticket | 5 秒认证截止后关闭，不能因缺 Origin 被视为可信 | pending |
| `R20-AUTH-005` | ticket 过期、伪造或重放 | `authentication_failed`；active session 保持为零 | pending |
| `R20-AUTH-006` | 首事件不是 `session.authenticate` | `authentication_required` 并关闭 | pending |
| `R20-AUTH-007` | 认证事件晚于 5 秒 | 有界关闭，不创建 session/native handle | pending |
| `R20-AUTH-008` | 任一 R20 v1 固定路由携带 query，或 path 携带凭据 | 任意 query 均以 `credential_in_query_forbidden` 拒绝；可疑 path 不记录敏感值 | pending |
| `R20-AUTH-009` | ticket 响应和服务日志审计 | `Cache-Control: no-store`；凭据、token 全部脱敏 | pending |
| `R20-AUTH-010` | 无 Origin 客户端提交无效或格式错误的 Bearer API key | upgrade 或 ticket 请求以 `invalid_api_key` 拒绝；不得混用 ticket 的 `authentication_failed` | pending |
| `R20-QUOTA-001` | 第 9 个全局 pending connection | upgrade 前拒绝；既有 8 个不受影响 | pending |
| `R20-QUOTA-002` | 同一来源第 3 个 pending connection | upgrade 前拒绝；其他来源配额独立 | pending |
| `R20-QUOTA-003` | 第 2 个 active session | `session_busy`，不排队且不分配 native 资源 | pending |
| `R20-QUOTA-004` | ticket store 全局达到 128 | 先清过期项；仍满时以 `ticket_capacity_exceeded` 拒绝签发且不覆盖有效 ticket | pending |
| `R20-QUOTA-005` | 同一来源存在 16 个未过期 ticket | 以 `ticket_source_limit_reached` 拒绝第 17 个 ticket；其他来源仍可签发 | pending |

## 控制事件 smoke

| ID | 场景 | 预期结果 | 状态 |
| --- | --- | --- | --- |
| `R20-CTL-001` | 客户端首 sequence 为 `1` 并连续递增 | 事件按序接受；服务端 sequence 独立从 `1` 开始 | pending |
| `R20-CTL-002` | duplicate、gap 或回退 sequence | `control_sequence_mismatch`；不得继续处理该事件 | pending |
| `R20-CTL-003` | event_id 重复 | `duplicate_event_id`；不得重复执行动作 | pending |
| `R20-CTL-004` | `session.update` 使用固定输入格式 | 返回 `session.updated` 与实际采用配置 | pending |
| `R20-CTL-005` | `session.ping` | 2 秒 send deadline 内返回 `session.pong`，其 `client_event_id` 等于 ping 的 `event_id` | pending |
| `R20-CTL-006` | `session.close` 的 reason 省略、为 allowlist 值或为任意其他文本 | 返回 `session.closed`；只保留 `client_closed`、`user_requested`、`page_unload`，其余统一为 `client_closed`，2 秒内释放连接资源 | pending |
| `R20-CTL-007` | 未知或未来事件 | `unsupported_event`，不能静默忽略 | pending |
| `R20-CTL-008` | 不存在 active response 的 `response.cancel` | 幂等 `response.cancelled`，`reason=not_active`，不产生内容 | pending |
| `R20-CTL-009` | 不存在或不活跃 response 的 displayed/consumed ack | 可恢复的 `response_not_active` | pending |
| `R20-CTL-010` | JSON 恰好 16 KiB 与超过 16 KiB | 边界值行为一致；超限为 `message_too_large` | pending |
| `R20-CTL-011` | 32 与 33 fragments | 32 可重组；33 为 `fragment_limit_exceeded` | pending |
| `R20-CTL-012` | 无效 UTF-8、空 JSON 或错误字段类型 | `invalid_event`，服务不崩溃 | pending |

## Binary frame 与输入缓冲 smoke

| ID | 场景 | 预期结果 | 状态 |
| --- | --- | --- | --- |
| `R20-BIN-001` | 44-byte header + 640-byte PCM payload | kind `1` frame 接受，总 message 为 684 bytes | pending |
| `R20-BIN-002` | ASCII `TMR1`、version `1`、flags `0` | header 按 little-endian 标量和 RFC 4122 ID 解码 | pending |
| `R20-BIN-003` | 使用已知 UUID 的 RFC 4122 canonical bytes | 解码为预期 ID；不得使用 mixed-endian `Guid.ToByteArray()` 作为 wire 格式 | pending |
| `R20-BIN-004` | magic/version/kind/flags 任一错误 | 返回对应 `binary_magic_mismatch`、`binary_version_mismatch`、`binary_kind_unsupported` 或 `binary_flags_unsupported` | pending |
| `R20-BIN-005` | payload length 与实际长度不一致或声明过大 | `binary_length_mismatch` 或 `binary_payload_too_large` | pending |
| `R20-BIN-006` | 输入 payload 为 638、639、641 或 642 bytes | `input_audio_frame_size_invalid`；只接受固定 640 bytes | pending |
| `R20-BIN-007` | capture sequence 从 `1` 连续增长 | 首帧发送 `input_audio_buffer.started` | pending |
| `R20-BIN-008` | capture sequence duplicate/gap/回退 | `audio_sequence_mismatch`，不静默补帧 | pending |
| `R20-BIN-009` | 当前 capture 已 commit/clear 后，同一连接切换新 capture_stream_id | 新 stream sequence 从 `1` 开始，旧缓冲不串入；未先 commit/clear 的直接切换返回 `capture_stream_changed` 并关闭 | pending |
| `R20-BIN-010` | 30 秒、1,500 帧、960,000 payload bytes | 精确边界可接受 | pending |
| `R20-BIN-011` | 超过 30 秒或 960,000 bytes | `input_audio_buffer_overflow`，缓冲不再增长 | pending |
| `R20-BIN-012` | `input_audio_buffer.clear` 省略/null/空白 capture_stream_id | 清理当前缓冲，返回 cleared 计数，内存缓冲归零 | pending |
| `R20-BIN-013` | 有效 `input_audio_buffer.commit` | 先返回 `buffered_audio_bytes`/`duration_ms`，再返回非终止 `realtime_pipeline_unavailable` | pending |
| `R20-BIN-014` | commit 后检查输出 | 无 transcript、assistant text、kind `2` frame 或静音占位音频 | pending |
| `R20-BIN-015` | commit/clear 后检查本地状态 | PCM 不写文件、不进 SQLite、不进普通日志/trace | pending |
| `R20-BIN-016` | `input_audio_buffer.clear` 提供 capture_stream_id | 匹配当前流时清理；无效或不匹配时返回可恢复的 `capture_stream_mismatch` 且保留缓冲 | pending |

持久化审计至少检查 Tomur 数据目录新增文件、conversation message/artifact/diagnostic 计数、SQLite 相关表和捕获日志。审计只比较本次运行前后变化，不删除无法确认归属的文件。

## 限制、背压与清理 smoke

| ID | 场景 | 预期结果 | 状态 |
| --- | --- | --- | --- |
| `R20-LIM-001` | 100 和 101 events/秒 | 100 在边界内；101 触发 `event_rate_exceeded` | pending |
| `R20-LIM-002` | 第 50,000 与 50,001 个 session event | 后者触发 `session_event_limit_exceeded` | pending |
| `R20-LIM-003` | inbound queue 64/65 items | 超限为 `input_queue_overflow`，不静默丢弃 | pending |
| `R20-LIM-004` | outbound control queue 64/65 items | 使用有界等待且不使用 `DropOldest`；超过 2 秒 send deadline 后关闭 | pending |
| `R20-LIM-005` | 客户端停止读取 | 单次 send 2 秒截止，随后取消并清理 | pending |
| `R20-LIM-006` | 30 秒无合法活动 | `session_idle_timeout`，以 `4008` 关闭并释放资源 | pending |
| `R20-LIM-007` | session 达到 15 分钟 | `session_duration_exceeded`，以 `4008` 有界终止且不接受后续 frame | pending |
| `R20-LIM-008` | close peer 不响应或持续发送在途数据 | 2 秒或最多 32 次 receive 任一边界先到后中止 close 等待并释放本地资源 | pending |
| `R20-LIM-009` | 页面卸载、网络断开和进程停止 | queue、CTS、task、socket 和 session lease 全部回收 | pending |
| `R20-LIM-010` | 未来客户端连续自动重试策略 | 尚未接入；接入后最多 3 次并使用有界 backoff，不恢复旧 PCM、response 或副作用；当前 gateway 不跨连接计数 | pending |
| `R20-LIM-011` | 多项超限并发发生 | 只完成一次关闭，计数不为负且 active lease 可再次获取 | pending |
| `R20-LIM-012` | 正常、认证、协议、策略、busy、timeout 与 overflow 关闭 | WebSocket code 分别为 `1000`、`4001`、`4002`、`4003`、`4004`、`4008`、`4009`，并与结构化 `error.code` 一致 | pending |

每个用例必须有测试自身的墙钟上限。执行结束后记录 pending/active count、仍存活的 session-owned tasks、channel item count、CTS、socket 和 native handle；仅观察进程总体存活不能证明清理完成。

## 后续语音质量 smoke

以下用例在对应能力接入前不可执行，状态仍统一为 `pending`，不得用 fake engine 或批处理接口改成 passed。

| ID | 能力 | 通过门槛 | 状态 |
| --- | --- | --- | --- |
| `R20-VAD-001` | speech start | 不少于 100 个标注 turn，p95 `<= 200 ms` | pending |
| `R20-VAD-002` | speech stop | 用户停止到 VAD stop p95 `<= 700 ms` | pending |
| `R20-VAD-003` | VAD 质量 | precision/recall 均 `>= 95%`，非语音误触发 `<= 0.2 次/分钟` | pending |
| `R20-ASR-001` | final transcript | speech stop 到 final p95 `<= 800 ms` | pending |
| `R20-ASR-002` | meaningful partial | 对应语音片段采集完成起 p95 `<= 1.0 s`；后续更新间隔 p95 `<= 500 ms` | pending |
| `R20-ASR-003` | 中文/英文准确率 | CER/WER 均 `<= 15%`，相对同模型批处理绝对退化 `<= 2` 个百分点 | pending |
| `R20-TTS-001` | 首个可听音频 | 用户停止说话起 p95 `<= 2.5 s`；优化目标 `<= 1.5 s` | pending |
| `R20-TTS-002` | warm 实时系数 | audible duration 定义下 warm RTF p95 `< 0.8` | pending |
| `R20-TTS-003` | 连续播放 | 无未解释持续 underrun、重复、乱序或拼接爆音 | pending |
| `R20-BARGE-001` | 有效插话 | 停止本地播放 p95 `<= 250 ms`，recall `>= 95%` | pending |
| `R20-BARGE-002` | echo-only | false interruption `<= 0.1 次/分钟` | pending |
| `R20-DSP-001` | 输出重采样 | 24 kHz 到 44.1/48 kHz 时长、连续性、漂移和有界分配通过 | pending |
| `R20-SOAK-001` | 重复交互 | 不少于 100 回合，结束后 session-owned task/handle 为零 | pending |
| `R20-SOAK-002` | 连续通话 | 不少于 30 分钟，资源增长落在 P0 冻结容差内 | pending |

延迟指标必须使用客户端采集时间、服务端单调时钟、统一 trace ID 和预先冻结的 percentile 算法。静音 PCM、占位事件、重复 partial 或不同时间基准不能用于满足门槛。浏览器 `playback_consumed` 只表示数据已进入渲染时间线，物理可听延迟必须通过音频回环设备或专用仪表单独测量。

## 浏览器与设备矩阵

所有组合当前均为 `pending`。

| 平台 | 浏览器 | 版本范围 | 设备模式 | 状态 |
| --- | --- | --- | --- | --- |
| Windows | Edge、Chrome | 当前及上一主版本 | 扬声器、耳机、AEC 降级 | pending |
| macOS | Safari、Chrome | 当前及上一主版本 | 扬声器、耳机、AEC 降级 | pending |
| Linux | Firefox、Chrome | 当前及上一主版本 | 扬声器、耳机、AEC 降级 | pending |

每个组合分别验证麦克风权限拒绝、AudioContext 用户激活、自动播放限制、设备热插拔、AudioWorklet 可用性、输入 sample rate、AEC/NS/AGC、页面隐藏/卸载、fallback 和资源清理。

## 兼容回归

R20 变更落地后必须重新执行以下回归。R8 历史证据不能替代本次回归，因此当前状态均为 `pending`。

| 接口/行为 | 预期 | 状态 |
| --- | --- | --- |
| `POST /v1/audio/transcriptions` | 文件级 ASR 行为与错误形状保持兼容 | pending |
| `POST /v1/audio/speech` | 整段 WAV TTS 行为与错误形状保持兼容 | pending |
| `POST /api/conversations/{conversationId}/voice-turns` | 按钮式录音 fallback 保持兼容 | pending |
| 文本 Chat 与 streaming | 普通请求不被 Realtime lease 永久阻塞 | pending |
| model load/unload | 与 active Realtime session 的优先级、取消和诊断稳定 | pending |
| runtime repair | 不与 session 清理形成死锁或泄漏 | pending |
| conversation history | partial/PCM 不持久化，final/ack 边界按协议持久化 | pending |
| tool safety | allowlist、参数校验、幂等键和副作用确认不被语音绕过 | pending |

## 发布证据

| 范围 | 必须记录 | 状态 |
| --- | --- | --- |
| 专项测试 | 协议、状态机、安全、binary codec、背压、竞态和清理 | pending |
| 完整 solution | 测试数、通过数、失败数、warning/error | pending |
| Web build | AudioWorklet/DSP 产物、source map 和静态托管 | pending |
| 非 AOT 自包含 | RID、包结构、WebSocket 与真实模型 smoke | pending |
| Native AOT | RID、发布 warning、启动和协议 smoke | pending |
| Native bundle | Whisper/VAD/TTS/ggml 版本、checksum、共享库兼容 | pending |
| License | Whisper、Silero、TTS、WavTokenizer、speaker 与 DSP 依赖的再分发/商业使用结论 | pending |

当前默认 TTS catalog 模型标记为 `CC-BY-NC-4.0`。在目标发布场景的许可审查完成前，不能仅凭技术 smoke 把它判定为可发布的 R20 默认 TTS。

## 结果判定

每个用例只允许 `pending`、`passed`、`failed` 或 `blocked`，并必须附原始证据路径。`blocked` 必须记录外部条件与下一步；没有执行记录时只能是 `pending`。

以下状态必须分别报告：

- protocol contract recorded；
- gateway available；
- model assets visible；
- session warm；
- fake pipeline tests passed；
- real model inference passed；
- latency target passed；
- full duplex device smoke passed；
- soak passed；
- cross-platform release passed。

任一较低层状态都不能替代较高层结论。只有真实设备、真实模型、量化质量/延迟、资源清理、兼容回归、soak 和发布矩阵全部形成证据后，才可以评估 R20 完成状态；本文当前不支持宣称 P0、P1 或 R20 完成。
