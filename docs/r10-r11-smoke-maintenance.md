# R10 / R11 Smoke 维护清单

记录时间：2026-07-02

本文记录 R10 会话服务与 R11 Web 工作台的回归验证范围。执行构建、启动、浏览器验证或真实模型 smoke 前，仍需遵守仓库验证规则：只有用户明确要求验证时才运行本机命令。

## 范围

1. R10 会话 API：会话创建、列表、详情、软删除、文本回合、语音回合、消息追加、产物登记、产物读取和诊断登记。
2. R11 工作台：Chat-first 默认入口、模型选择、状态抽屉、M1 Settings 分组、Chat 上下文诊断入口、附件回合、按钮式录音、TTS 播放和历史同步。
3. R8/R9 能力复用：多模态真实模型 smoke 证据继续引用 `docs/r8-smoke-report.md`，Agent/AOT 边界继续引用 `docs/r9-aot-trimming-audit.md`。

## API 回归矩阵

| Area | Endpoint | Expected |
| --- | --- | --- |
| Conversation list | `GET /api/conversations` | 返回本地未删除会话列表 |
| Conversation create | `POST /api/conversations` | 写入 SQLite 会话记录 |
| Conversation detail | `GET /api/conversations/{conversationId}` | 返回消息、产物和诊断 |
| Conversation delete | `DELETE /api/conversations/{conversationId}` | 软删除会话，列表不再显示 |
| Text turn | `POST /api/conversations/{conversationId}/turns` | 记录用户消息、工具摘要、助手回复、产物和诊断 |
| Voice turn | `POST /api/conversations/{conversationId}/voice-turns` | 返回 transcript、assistant text、TTS artifact 或明确诊断 |
| Artifact content | `GET /api/conversations/{conversationId}/artifacts/{artifactId}/content` | 只允许读取 Tomur 数据目录内产物 |

## Web 回归矩阵

1. 默认入口直接显示 Chat 工作台，不显示管理后台式首页。
2. 刷新状态会读取 version、runtime、multimodal、catalog、installed models 和 `/v1/models`。
3. 启动后读取 `/api/conversations`，点击历史会话时懒加载详情。
4. 纯文本 streaming 成功后补写到 `/api/conversations`。
5. 带图片、音频或文本文件附件的回合调用 conversation turn。
6. 录音按钮生成 16 kHz mono PCM WAV 后调用 voice turn。
7. 诊断标签可进入 Models、Runtime、API、Files 或 Advanced 对应 Settings 分组。
8. 会话菜单调用软删除 API；刷新后被删除会话不再出现在列表中。
9. Settings M1 分组只展示当前已接通能力，不把下载队列、设置写入或模型删除展示为已可用功能。

## 不计入完成口径

1. 可视化下载队列。
2. Settings 写入编辑。
3. 模型删除与导入向导。
4. VAD、唤醒词、barge-in 打断。
5. ASR/TTS 分段 streaming。
6. 模型自主多模态 tool-calling。

