# R20 Realtime 原生协议 v1

> 本文记录 R20 原生网关与语音管线的代码契约，不构成构建、真实设备或性能证据。常驻语音、流式文本/短句音频、AudioWorklet、取消与确认后历史已接入；默认半双工，双向模式为 AEC 必需的实验选项。OpenAI Realtime 风格适配仍未实现，所有 smoke 状态保持 pending。

## 协议概览

R20 v1 是 Tomur 的本地优先原生 Realtime 协议。有效配置后执行 Silero VAD、Whisper、文本模型和 TTS 的本地级联链路。输入 PCM 和 partial 只驻留内存，final transcript 与客户端确认的助手内容进入现有 conversation store。

| 项目 | 冻结值 |
| --- | --- |
| WebSocket path | `/api/realtime/v1` |
| WebSocket subprotocol | `tomur.realtime.v1` |
| Ticket endpoint | `POST /api/realtime/tickets` |
| Status endpoint | `GET /api/realtime/status` |
| 控制消息 | UTF-8 JSON text message |
| 音频消息 | 固定 44-byte header 加 PCM payload 的 binary message |
| MVP 网络边界 | 仅 loopback |
| 输入音频 | signed PCM16 little-endian、16 kHz、mono、20 ms/frame |
| 输入 payload | 每帧固定 640 bytes |
| 当前处理模式 | 手动提交或 server VAD；默认半双工，实验性双向要求 AEC |

客户端必须只请求 `tomur.realtime.v1`。服务端不得在缺少、拼写错误或同时包含未支持版本的 subprotocol 时静默升级，也不得协商到其他协议。

## 建立安全连接

### 浏览器客户端

浏览器工作台必须从与 WebSocket 完全相同的 origin 发起连接。这里的 exact same-origin 指 scheme、host 和显式或默认 port 全部一致；CORS 不是 WebSocket 认证机制，不能替代 Host 和 Origin 校验。

Origin 用于约束浏览器请求上下文，不是本机进程身份凭据；能够直接构造 HTTP 请求的 loopback 本机进程位于当前 MVP 的本机信任边界内。跨本机身份隔离与远程客户端认证不属于此切片。

1. 浏览器通过同源 `POST /api/realtime/tickets` 获取短期一次性 ticket。
2. 浏览器连接同源 `/api/realtime/v1`，并请求 `tomur.realtime.v1` subprotocol。
3. 服务端在 upgrade 前验证 loopback、Host、Origin、subprotocol 和 pending connection 配额。
4. upgrade 后的第一个客户端控制事件必须是 `session.authenticate`，并携带一次性 ticket。
5. 该事件必须在 upgrade 后 5 秒内到达，且客户端控制 sequence 必须为 `1`。
6. ticket 成功消费且 active session 配额可用后，服务端发送 `session.created`。

认证完成前只允许接收 `session.authenticate` 或终止连接。服务端不得在此阶段加载模型、创建 native context、分配音频 session、创建 conversation item，或把连接计入 active Realtime session。

### 非浏览器客户端

缺少 `Origin` 的连接按非浏览器客户端处理，可在 upgrade 请求中发送 `Authorization: Bearer <Tomur API key>` 完成预认证。未携带 Bearer API key 的非浏览器连接可以先 upgrade，但 upgrade 后的第一个控制事件必须在 5 秒内通过 `session.authenticate` 提交一次性 ticket，否则服务端关闭连接。

一次性 ticket 无论由浏览器还是非浏览器使用，都只能放在 upgrade 后的首个 `session.authenticate` JSON 事件中，不接受 ticket upgrade header。缺少 `Origin` 绝不等价于可信客户端。

没有 `Origin` 的 `POST /api/realtime/tickets` 请求必须携带有效 Tomur Bearer API key。带 `Origin` 的 ticket 请求必须通过 exact same-origin 校验。

### 凭据处理

API key、ticket、session token 和未来 reconnect token 都不得出现在 URL path、query string、日志、diagnostic message、trace attribute 或 close reason 中。Ticket 响应必须使用 `Cache-Control: no-store`，ticket 只返回一次，并在成功消费、过期或容量回收后不可再次使用。

Ticket 的 TTL 为 30 秒，内存容量为全局 128、每来源 16。签发前先回收过期 ticket；任一容量仍满时拒绝签发，不得覆盖尚未过期且未消费的 ticket。ticket 不预留 active session 名额。

