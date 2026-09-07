# R21 ONNX 视觉模型提供器参考清单

本文是 Tomur R21 的规划输入，不表示这些模型或接口已经接入。能力矩阵参考
[javpower/rust-onnx-infer](https://gitee.com/javpower/rust-onnx-infer)，上游当前公开版本为 `0.2.0`，本次考察的 `master` 提交为
`8158cf0836e004d9f56ee15782a9d7c72b07f175`。

## 参考范围

Tomur 计划完整纳入以下模型能力族的评估与接入排期。每个能力族仍需单独确认模型权重、标签、分词器、后处理和再分发许可；模型不会因为出现在本清单中而进入默认 Catalog。

| 能力族 | 参考模型/流水线 | Tomur 规划边界 |
| --- | --- | --- |
| 图像分类 | YOLO-CLS、ResNet、MobileNet、EfficientNet、ViT、自定义归一化 | 统一图像输入、标签、top-k 与批量推理契约 |
| 目标检测 | YOLOv5/v8/v9/v10/v11/v26、RT-DETR、DETR、RF-DETR | 传统输出与 End2End 输出显式识别，统一框、置信度和 NMS |
| 实例分割 | YOLO-Seg、RF-DETR-Seg、YOLOE | 输出实例框、类别、置信度和掩码；视觉/文本提示分开建模 |
| 交互式分割 | SAM、SAM2 | 支持点、框和掩码提示，模型状态有界驻留 |
| 开放词表 | Grounding DINO、Grounded-SAM、DART v2 | 文本 tokenizer、检测和分割流水线可诊断串联 |
| 显著性抠图 | BiRefNet | 输出 soft alpha，并提供尺寸与色彩空间说明 |
| 超分与图像增强 | Real-ESRGAN、DnCNN、Zero-DCE、DehazeFormer | 分块、重叠、拼接和增强参数有明确资源上限 |
| 特征匹配 | LightGlue、DeDoDe-G、LoMa-R、RoMaV2 | 点对、置信度、密集对应场和批处理结果使用稳定契约 |
| 姿态估计 | YOLOv8/11/26-Pose、RTMO | COCO 关键点、SimCC 和置信度定义固定 |
| 人脸 | YuNet、SFace/ArcFace、年龄性别、表情、106 点、CDCN 活体 | 检测、属性、识别、关键点和活体分成独立能力，默认不持久化生物特征 |
| 文字识别 | PaddleOCR v4/v5 det+rec、DBNet、SVTR、CTC | 检测、识别、字典和方向处理分层，返回文本框与置信度 |
| 深度估计 | Depth Anything V2、MiDaS | 输出相对深度图，明确其不是绝对测距 |
| 风格迁移 | fast-neural-style（candy、mosaic） | 任意尺寸输入、动静态输出和内存上限可诊断 |
| 行人重识别 | OSNet-x1.0 | 512 维 embedding 与本地图库检索，需单独的数据保护边界 |
| 表格识别 | SLANet-plus | 结构 token、单元框和 OCR 回填 HTML 分层输出 |
| 旋转框检测 | YOLOv8/11-OBB、YOLO26-OBB | 旋转框坐标、角度和旋转 IoU NMS 独立于普通检测 |
| 语义分割 | SegFormer-B0（ADE20K） | 类别直方图、调色板叠加和原图映射 |
| 人体解析与人像分割 | SegFormer-B2、PP-HumanSeg、MODNet | 人衣类别占比与 alpha 抠人分开提供 |
| 去模糊 | NafNet | 对齐输入、输出尺寸和边界处理固定 |
| 图像质量 | 清晰度/亮度/对比度/噪声算法、FIQA | 算法评分与深度模型评分分开报告，不伪造客观质量结论 |
| 二维码 | WeChat QR Detector、rqrr | 检测与内容解码分层，解码内容按不可信输入处理 |
| 动作与手势 | ST-GCN、21 点几何手势规则、姿态规则 | 时序窗口有界；规则结果与模型结果区分 |
| 车牌 | mnet 检测、透视矫正、LPRNet | 与现有 HyperLPR3/MNN 和 R19 TomurLPR 并行，不能替换既有路径 |
| 手部与全身关键点 | Palm、RTMPose-hand、rtmpose-m WholeBody 133 点 | 检测与关键点串联，明确 body/face/hand/foot 子集 |

## Tomur 接入原则

1. Tomur 只把上游作为参考实现和模型能力清单；不把 Rust crate、Rust 进程或另一套 HTTP 服务作为 Tomur 依赖。
2. R21 provider 通过 `providers/` 的稳定契约静态接入 `Tomur.csproj`，模型格式、架构、输入尺寸、标签和后处理必须由 manifest 显式声明。
3. ONNX 图执行分为可审计的纯 C# 算子子集与明确声明的 native 加速路径。任何 native ONNX Runtime 或执行提供器都必须进入 Tomur runtime bundle、许可清单和诊断面，不能隐式下载或隐式 P/Invoke。
4. 模型权重、tokenizer、字典、类别标签和测试图片作为 `<data>/models` 下的独立资产管理；许可不清晰的资产只能由用户自行提供，不能进入默认 Catalog、程序或发布包。
5. 所有视觉输入、输出、队列、并发 session、图像尺寸、张量大小和批量大小必须有上限；模型缺失、算子不支持、shape/layout 不匹配、内存不足和后处理失败必须返回结构化诊断。
6. 图像、人脸、行人和车牌等敏感结果默认只在请求生命周期内存在，不写入普通日志或 SQLite；需要保存时必须由用户显式触发。

## 来源与致谢

Tomur R21 的能力分组、统一同步/异步推理接口、模型元数据标签、SAHI 切片思路以及上述视觉模型覆盖范围，受到 [javpower/rust-onnx-infer](https://gitee.com/javpower/rust-onnx-infer) 的公开工作启发。感谢 javpower 公开这套 Rust/ONNX Runtime 视觉推理实现。Tomur 将在 C# 单进程边界内独立实现和验证相关能力，不复制其运行时依赖；上游项目采用 MIT OR Apache-2.0，具体代码或模型资产是否可复用仍需逐项遵守其许可证和第三方许可。

## 证据要求

本清单建立时没有执行 Tomur 构建、测试、模型下载或真实视觉推理，R21 状态保持 `⏳ 计划中`。后续每个能力族必须分别记录模型来源、许可、输入输出样例、CPU/GPU 后端、冷/热延迟、峰值内存、取消与释放结果；只有真实模型 smoke 和发布矩阵证据齐备后，才能将对应子项标为已验证。
