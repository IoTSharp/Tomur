# Tomur fixed llama.cpp ABI 对照证据

## 身份与范围

- 日期：2026-10-08（Asia/Shanghai；过程日志文件名为 UTC）
- 固定 native 子模块：1bc7a5af0d14b1fb72f266abbd1237b394187115
- PowerShell：7.6.6，固定路径 C:\Program Files\PowerShell\7\pwsh.exe
- Native compiler：MSVC 19.51.36260.0；C17、UTF-8、/W4 /WX；Windows SDK 10.0.26100.0
- .NET SDK：10.0.401；实际 source link 编译；net10.0、x64，0 warnings / 0 errors。
- 未执行服务、native DLL 加载、模型加载、推理、AOT 发布或 Git 变更。
- 源与 header SHA-256、原始输出见 oracle-result.json。oracle 工程链接实际 LlamaNativeMethods.cs，不复制结构声明。

## 布局结果

两边 tiny 均先执行第一个结构/字段（两行）成功。完整检查只遍历 6 个结构、每结构最多 64 字段、5 秒取消；对照最多 128 行、5 秒。80 行全部逐字一致：

| 结构 | native sizeof | Marshal.SizeOf |
| --- | ---: | ---: |
| ggml_backend_dev_caps | 5 | 5 |
| ggml_backend_dev_props | 56 | 56 |
| llama_model_params | 80 | 80 |
| llama_context_params | 160 | 160 |
| llama_sampler_chain_params | 1 | 1 |
| llama_batch | 56 | 56 |

新增 caps.mmap_support 偏移 4；仅比较 props 总尺寸会被尾部 padding 掩盖该遗漏。模型结构 load_mode/lazy_mode 位于 24/28，bool 位于 72..77；context 每序列上限位于 24，bool 位于 128..133，n_samplers（size_t）位于 144。完整偏移保留在 native-full / managed-full stdout。

## 42 个函数签名审查

以下与固定头文件逐项比对；ptr 表示指针大小 nint，size_t 使用 nuint，token/position/sequence ID 为 int32，所有 enum 为 int32，float 为单精度。const 不影响传参 ABI。

| Native exports | 托管形状与结论 |
| --- | --- |
| llama_backend_init | void()，一致 |
| ggml_backend_load_all_from_path | void(UTF-8 char*)，一致 |
| ggml_backend_load | ptr(UTF-8 char*)，一致 |
| ggml_backend_register | void(ptr)，一致 |
| ggml_backend_dev_count / ggml_backend_dev_get | size_t() / ptr(size_t)，一致 |
| ggml_backend_dev_type | enum(ptr)，补齐 META=4；一致 |
| ggml_backend_dev_get_props | void(ptr, out props)，不是按值；结构对照一致 |
| ggml_backend_dev_backend_reg / ggml_backend_reg_name | ptr(ptr) / UTF-8 borrowed char*(ptr)，一致；返回字符由 PtrToStringUTF8 读取 |
| llama_model_default_params / llama_context_default_params / llama_sampler_chain_default_params | 三个结构均按值返回；保留 DllImport 按值返回，不改为指针 |
| llama_model_load_from_file | ptr(UTF-8 char*, model_params 按值)，一致 |
| llama_model_free / llama_free | void(ptr)，由既有 SafeHandle 对应释放；一致 |
| llama_init_from_model | ptr(ptr, context_params 按值)，一致 |
| llama_model_get_vocab | borrowed ptr(ptr)，一致 |
| llama_vocab_n_tokens / llama_model_n_embd / llama_model_n_embd_out | int32(ptr)，一致 |
| llama_tokenize | int32(ptr, UTF-8 char*, int32 byteLength, token*, int32, bool, bool)，现有 I1 显式单字节，一致 |
| llama_batch_get_one | batch 按值返回(token*, int32)，一致 |
| llama_decode | int32(ptr, batch 按值)，一致 |
| llama_sampler_chain_init | ptr(sampler_chain_params 按值)，一致 |
| llama_sampler_chain_add | void(ptr, ptr)，一致 |
| llama_sampler_init_top_k | ptr(int32)，一致 |
| llama_sampler_init_top_p | ptr(float, size_t)，一致 |
| llama_sampler_init_temp | ptr(float)，一致 |
| llama_sampler_init_penalties | ptr(int32 n_vocab, int32 last_n, float repeat, float freq, float present)，五参一致 |
| llama_sampler_init_dist | ptr(uint32)，一致 |
| llama_sampler_free / llama_sampler_reset | void(ptr)，一致 |
| llama_sampler_sample | int32(ptr, ptr, int32)，一致 |
| llama_token_to_piece | int32(ptr, int32, byte*, int32, int32, bool)，显式 I1 单字节，一致 |
| llama_vocab_is_eog | bool(ptr, int32)，返回显式 I1 单字节，一致 |
| llama_get_embeddings | float*(ptr)，一致 |
| llama_set_embeddings | void(ptr, bool)，显式 I1 单字节，一致 |
| llama_get_embeddings_ith / llama_get_embeddings_seq | float*(ptr, int32)，一致 |
| llama_get_memory | opaque ptr(ptr)，一致 |
| llama_memory_clear | void(ptr, bool)，显式 I1 单字节，一致 |

只审查 header/signature，不验证已安装 DLL 的 exports 或库 provenance。目标没有 win-x86 RID；本机 x64 calling convention 统一，现有 source-generated imports 保留 Cdecl。

## 调用方与生命周期

LlamaNativeSession 不再引用被移除的 use_mmap/use_direct_io/use_mlock；ModelDefaultParams 返回后只覆盖 n_gpu_layers/devices/main_gpu，ContextDefaultParams 返回后覆盖保留字段。未强制覆写 vocab_only/no_alloc 掩盖错位。设备 null-terminated 列表由 using scope 保持到同步模型加载之后；prompt token 指针处于 fixed，单 token 位于同步 Decode 的 stack scope。Callback 字段为 native pointer；当前调用方没有赋值 progress_callback/cb_eval/abort_callback，保留 native 默认，不引入无根 delegate。

## 有界过程

所有编译与执行通过 Sezika Invoke-BoundedProcess：编译 native <=60s，managed <=90s；tiny/full/sdk-version 每项 <=20s。runner 的 result/identity JSON 记录 PID、创建时间、命令、launcher/父链及 finally 清理结果。

| 步骤 | 根 PID | 结果 |
| --- | ---: | --- |
| native 首次 /W4 /WX /TC | 81848 | 退出 2；header C4201 匿名 union、C4819 编码警告触发 /WX |
| native C17 UTF-8 重试（最多两次编译） | 54444 | 成功；无关闭 warning 的选项 |
| managed compile | 78796 | 成功；0 warnings / 0 errors |
| managed tiny | 62688 | 成功 |
| native tiny | 27176 | 成功 |
| managed full | 29152 | 成功 |
| native full | 78056 | 成功 |
| SDK version | 55308 | 成功；10.0.401 |

该验证仅确认 fixed header 的 Windows x64 托管布局和声明；真实模型、不同 native 二进制、Native AOT、其他 OS/架构保持由主线程独立验收。
资源复核：8 个 result 均无 cleanup errors；记录的 9 个 PID 已全部退出。自动审批拒绝合并清理及更窄的两显式文件 Remove-Item，理由仅返回 blocked by policy，均未执行；native-layout.exe/.obj 与 bin/obj 因此保留并报告，不请求用户重复授权。