### Loopback MVP

首个版本只允许 loopback 监听与 loopback 对端。以下任一条件不满足时都必须在分配 Realtime/native 资源之前拒绝：

- 实际远端地址是 IPv4 或 IPv6 loopback。
- 实际本地 socket 地址和 Host 都属于 loopback；Host 保留客户端可见端口，以兼容同机开发代理。
- 浏览器请求的 Origin 与客户端所见服务 origin 完全一致。
- 不信任未经明确配置的 forwarded headers 来把非 loopback 请求解释为 loopback。

绑定 `0.0.0.0`、`::`、局域网地址或公网地址不使 Realtime 自动可用。远程鉴权、TLS、代理信任链和跨设备媒体质量不属于此 MVP。

## HTTP 辅助端点

### POST /api/realtime/tickets

签发短期一次性 Realtime ticket。R20 v1 的三个固定路由都不定义 query 参数，因此任何 query string 均被拒绝；请求也不接受调用方指定 TTL。

成功响应至少包含：

```json
{
  "ticket": "sensitive-one-time-value",
  "expires_at": "2026-09-05T12:00:30Z",
  "expires_in_seconds": 30,
  "protocol": "tomur.realtime.v1",
  "web_socket_path": "/api/realtime/v1",
  "authenticate_event_type": "session.authenticate"
}
```

`ticket` 是敏感值。示例只说明形状，不是有效凭据。

| HTTP 状态 | 语义 |
| --- | --- |
| `200` | ticket 已签发 |
| `400` | 请求携带了 v1 未定义的 URL query 参数 |
| `401` | 无 Origin 的客户端缺少或提交了无效 Bearer API key |
| `403` | 非 loopback、Host 不允许或浏览器 Origin 不匹配 |
| `429` | 每来源或全局 ticket 内存容量已满 |
| `503` | 本地凭据存储不可用或安全 ticket 无法生成 |

### GET /api/realtime/status

返回 gateway 与 pipeline 的分离状态。响应必须区分以下事实，不得把其中任一项概括成完整 Realtime 可用：

- gateway 是否已注册并可接受 loopback 连接；
- 当前协议版本；
- pipeline 是否已连接；
- 当前 pending/active session 数及上限；
- VAD、ASR warm session、TTS warm session 和 full duplex 是否可用；
- 当前 status DTO 对外发布的资源限制。

Status 分别报告 `model_ready`、`session_loaded` 和本进程真实执行后的 `warm_executed`；`full_duplex` 固定保持 `degraded_unverified`，`smoke` 固定保持 `pending`。`diagnostics` 提供输入缓冲字节、待播放毫秒、turn 数、最近错误、final ASR 执行时间和从提交到首个 PCM 的服务端时间。后两项不是跨客户端时钟的端到端指标，也不证明首个样本可听。响应不得包含凭据、音频或 transcript。

内置浏览器客户端对异常断线最多重连三次，backoff 为 1.5、3、4.5 秒，且不延长客户端 15 分钟总截止；只恢复配置与已提交 conversation，不重放 PCM 或旧 response。协议、认证和配额拒绝不自动重试。`limits.graceful_close_timeout_milliseconds = 2000` 为 transport close 截止，不是强行释放仍在 native 栈上的句柄的期限。

## 控制事件

### 公共 envelope

每个 JSON 控制事件都必须包含以下字段：

```json
{
  "type": "session.ping",
  "event_id": "client-event-0001",
  "sequence": 1,
  "timestamp_us": 1245000
}
```

| 字段 | 类型 | 规则 |
| --- | --- | --- |
| `type` | string | 当前事件矩阵中的精确事件名；区分大小写 |
| `event_id` | string | 当前连接方向内唯一；长度 1–64，只允许 ASCII 字母、数字、`.`、`_`、`-`，不得含凭据 |
| `sequence` | signed 64-bit integer | 取值 `1..Int64.MaxValue`；客户端控制 sequence 从 `1` 开始，逐事件严格加一，不允许 gap、重复或回退 |
| `timestamp_us` | signed 64-bit integer | 发送端单调时钟的微秒值，不是 UTC epoch，不与另一端直接相减 |

