# Tomur

Tomur 是一个 .NET 10 + C# 构建的本地优先 AI Runner。核心程序名就是 Tomur，Windows 发布为 `tomur.exe`，Linux 发布为 `tomur`。

Tomur 必须保持单体入口：本地运行时、模型目录、下载、诊断、兼容 API 和前端工作台都围绕同一个程序组织。模型文件、数据文件和 native runtime 可以位于外部资产目录，但用户面对的启动入口只有 Tomur。

## 核心能力

1. 本地文本模型运行。
2. OCR native 能力。
3. 本地图像生成。
4. 本地语音识别。
5. 本地 TTS。
6. 本地兼容 API。
7. Chat、Models、Downloads、Runtime、Files、Transcribe、Images、Settings 工作台。

## 工程边界

Tomur 的工程边界是单体本地程序、native runtime 管理、本地模型资产管理、兼容 API 和前端工作台。

Tomur 文档和界面文案只描述自身能力，不写任何非 Tomur 信息。
