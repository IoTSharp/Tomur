# R22 多语言决策引擎对接

状态：计划中，2026-09-23。Sezika 已完成可执行的纯 C# CPU typed decision engine，以及 CUDA Driver 固定 PTX vector-add/GEMM、决策头和 win-x64 Native AOT smoke；Tomur 尚未新增 provider、模型包或可用决策端点。

## 定位

[Sezika](https://github.com/IoTSharp/Sezika) 是独立的 C# / .NET 10 多语言非自回归 System 1 决策引擎项目。其当前证据包括 tiny deterministic model 的 Choice、Score、Boolean CPU 闭环，以及 CUDA Driver 固定 PTX/GEMM 决策头和 win-x64 Native AOT smoke；这些证据不代表真实发布模型质量或完整 Transformer GPU encoder。Tomur 计划通过静态引用的 provider 同进程调用，输入文本/JSON state 与类型化问题，输出 Choice、Score、Boolean 和概率；决策不等同于聊天回复或工具执行。

独立引擎的研究、架构和 CPU/GPU 路线维护在其 [ROADMAP](https://github.com/IoTSharp/Sezika/blob/main/ROADMAP.md)、[架构](https://github.com/IoTSharp/Sezika/blob/main/docs/architecture.md)、[GPU/AOT 设计](https://github.com/IoTSharp/Sezika/blob/main/docs/gpu-aot.md) 和 [Tomur 对接设计](https://github.com/IoTSharp/Sezika/blob/main/docs/tomur-integration.md)。Tomur 的排期与验收以本仓库 [R22](../ROADMAP.md) 为准。

## 接入边界

- `providers/Decision` 新增薄适配层；Sezika 反向不依赖 Tomur。正式引用固定 NuGet 版本，不提交兄弟工作目录的绝对 ProjectReference。
- `providers/Abstractions` 新增 decision provider/session 契约；现有 text generation provider 不承担该职责。
- `ModelProviderRegistry` 静态注册 `managed-decision`；模型 capability 增加 `decision`，不将此类模型默认当作聊天模型。
- 模型继续使用 `<data>/models`、Catalog、pull、checksum、license 与安装清单；未确定 revision/许可/完整资产前不登记可用模型。
- `app/Decisions` 管理 session、资源、排队、取消；`app/Api` 提供专用入口；`ServeCommand` 与 `AppJsonSerializerContext` 静态注册。
- 拟议入口为 `GET /api/decisions/status` 与 `POST /api/decisions`；可选 `/v1/systemone` 等待独立兼容性矩阵，不宣称已可调用。

## 纯 C# GPU 与 AOT

Sezika 模型/算子/调度源代码为 C#，允许 CUDA Driver 等系统驱动调用；不通过 ONNX Runtime、LibTorch、cuBLAS/cuDNN 或 C++ bridge 承担推理。当前已验证固定 PTX vector-add/GEMM、CUDA Driver 资源生命周期、typed decision head 和 win-x64 Native AOT smoke；这条证据不等同于完整 Transformer GPU encoder 已接通。构建期 PTX/ABI 与 AOT Driver loader 的路线仍需扩展到完整模型算子并分别验收。

ILGPU 常规运行路径依赖 IL 读取与 Reflection.Emit，不能直接认为 Native AOT 兼容。Sezika 的固定 PTX/GEMM 原型已完成目标设备实卡数值、driver/module/launch 和资源回收 smoke；完整模型 kernel ABI、显存预算和逐算子对齐仍待验证。NVIDIA 是首条 GPU 路线，其他厂商不在首个支持承诺中。

该 driver-only 约束属于 Sezika 决策 provider，不更改 Tomur 已有 native runtime 能力。

## 诊断、身份与执行

分别报告内置 provider、driver 可用、资产完整、schema 有效、session 加载、推理完成/拒答、校准状态、多语质量、AOT smoke 和性能证据。缺少模型或失败时返回清晰错误，不用规则结果或示例概率代替模型推理。

现有普通 API 路径没有在本次检查中确认统一鉴权，新增端点必须显式接入实际身份和监听策略，不能假定 ApiKeyStore 已覆盖所有路由。多语 tokenizer 必须验证主程序 `InvariantGlobalization=true` 下的实际行为。

Agent 的 `decision.predict` 仅作只读预测。它选择的模型/工具/action 是建议，不能绕过已有 allowlist、参数校验、精确确认与最大迭代次数。未完成语言质量/校准证据前，不能自动接管 Chat 路由。

## 完成条件

真实发布模型与许可、完整 GPU encoder 的 C# 数值对齐、Tomur CPU/CUDA Native AOT 宿主发布、多语言测试、取消/卸载/内存显存回收、协议与鉴权、原有 Chat/Agent 回归分别有记录后，才提升对应状态。Sezika 的 CPU typed engine 与固定 CUDA PTX/GEMM smoke 已有独立证据，但 Tomur provider/API/Catalog 运行验收仍待执行。