客户端和服务端控制 sequence 分属两个独立命名空间。服务端 sequence 同样从 `1` 开始严格递增。重连会创建新连接并把两个控制 sequence 重置为 `1`；首个切片不恢复旧连接 sequence 或未提交状态。

JSON WebSocket message 可以由多个 WebSocket fragment 组成，但服务端必须先在有界缓冲中完整重组，再做一次 UTF-8 与 JSON 解析。一个 JSON message 最多 16 KiB、最多 32 个 fragments。

### 客户端事件矩阵

| 事件 | 允许阶段 | 语义 |
| --- | --- | --- |
| `session.authenticate` | 未认证，且只能是首事件 | 提交 `ticket`；upgrade 已通过 Bearer 认证时再次发送返回可恢复的 `already_authenticated` |
| `session.update` | 已认证且未配置 | 校验模型与资源，加载语音 session；已配置时返回 `session_already_configured`，变更配置须重连 |
| `session.ping` | 已认证 | 服务端返回 `session.pong`，并用 `client_event_id` 关联本事件的 `event_id` |
| `session.close` | 已认证 | 请求正常关闭；可选 `reason` 只接受 `client_closed`、`user_requested`、`page_unload`，省略或其他值统一归一化为 `client_closed` |
| `input_audio_buffer.commit` | 已认证 | 提交指定 `capture_stream_id` 的当前输入缓冲 |
| `input_audio_buffer.clear` | 已认证 | 省略、设为 `null` 或空白 `capture_stream_id` 时丢弃当前输入缓冲；提供 ID 时必须与当前输入流匹配 |
| `response.cancel` | 已认证 | 取消指定 `response_epoch`；没有 active response 时按幂等取消返回 |
| `response.text.displayed` | 已认证 | 确认指定 response/item 的文本已展示；不是工具副作用确认 |
| `response.audio.playback_consumed` | 已认证 | 确认输出 sequence 已进入客户端音频渲染时间线；不证明物理扬声器已发声 |

未知事件和为未来协议预留但尚未接入的事件必须返回 `unsupported_event`，不得静默忽略。

`session.close.reason` 是协议枚举，不是任意客户端文本。服务端不得回显 allowlist 之外的输入，从而避免把凭据、用户内容或其他敏感值带入 `session.closed`、证据文件或诊断链路。

### 服务端事件矩阵

| 事件 | 触发条件 | 语义 |
| --- | --- | --- |
| `session.created` | 认证与 active 配额获取成功 | 返回 session ID、协议、冻结限制和当前能力状态 |
| `session.updated` | 有效 `session.update` | 返回服务端实际采用的配置 |
| `session.pong` | 有效 `session.ping` | `client_event_id` 等于对应 ping 的 `event_id`，并带服务端单调时间 |
| `session.closed` | 已认证客户端发送有效 `session.close` | 返回归一化后的 allowlist 关闭原因、最终状态和当前连接累计计数；收到 WebSocket close 帧时直接完成 close handshake，不再发送应用数据 |
| `input_audio_buffer.started` | capture stream 的首个有效输入 frame | 返回 `capture_stream_id` 和 `first_sequence`；binary sequence 仍以输入帧头为准 |
| `input_audio_buffer.committed` | 有效 commit | 返回 `buffered_audio_bytes` 和 `duration_ms` |
| `input_audio_buffer.cleared` | 有效 clear | 返回已清理的 `capture_stream_id`、原因、丢弃 frame 数和 payload byte 数 |
| `response.cancelled` | 有效取消或确认插话 | 返回旧 `response_epoch`，立即清除客户端尚未消费的音频；`status` 区分 `client_requested` 与 `barge_in` |
| `error` | 非终止或终止错误 | 返回稳定 code、非敏感 message、`fatal` 和必要的作用域标识 |

`response.text.displayed` 与 `response.audio.playback_consumed` 不要求额外成功事件。已结束的旧 epoch 确认不会进入新 response；当前 epoch 的越界、回退或不匹配确认返回 `invalid_display_ack` / `invalid_playback_ack`。

### 配置与流式事件

`session.created` 后发送 `session.update`，并等待 `session.updated` 后采集音频。完整配置示例中的模型与 conversation 必须替换为本地有效 ID：

