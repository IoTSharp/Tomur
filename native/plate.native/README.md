# plate.native

本目录提供 Tomur 的车牌识别稳定 C ABI。识别引擎采用 [HyperLPR3](https://github.com/szad670401/HyperLPR) C++/MNN 路线，桥接层负责图片解码、上下文复用、参数校验和 JSON 序列化。

HyperLPR3 源码以 `native/hyperlpr3` Git 子模块固定到上游提交 `9307450f7b7915be18f23a539ec05b41fe6629f4`，当前接口按该提交的公开 C API 编写。MNN 2.2.0 固定在 `native/mnn` 的 `4634ed830248844034cc926646834dabfb6d9adc`，OpenCV 4.12.0 固定在 `native/opencv` 的 `49486f61fb25722cbcf586b7f4320921d46fb38e`。Tomur 不提交预编译库；发布构建必须从这些固定源码生成目标架构产物并审计最终 bundle。HyperLPR3 子模块自带 `r2_mobile` 模型文件，Tomur 不会自动将它们安装到模型目录或复制进 native bundle，使用和分发前仍需单独审计许可。

## 模型目录

托管层向 ABI 传入的 `model_dir_utf8` 必须直接指向 `r2_mobile`：

```text
<data>/models/plate/hyperlpr3/r2_mobile/
  b320_backbone_h.mnn
  b320_header_h.mnn
  b640x_backbone_h.mnn
  b640x_head_h.mnn
  litemodel_cls_96xh.mnn
  rpv3_mdict_160h.mnn
```

默认数据目录下的完整位置为 `<data>/models/plate/hyperlpr3/r2_mobile`。bridge 会在创建上下文前逐一验证上述六个模型；缺失或空文件返回 `TOMUR_PLATE_STATUS_MODEL_UNAVAILABLE`。模型权重来源与再分发条款必须在发布前单独审计，不能因 HyperLPR3 源码使用 Apache-2.0 就推定模型权重具有相同许可。

桥接层固定使用 `DETECT_LEVEL_HIGH`，实际加载 `b640x_backbone_h.mnn` 与 `b640x_head_h.mnn` 进行 640x640 检测；320 模型仍属于上游完整 `r2_mobile` 资产并接受完整性检查。该选择面向整幅抓拍图中的小车牌，CPU 延迟和现场准确率仍需按目标架构实测。

## C ABI

```c
tomur_plate_result * tomur_plate_recognize_image(
    const char * model_dir_utf8,
    const uint8_t * image_data,
    size_t image_length,
    int max_results,
    float min_confidence);

void tomur_plate_result_free(tomur_plate_result * result);
```

`image_data` 是 JPEG、PNG、WebP 或 BMP 编码图片，原生层限制为 64 MiB，并在 OpenCV 完整解码前读取图片头、拒绝超过 5000 万像素的输入；`max_results` 范围为 `1..10`，`min_confidence` 范围为 `0..1`。返回对象及其中两个字符串由原生层分配，调用方必须使用 `tomur_plate_result_free` 释放。

成功时 `json_utf8` 只包含托管层需要的内部数据：

```json
{
  "results": [
    {
      "plate_number": "新A12345",
      "plate_type": "blue",
      "plate_color_code": "0",
      "vehicle_id": "新A12345_0",
      "recognition_confidence": 0.96,
      "detection_confidence": null,
      "box": [12, 34, 156, 78]
    }
  ]
}
```

HyperLPR3 公开 C API 不提供检测置信度，因此 `detection_confidence` 明确为 `null`，不得使用文字置信度伪造。颜色码映射为蓝 `0`、黄 `1`、黑 `2`、白 `3`、绿 `11`；无法从上游类型确认颜色时使用未确定 `9`。

状态码含义：

| 值 | 名称 | 含义 |
| --- | --- | --- |
| 0 | `OK` | 调用成功，允许结果数组为空 |
| 1 | `INVALID_ARGUMENT` | 参数范围或输入指针无效 |
| 2 | `MODEL_UNAVAILABLE` | 模型目录或必要模型文件缺失 |
| 3 | `IMAGE_DECODE_FAILED` | 图片无法解码或超过大小限制 |
| 4 | `RUNTIME_INITIALIZATION_FAILED` | HyperLPR3/MNN 上下文初始化失败 |
| 5 | `RECOGNITION_FAILED` | 推理过程或上游结果结构异常 |
| 6 | `INTERNAL_ERROR` | 未分类的原生异常 |

## 构建依赖

先从 `native/mnn` 和 `native/opencv` 为目标 RID 构建依赖，再从 `native/hyperlpr3` 构建 HyperLPR3，并设置：

- `TOMUR_HYPERLPR3_ROOT`：包含 `include/hyper_lpr_sdk.h` 与共享库 `lib`/`bin` 的安装根目录；当前 bundle 会探测 HyperLPR3 动态库，不接受仅有静态 `.a` 的安装。
- `OpenCV_DIR`：目标架构 OpenCV 的 CMake package 目录。
- `TOMUR_HYPERLPR3_RUNTIME_LIBRARY`：Windows 下必须显式指向 `hyperlpr3.dll`。
- `TOMUR_MNN_RUNTIME_LIBRARY`：HyperLPR3 动态链接 MNN 时指向对应运行库；静态链接时留空。
- `TOMUR_PLATE_RUNTIME_DEPENDENCIES`：需要随包分发的 OpenCV/MNN 动态库绝对路径列表。

HyperLPR3 的 `LINUX_FETCH_MNN` 仅用于声明 CMake `FetchContent` 关系；可复现发布必须同时设置 `FETCHCONTENT_SOURCE_DIR_MNN=<tomur>/native/mnn`，使它使用固定子模块而不是访问网络。OpenCV 必须从 `native/opencv` 构建，并把 `core`、`imgproc`、`imgcodecs` 及其实际动态依赖放入同一 `plate/cpu` runtime 目录。

随后使用 Tomur 统一入口：

```powershell
tomur native build --rid win-x64 --backend cpu
```

```bash
tomur native build --rid linux-x64 --backend cpu
tomur native build --rid linux-arm64 --backend cpu
```

Linux ARM64 预设使用 `aarch64-linux-gnu-gcc/g++` 交叉编译。HyperLPR3、MNN 与 OpenCV 必须同为 Linux ARM64 产物，不能让 CMake 回退到构建机的 x64 依赖。安装结果进入 `native/runtimes/<rid>/native/plate/cpu/`；模型仍由 Tomur 模型目录独立管理。
交叉编译配置会强制要求目标架构的 HyperLPR3 路径和 `OpenCV_DIR`，并以 `NO_DEFAULT_PATH` 解析这些依赖；缺失时在配置阶段直接失败，不等待链接阶段才暴露架构错误。
