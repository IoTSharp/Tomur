# R20 Realtime 原生协议 v1

> 本文冻结 R20 首个实现切片的线级协议与安全边界，不表示 P0 或 P1 已完成。当前切片只建立本地 WebSocket 网关、认证、配额、控制事件和输入缓冲边界；VAD、增量 ASR、增量文本、增量 TTS、全双工、barge-in、AudioWorklet 和 OpenAI Realtime 风格适配均未接入。

## 协议概览

R20 v1 是 Tomur 的本地优先原生 Realtime 协议。首个实现切片用于验证连接生命周期、安全边界、帧格式、顺序、背压和清理行为，不产生 transcript 或音频输出，也不把输入 PCM 持久化到文件、SQLite 或日志。

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
| 当前处理模式 | 有界 push-to-talk 输入缓冲；pipeline 未接通 |

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

首个切片即使 gateway 可用，也必须报告 pipeline、VAD、增量 ASR、增量 TTS 和 full duplex 未接入。Status 响应不得包含 ticket、API key prefix、session token、原始音频或 transcript。

未来客户端自动重试上限暂定为 3 次，但当前 status DTO 不发布 `maximum_client_reconnects`，gateway 也不跨连接跟踪或强制执行该策略；内置 Realtime 客户端、backoff 和断线恢复尚未接入。`limits.graceful_close_timeout_milliseconds = 2000` 发布 close 的墙钟截止，最多 32 次 receive 仍是服务端内部协议边界。

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
| `session.update` | 已认证 | 更新当前切片支持的 session 配置；固定音频格式不可改为其他值 |
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
| `response.cancelled` | 有效 `response.cancel` | 当前切片没有 active response 时返回请求的 `response_epoch`，且 `reason` 为 `not_active` |
| `error` | 非终止或终止错误 | 返回稳定 code、非敏感 message、`fatal` 和必要的作用域标识 |

`response.text.displayed` 与 `response.audio.playback_consumed` 是客户端 acknowledgement，不要求额外成功事件。若引用的 response 当前不活跃，服务端返回可恢复的 `response_not_active`。

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

每个新 `capture_stream_id` 的 binary sequence 从 `1` 开始。当前缓冲必须先 commit 或 clear，之后同一连接才可以切换到新的 `capture_stream_id`；缓冲仍存在时直接切换会返回终止错误 `capture_stream_changed`。duplicate、gap、回退或跨 capture stream 复用 sequence 都不能静默接受。输入 timestamp 表示该帧首个 sample 的客户端单调采集时间；它用于同一客户端时钟域内的顺序与延迟测量，不代表 UTC。

当前切片对单个 utterance 最多接收 30 秒或 960,000 payload bytes，即最多 1,500 个标准输入 frame。达到任一上限后，不再接受额外 PCM，必须产生稳定诊断并清理或关闭，不得继续增长缓冲。

### Output PCM

kind `2` 为未来 24 kHz mono PCM16 输出保留，其 ID 是 `response_id`，sequence 在每个 response 内从 `1` 开始。首个切片不得发送 kind `2` frame；提前发送空白或静音 PCM 不能用来表示 pipeline 可用或满足首音频延迟。

## Commit、clear 与持久化

`input_audio_buffer.commit` 必须引用当前有效的 `capture_stream_id`。在首个切片中，有效 commit 的确定行为是：

1. 封存并清空该 capture stream 的内存缓冲。
2. 发送 `input_audio_buffer.committed`，报告 `buffered_audio_bytes` 和 `duration_ms`。
3. 发送非终止 `error`，code 固定为 `realtime_pipeline_unavailable`，`fatal` 为 `false`。
4. 不生成 speech started/stopped、partial/final transcript、assistant text 或 output audio。
5. 不创建 conversation message、artifact 或 diagnostic，不把 PCM 写入文件、SQLite、普通日志或 trace。

`input_audio_buffer.clear` 省略、设为 `null` 或空白 `capture_stream_id` 时立即丢弃当前缓冲；提供 ID 时，该 ID 必须是当前有效的 `capture_stream_id`，否则返回可恢复的 `capture_stream_mismatch` 且不清理缓冲。成功后发送 `input_audio_buffer.cleared`。clear 是内存生命周期操作，不表示撤销已经提交的外部副作用。

## 状态与顺序

完整 R20 状态集合预留为：

```text
connecting -> listening -> user_speaking -> transcribing -> thinking -> speaking
```

并包含 `interrupted`、`reconnecting`、`failed` 和 `closed`。首个切片实际执行的正常输入路径为：

```text
connecting -> listening -> user_speaking -> transcribing -> listening
```

任一阶段都可以按协议进入 `failed` 或 `closed`。当前 `user_speaking` 仅表示客户端已经开始提交输入 PCM，是 transport 缓冲状态，不是 VAD 检测结论；`transcribing` 仅标记手动 commit 边界，不表示 ASR 已运行。commit 返回 `realtime_pipeline_unavailable` 后，连接恢复到 `listening`，除非同时触发资源、安全或协议终止条件。

客户端每次重新连接都必须建立新 session、重新认证并从 sequence `1` 开始。协议为未来客户端冻结最多 3 次自动重试和有界 backoff 策略，但当前切片尚未接入客户端自动重连，也不由 gateway 跨连接计数或强制执行；当前 gateway 不恢复旧 PCM、旧 response、旧 acknowledgement 或可能有副作用的工具执行。

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
| 未来客户端自动重试策略 | 最多 3 次 | 尚未接入；未来客户端停止自动重试并展示诊断，gateway 不把它作为当前连接配额 |

事件计数同时包含完整 JSON 控制 message 和完整 binary frame，避免通过 binary 流绕过速率与 session 总量限制。合法 ping 和 PCM 会刷新 idle deadline；未完成 fragment、无效消息和被拒绝的凭据不会无限延长连接寿命。

所有 channel 都必须有固定容量。控制事件不得使用静默 `DropOldest`；输入 queue overflow、gap、duplicate 或慢消费必须通过 `error` 和明确 close/reset 行为暴露。当前没有 output audio，因此不得预建无界输出缓冲。

## 错误与关闭

服务端错误事件至少包含：

```json
{
  "type": "error",
  "event_id": "server-event-0004",
  "sequence": 4,
  "timestamp_us": 928000,
  "code": "realtime_pipeline_unavailable",
  "message": "Realtime audio processing is not connected in this protocol slice.",
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
| `capture_stream_changed` | 当前缓冲 commit/clear 前切换了 `capture_stream_id` |
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
| `realtime_pipeline_unavailable` | 网关存在，但 VAD/ASR/LLM/TTS pipeline 未连接 |
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

该协议冻结不构成以下能力的接入或验证证据：

- Silero VAD session、speech started/stopped 或 pre-roll；
- 常驻 Whisper session、滚动窗口、partial/final transcript 或 native abort；
- 异步 LLM token channel、短句聚合或 response epoch 取消栅栏；
- 常驻 TTS acoustic/WavTokenizer session、PCM callback 或 audio delta；
- AudioWorklet、输入降采样、24 kHz 输出重采样或 jitter buffer；
- echo cancellation、barge-in、全双工或断线恢复；
- OpenAI Realtime 风格兼容。

在真实协议测试、安全测试、资源回收测试和后续语音质量证据完成前，不得宣称 R20 P0 或 P1 完成，也不得把 `gateway available` 表述为 `Realtime voice ready`。