```json
{
  "type": "session.update",
  "event_id": "configure-1",
  "sequence": 2,
  "timestamp_us": 1000,
  "session": {
    "conversation_id": "local-conversation-id",
    "model": "local-chat-model",
    "asr_model": "whisper-large-v3-turbo-q5",
    "tts_model": "outetts-0.2-500m-q4km",
    "language": null,
    "turn_detection": "server_vad",
    "mode": "half_duplex",
    "echo_cancellation": true,
    "input_audio_format": "pcm16le",
    "input_sample_rate": 16000,
    "input_channels": 1,
    "input_frame_duration_ms": 20,
    "output_audio_format": "pcm16le",
    "output_sample_rate": 24000,
    "output_channels": 1
  }
}
```

`turn_detection` 支持 `manual`、`server_vad`；`mode` 支持 `half_duplex` 和显式 `duplex_experimental`。实验模式要求 `echo_cancellation=true`，浏览器仅在实际 track settings 报告 AEC 时允许选择成功；声明本身不构成回声质量证据。半双工在回复执行或尚有待消费输出时不启动新的输入 utterance，用户可主动取消回复后继续说话。

| 事件 | 内容与边界 |
| --- | --- |
| `session.ready` | 语音模型驻留；`status=models_resident_degraded_unverified`，不是 warm 或性能通过 |
| `session.state` | 当前 listening/user_speaking/transcribing/thinking/speaking 等状态 |
| `input_audio_buffer.speech_started` | 服务端 VAD 确认开始；包含 utterance 与 capture stream，客户端清除旧输出 |
| `input_audio_buffer.speech_stopped` | endpoint 或手动提交已封存输入 |
| `input_audio_transcription.delta` | 同一 utterance 的临时完整窗口 `text`，替换上一次 partial，不能简单追加 |
| `input_audio_transcription.done` | 唯一 final `text`，对应已保存的用户回合 |
| `response.created` | final ASR 开始前建立 `response_epoch`、`response_id`、`item_id` 映射，允许在转写期间取消；输出 sequence 从 1 开始 |
| `response.text.delta` | 真实 token `delta`，`character_count` 为累计 UTF-16 code unit 数 |
| `response.text.done` | 文本生成结束，不表示音频播放结束 |
| kind `2` binary | 当前 response 的真实短句 PCM，不附加固定静音 |
| `response.audio.done` | 合成/发送结束，播放仍可能有待消费队列 |
| `response.done` | 生成完成且全部输出音频已被客户端确认消费，随后恢复 listening 并刷新会话历史 |

Whisper partial 每新增一秒音频且前次处理已完成时尝试一次，窗口最多四秒，部分取消不产生 final。VAD 为 512-sample recurrent 窗口，阈值为 0.6/0.35，连续 100 ms 高概率开始、600 ms 低概率结束，pre-roll 300 ms。短句聚合上限 160 个 UTF-16 code unit，标点切分最短 12，750 ms 等待后允许提前 flush；不切断 surrogate pair。

## Binary audio frame

每个 binary WebSocket message 由 44-byte header 和 payload 组成。所有整数按 little-endian 编码；唯一例外是 16-byte ID 必须使用 RFC 4122 canonical byte order，不能直接依赖具有 mixed-endian 行为的 `Guid.ToByteArray()`。

| Offset | Size | 字段 | 规则 |
| ---: | ---: | --- | --- |
| `0..3` | 4 | magic | ASCII `TMR1` |
| `4` | 1 | version | 固定 `1` |
| `5` | 1 | kind | `1` = input PCM；`2` = output PCM |
| `6..7` | 2 | flags | v1 固定 `0`，非零即拒绝 |
| `8..23` | 16 | id | RFC 4122 bytes；input 为 `capture_stream_id`，output 为 `response_id` |
| `24..31` | 8 | sequence | little-endian unsigned wire 字段；v1 接受 `1..Int64.MaxValue`，并在当前 ID 内从 `1` 开始严格递增 |
| `32..39` | 8 | timestamp_us | signed 64-bit monotonic capture/playback timestamp |
| `40..43` | 4 | payload length | payload bytes，不含 44-byte header |

接收端重组完整 WebSocket message 后，必须验证实际长度严格等于 `44 + payload length`。magic、version、kind、flags、ID、sequence、timestamp、payload length 或格式任一不合法时，返回结构化错误并按错误级别 reset、cancel 或 close。

### Input PCM

kind `1` 的输入格式固定为 PCM16LE、16 kHz、mono、20 ms。每帧包含 320 个 signed 16-bit samples，因此 payload 必须恰好为 640 bytes，完整 binary message 必须恰好为 684 bytes。

每个新 `capture_stream_id` 的 binary sequence 从 `1` 开始。连续采集的 capture stream 跨 VAD endpoint 和 commit 保持不变，只有显式 clear 或重连才重置 ID/sequence。duplicate、gap、回退或跨 capture stream 复用 sequence 都不能静默接受。输入 timestamp 表示该帧首个 sample 的客户端单调采集时间，不代表 UTC，也不继承 response epoch。

当前切片对单个 utterance 最多接收 30 秒或 960,000 payload bytes，即最多 1,500 个标准输入 frame。达到任一上限后，不再接受额外 PCM，必须产生稳定诊断并清理或关闭，不得继续增长缓冲。

### Output PCM

kind `2` 为 24 kHz mono PCM16 输出，payload 为 2..4800 个偶数字节，ID 是 `response_id`，sequence 在每个 response 内从 `1` 开始。timestamp 从 0 开始，下一帧必须接续上帧 payload 的样本时长。浏览器将其重采样至实际 AudioContext 采样率；播放 underrun 显式展示，溢出终止。固定静音不能用来表示 pipeline 可用或满足首音频延迟。

## Commit、clear 与持久化

`input_audio_buffer.commit` 必须引用当前有效的 `capture_stream_id`。有效 commit 的行为是：

1. 封存当前 utterance 并清空其内存缓冲，保留独立 capture stream 序号。
2. 响应任务先取得音频快照与旧取消源的清理责任，发送 `input_audio_buffer.committed`、speech stopped 和 `response.created`；等待旧 native 工作退出期间也允许客户端取消，事件发送失败同样进入清理路径。
3. 等待已取消的 partial/旧 response 退出后，执行唯一 final ASR，空结果返回 `transcript_empty`。输入事件保留提交时的 capture ID，不因后续 clear 或新流而改变。
4. 保存用户 final，读取最多 24 条已提交历史，经有界 token/短句队列合成音频；不执行模型声明的工具。
5. PCM 不进入文件、SQLite 或日志。`displayed` 只能确认已经开始发送的 UTF-16 前缀；`playback_consumed` 必须对应已发送 sequence 的准确结束 timestamp。完整短句最后一帧被消费后，该短句的源文本才可作为 played 前缀。两类确认取最大前缀，递增更新同一个 assistant item；取消记录 interrupted，未确认尾部不进入下一轮上下文。

`input_audio_buffer.clear` 省略、设为 `null` 或空白 `capture_stream_id` 时立即丢弃当前缓冲；提供 ID 时，该 ID 必须是当前有效的 `capture_stream_id`，否则返回可恢复的 `capture_stream_mismatch` 且不清理缓冲。成功后发送 `input_audio_buffer.cleared`。clear 是内存生命周期操作，不表示撤销已经提交的外部副作用。

## 状态与顺序

完整 R20 状态集合预留为：

```text
connecting -> listening -> user_speaking -> transcribing -> thinking -> speaking
```

并包含 `interrupted`、客户端 `reconnecting`、`failed` 和 `closed`。正常回合完成后回到 listening，实验性双向允许输出期间建立新的 utterance：

```text
speaking -> interrupted -> listening -> user_speaking -> transcribing
```

server VAD 模式的 user_speaking 为模型检测结论；manual 模式为客户端明确采集开始。插话先取消旧 epoch，旧 token/PCM 不得越过发送栅栏；最多保留一个正在退出的 native response，下一回合等待其退出后才使用相同句柄。持续取消不能形成无界任务链。

每次重连建立新 session、重新认证并从 sequence `1` 开始，绑定同一 conversation。只使用已经持久化的历史，不恢复旧 PCM、旧 response 或未确认 acknowledgement。

## 资源限制与 overflow

| 限制 | 冻结值 | 超限行为 |
| --- | ---: | --- |
| 认证截止时间 | 5 秒 | 认证失败并关闭 |
| 空闲超时 | 30 秒 | 正常清理并关闭 |
| session 总时长 | 15 分钟 | 发送终止事件并关闭 |
| 单次 send 截止时间 | 2 秒 | 取消发送并关闭慢客户端 |
| graceful close | 2 秒且最多 32 次 receive | 任一边界先到即中止 close 等待并释放资源 |
| JSON message | 16 KiB | `message_too_large`，关闭 |
| WebSocket fragments/message | 32 | `fragment_limit_exceeded`，关闭 |
| inbound queue | 64 items | `input_queue_overflow`，取消并关闭 |
| outbound control queue | 64 items | 有界等待，超过 2 秒 send deadline 后取消并关闭 |
| 单 utterance | 30 秒 / 960,000 payload bytes | `input_audio_buffer_overflow`，清理当前缓冲并关闭 |
| 客户端事件速率 | 100 events/秒 | `event_rate_exceeded`，关闭 |
| 每 session 客户端事件总数 | 50,000 | `session_event_limit_exceeded`，关闭 |
| pending connections | 全局 8 / 每来源 2 | upgrade 前拒绝 |
| active Realtime sessions | 1 | `session_busy`，不排队 |
| ticket | TTL 30 秒 / 全局 128 / 每来源 16 | 过期清理；任一容量满则拒绝签发 |
| 客户端自动重试 | 最多 3 次，1.5/3/4.5 秒 backoff | 仅异常断线；协议或配额失败直接显示诊断 |
| native 语音 load | 60 秒 | 协作取消；释放已创建句柄 |
| partial / final ASR | 4 / 30 秒 | partial 取消；final 超限诊断 |
| 单短句 TTS | 30 秒、30 秒音频 | abort/cancel，不产生占位音频 |
| response / turns | 120 秒 / 100 回合 | 取消或结束 session |
| token / 短句队列 | 256 / 8 items | 明确 overflow 并取消 response |
| response 文本 / 短句总数 | 8192 UTF-16 code units / 128 段 | 超限诊断，不静默丢弃尾部 |
| native PCM 队列 | 320 chunks，每 chunk 最大 4800 bytes | callback 返回取消，不阻塞 native 等待网络 |
| 客户端发送缓存 | 128 KiB | 停止采集并关闭 |
| 已发送未消费音频 | 2 秒，最多等待 5 秒 | `playback_backpressure`，取消 response |
| 浏览器播放环形缓冲 | 3 秒、32 个 chunk 边界 | overflow 关闭；不足时显式 underrun 并重新缓冲 |

事件计数同时包含完整 JSON 控制 message 和完整 binary frame，避免通过 binary 流绕过速率与 session 总量限制。合法 ping 和 PCM 会刷新 idle deadline；未完成 fragment、无效消息和被拒绝的凭据不会无限延长连接寿命。

所有 channel 都有固定容量，控制事件不使用静默 DropOldest。资源预留在普通模型执行锁之前取得；Realtime 与普通 Chat/ASR/TTS/OCR/图像推理不能并发获取模型。unload 与 native repair 发起取消，在 session-owned 工作和句柄释放前返回 `realtime_resource_busy`/409，调用方在释放后重试。语音路径固定使用 CPU 参数，并按模型体积加上下文余量做保守内存预算，实际 RSS、并存与延迟仍须真实测量。

正常关闭在任务退出、确认内容落库和 native 句柄回收后发送关闭响应；结束时记录尚在 engine 缓冲中的输入。资源抢占会唤醒正在等待输入的连接并返回 `session_stopped`，静音期间也不会无限占用语音模型。`response.done` 等待最终播放确认最多 5 秒，不能在音频仍排队时提前显示 listening。

## 错误与关闭

服务端错误事件至少包含：

```json
{
  "type": "error",
  "event_id": "server-event-0004",
  "sequence": 4,
  "timestamp_us": 928000,
  "code": "realtime_native_abi_unavailable",
  "message": "The installed speech libraries do not provide Realtime session ABI v1.",
  "fatal": false
}
```

当前实现可能返回的稳定错误 code 如下：

| Code | 语义 |
| --- | --- |
| `authentication_required` | 未认证连接的首事件不是 `session.authenticate`，或认证前发送 binary audio |
| `authentication_timeout` | 完整的首个 `session.authenticate` 控制事件未在 5 秒内到达 |
| `authentication_failed` | 一次性 ticket 无效、过期、来源不匹配或已消费 |
| `invalid_api_key` | Bearer API key 无效或格式错误 |
| `api_key_store_unavailable` | 本地 API key store 暂时不可用 |
| `credential_in_query_forbidden` | R20 v1 固定路由收到任意未定义的 URL query 参数 |
| `ticket_source_limit_reached` | 当前来源已有 16 个未过期 ticket |
| `ticket_capacity_exceeded` | 全局已有 128 个未过期 ticket |
| `ticket_generation_failed` | 安全随机 ticket 在有界重试内无法生成 |
| `host_not_allowed` | Host 不属于 loopback allowlist |
| `origin_not_allowed` | 浏览器 Origin 不是 exact same-origin |
| `realtime_remote_disabled` | 监听或远端不满足 loopback MVP |
| `websocket_required` | Realtime WebSocket 路由未收到 upgrade 请求 |
| `subprotocol_required` | 未协商 `tomur.realtime.v1` |
| `connection_limit_reached` | 全局 pending connection 已达到 8 |
| `source_connection_limit_reached` | 当前来源 pending connection 已达到 2 |
| `connection_id_unavailable` | 有界重试内无法创建连接预留 ID |
| `session_busy` | 已存在 active Realtime session |
| `invalid_event` | JSON envelope 或事件 payload 无效 |
| `invalid_event_id` | `event_id` 缺失、过长或包含协议不允许的字符 |
| `invalid_sequence` | 客户端控制 `sequence` 不是正整数 |
| `invalid_timestamp` | 客户端控制 `timestamp_us` 为负数 |
| `unsupported_event` | v1 当前矩阵未支持该事件 |
| `control_sequence_mismatch` | 控制事件 sequence gap、重复或回退 |
| `control_timestamp_reordered` | 控制事件 timestamp 相对前一事件回退 |
| `duplicate_event_id` | 当前 session 内重复使用客户端 `event_id` |
| `already_authenticated` | 已通过 Bearer 或 ticket 认证后再次发送 `session.authenticate` |
| `event_not_allowed` | 已知事件在当前 session 状态下不允许执行 |
| `input_audio_not_allowed` | 当前 session 状态不允许接收输入音频 |
| `audio_sequence_mismatch` | capture stream sequence gap、重复、乱序或未从 `1` 开始 |
| `audio_timestamp_reordered` | 输入音频 timestamp 相对前一帧回退 |
| `capture_stream_changed` | 未 clear 或重连就切换了 `capture_stream_id`；commit 不重置输入流 |
| `binary_header_too_short` | binary message 不足 44-byte header |
| `binary_magic_mismatch` | binary magic 不是 ASCII `TMR1` |
| `binary_version_mismatch` | binary frame version 不是 `1` |
| `binary_kind_unsupported` | binary frame kind 不受支持 |
| `binary_flags_unsupported` | v1 binary flags 不为 `0` |
| `binary_identifier_invalid` | binary frame identifier 为空 |
| `binary_sequence_invalid` | binary frame sequence 不在 `1..Int64.MaxValue` |
| `binary_timestamp_invalid` | binary frame timestamp 为负数 |
| `binary_payload_too_large` | header 声明的 payload 超过支持范围 |
| `binary_length_mismatch` | header payload length 与实际 message 长度不一致 |
| `binary_direction_invalid` | 客户端发送了非 input kind 的 binary frame |
| `input_audio_frame_size_invalid` | kind `1` 输入 payload 不是固定 640 bytes |
| `message_too_large` | JSON、binary 或累计 fragment 超限 |
| `fragment_type_mismatch` | 同一 message 的 WebSocket fragments 混用了 text 与 binary 类型 |
| `fragment_limit_exceeded` | 单 message fragment 数超过 32 |
| `event_rate_exceeded` | 超过 100 events/秒 |
| `session_event_limit_exceeded` | 超过 50,000 events/session |
| `input_queue_overflow` | inbound queue 已满 |
| `input_audio_buffer_overflow` | utterance 超过时长或 byte 上限 |
| `invalid_session_configuration` | `session.update` 配置缺失或不满足 v1 固定音频格式 |
| `session_update_during_utterance` | 输入音频尚在缓冲时尝试更新 session 配置 |
| `capture_stream_mismatch` | commit/clear 提供的 `capture_stream_id` 无效或与当前输入流不匹配 |
| `input_audio_buffer_empty` | 当前输入缓冲为空时执行 commit |
| `utterance_id_invalid` | 提供的 `utterance_id` 不是非空 UUID |
| `response_epoch_invalid` | `response.cancel` 的 `response_epoch` 不是正整数 |
| `response_not_active` | displayed/played acknowledgement 引用的 response 当前不活跃 |
| `session_configuration_required` | 发送 audio 前必须完成 session.update |
| `session_already_configured` | 已驻留的配置不能原位修改，须重连 |
| `realtime_model_unavailable` | 指定本地模型不存在或能力不匹配 |
| `realtime_native_abi_unavailable` | Whisper/TTS 库缺失、损坏或没有 Realtime ABI v1 |
| `realtime_memory_budget_exceeded` | 所选模型超过保守内存估算预算 |
| `realtime_resource_busy` | 另一推理入口正在使用资源，或语音句柄尚未释放 |
| `vad_execution_failed` / `asr_execution_failed` / `tts_execution_failed` | 本地语音执行失败 |
| `asr_timeout` / `response_timeout` | native 或响应超出时间预算 |
| `transcript_empty` | 未识别出有效语音，未生成占位用户内容 |
| `utterance_too_long` / `turn_limit_exceeded` | 30 秒发言或 100 回合上限 |
| `response_cancellation_pending` | 已有旧 native 工作正在退出，不继续扩展任务链 |
| `tts_segment_limit` | 单个响应超过 128 个短句 |
| `session_stopped` | 会话时限或进程内资源所有者终止会话 |
| `text_queue_overflow` / `tts_text_queue_overflow` / `tts_output_overflow` | 有界 token/短句/PCM 队列溢出 |
| `playback_backpressure` | 输出未消费水位持续超限 |
| `invalid_display_ack` / `invalid_playback_ack` | 确认越界、回退或与已发送边界不匹配 |
| `session_idle_timeout` | 已认证 session 连续 30 秒没有收到完整消息 |
| `session_duration_exceeded` | session 达到 15 分钟总时长上限 |
| `transport_error` | WebSocket 或底层 I/O 在接收期间异常结束 |
| `input_channel_closed` | bounded inbound channel 在没有终止事件时异常关闭 |

WebSocket close code 冻结如下。正常关闭使用标准 code；终止错误先尽力发送结构化 `error`，再使用对应私有 code。close reason 只携带固定短标识，客户端应以 `error.code` 为完整诊断依据。

| WebSocket code | 名称 | 使用范围 |
| ---: | --- | --- |
| `1000` | Normal Closure | 有效 `session.close` 或收到 peer close 后完成正常握手 |
| `4001` | Authentication Failed | `authentication_required`、`authentication_failed` |
| `4002` | Protocol Error | JSON、事件 envelope、顺序、binary frame、状态或 transport 协议错误 |
| `4003` | Policy Violation | event rate 或 session event 总量超限 |
| `4004` | Session Busy | active Realtime session 配额不可用 |
| `4008` | Timeout | authentication、idle 或 session duration 超时 |
| `4009` | Queue Overflow | inbound queue、输入音频缓冲或 bounded input channel overflow |

认证、Origin、Host、subprotocol、配额和尺寸错误必须尽可能在 upgrade 前以 HTTP 状态拒绝。upgrade 后若仍需终止，服务端先尝试在 2 秒 send deadline 内发送非敏感 `error`，随后发起 WebSocket close；客户端不得依赖 close reason 获得完整诊断。

## 当前能力边界

原生级联链路已接入，仍有以下明确边界：

- 当前发布库必须重新包含 `tomur_realtime_speech_*` / `tomur_realtime_tts_*` ABI v1；旧库返回明确不可用诊断，不回退为伪流式批处理。
- Whisper partial 是有界重叠窗口的替换结果；TTS 是短句增量，不是直接 speech-to-speech，也不是 vocoder 每个音频 token 的连续生成。
- warm TTS RTF、首个有意义 partial、CER/WER、AEC/barge-in、Soak、AOT 与跨平台发布均未验证。
- Realtime 当前不执行工具，也不把语音识别结果作为副作用确认；未知工具事件明确拒绝。
- OpenAI Realtime 风格适配仍待实现，不能把本协议宣称为 OpenAI Realtime 兼容。

原始证据入口为 [R20 smoke](./r20-realtime-voice-smoke.md)。代码接入不改变 pending 验收状态。
