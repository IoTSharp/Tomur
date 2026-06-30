using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomur.Inference;

internal static partial class LlamaNativeMethods
{
    internal const string LibraryName = "llama";
    internal const string GgmlLibraryName = "ggml";
    internal const string GgmlBaseLibraryName = "ggml-base";

    [LibraryImport(LibraryName, EntryPoint = "llama_backend_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void BackendInit();

    [LibraryImport(GgmlLibraryName, EntryPoint = "ggml_backend_load_all_from_path", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GgmlBackendLoadAllFromPath(string directoryPath);

    [LibraryImport(GgmlLibraryName, EntryPoint = "ggml_backend_dev_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint BackendDeviceCount();

    [LibraryImport(GgmlLibraryName, EntryPoint = "ggml_backend_dev_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint BackendDeviceGet(nuint index);

    [LibraryImport(GgmlBaseLibraryName, EntryPoint = "ggml_backend_dev_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GgmlBackendDeviceType BackendDeviceType(nint deviceHandle);

    [DllImport(GgmlBaseLibraryName, EntryPoint = "ggml_backend_dev_get_props")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static extern void BackendDeviceGetPropsCore(nint deviceHandle, out GgmlBackendDevicePropertiesNative properties);

    [LibraryImport(GgmlBaseLibraryName, EntryPoint = "ggml_backend_dev_backend_reg")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint BackendDeviceBackendReg(nint deviceHandle);

    [LibraryImport(GgmlBaseLibraryName, EntryPoint = "ggml_backend_reg_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint BackendRegNameCore(nint backendRegHandle);

    [DllImport(LibraryName, EntryPoint = "llama_model_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern LlamaModelParams ModelDefaultParams();

    [DllImport(LibraryName, EntryPoint = "llama_context_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern LlamaContextParams ContextDefaultParams();

    [DllImport(LibraryName, EntryPoint = "llama_sampler_chain_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern LlamaSamplerChainParams SamplerChainDefaultParams();

    [DllImport(LibraryName, EntryPoint = "llama_model_load_from_file")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint ModelLoadFromFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        LlamaModelParams modelParams);

    [LibraryImport(LibraryName, EntryPoint = "llama_model_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ModelFree(nint modelHandle);

    [DllImport(LibraryName, EntryPoint = "llama_init_from_model")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint InitFromModel(nint modelHandle, LlamaContextParams contextParams);

    [LibraryImport(LibraryName, EntryPoint = "llama_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ContextFree(nint contextHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_model_get_vocab")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ModelGetVocab(nint modelHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_model_n_embd")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ModelNEmbd(nint modelHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_model_n_embd_out")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ModelNEmbdOut(nint modelHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_tokenize", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Tokenize(
        nint vocabHandle,
        string text,
        int textLength,
        nint tokens,
        int maxTokens,
        [MarshalAs(UnmanagedType.I1)] bool addSpecial,
        [MarshalAs(UnmanagedType.I1)] bool parseSpecial);

    [DllImport(LibraryName, EntryPoint = "llama_batch_get_one")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern LlamaBatch BatchGetOne(nint tokens, int tokenCount);

    [DllImport(LibraryName, EntryPoint = "llama_decode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern int Decode(nint contextHandle, LlamaBatch batch);

    [DllImport(LibraryName, EntryPoint = "llama_sampler_chain_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static extern nint SamplerChainInit(LlamaSamplerChainParams samplerChainParams);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_chain_add")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SamplerChainAdd(nint chainSampler, nint sampler);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_init_top_k")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SamplerInitTopK(int topK);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_init_top_p")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SamplerInitTopP(float topP, nuint minKeep);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_init_temp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SamplerInitTemp(float temperature);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_init_penalties")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SamplerInitPenalties(int penaltyLastN, float penaltyRepeat, float penaltyFreq, float penaltyPresent);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_init_dist")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint SamplerInitDist(uint seed);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SamplerFree(nint samplerHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SamplerReset(nint samplerHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_sample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SamplerSample(nint samplerChain, nint contextHandle, int idx);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_accept")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SamplerAccept(nint samplerChain, int tokenId);

    [LibraryImport(LibraryName, EntryPoint = "llama_token_to_piece")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int TokenToPiece(
        nint vocabHandle,
        int tokenId,
        nint buffer,
        int length,
        int lstrip,
        [MarshalAs(UnmanagedType.I1)] bool special);

    [LibraryImport(LibraryName, EntryPoint = "llama_vocab_is_eog")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool TokenIsEog(nint vocabHandle, int tokenId);

    [LibraryImport(LibraryName, EntryPoint = "llama_get_embeddings")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint GetEmbeddings(nint contextHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_set_embeddings")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEmbeddings(nint contextHandle, [MarshalAs(UnmanagedType.I1)] bool embeddings);

    [LibraryImport(LibraryName, EntryPoint = "llama_get_embeddings_ith")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint GetEmbeddingsIth(nint contextHandle, int index);

    [LibraryImport(LibraryName, EntryPoint = "llama_get_embeddings_seq")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint GetEmbeddingsSeq(nint contextHandle, int sequenceId);

    [LibraryImport(LibraryName, EntryPoint = "llama_get_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint GetMemory(nint contextHandle);

    [LibraryImport(LibraryName, EntryPoint = "llama_memory_clear")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MemoryClear(nint memoryHandle, [MarshalAs(UnmanagedType.I1)] bool data);

    internal static void ClearMemory(nint contextHandle)
    {
        var memoryHandle = GetMemory(contextHandle);
        if (memoryHandle != nint.Zero)
        {
            MemoryClear(memoryHandle, data: true);
        }
    }

    internal static string? GetBackendRegName(nint backendRegHandle)
        => PtrToStringUtf8(BackendRegNameCore(backendRegHandle));

    internal static GgmlBackendDeviceProperties GetBackendDeviceProperties(nint deviceHandle)
    {
        BackendDeviceGetPropsCore(deviceHandle, out var nativeProperties);

        return new GgmlBackendDeviceProperties(
            PtrToStringUtf8(nativeProperties.name),
            PtrToStringUtf8(nativeProperties.description),
            (ulong)nativeProperties.memory_total,
            PtrToStringUtf8(nativeProperties.device_id),
            nativeProperties.type);
    }

    private static string? PtrToStringUtf8(nint value)
        => value == nint.Zero ? null : Marshal.PtrToStringUTF8(value);
}

internal enum GgmlBackendDeviceType
{
    Cpu = 0,
    Gpu = 1,
    IntegratedGpu = 2,
    Accelerator = 3
}

[StructLayout(LayoutKind.Sequential)]
internal struct GgmlBackendDeviceCapabilities
{
    [MarshalAs(UnmanagedType.I1)]
    public bool @async;

    [MarshalAs(UnmanagedType.I1)]
    public bool host_buffer;

    [MarshalAs(UnmanagedType.I1)]
    public bool buffer_from_host_ptr;

    [MarshalAs(UnmanagedType.I1)]
    public bool events;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GgmlBackendDevicePropertiesNative
{
    public nint name;
    public nint description;
    public nuint memory_free;
    public nuint memory_total;
    public GgmlBackendDeviceType type;
    public nint device_id;
    public GgmlBackendDeviceCapabilities caps;
}

internal readonly record struct GgmlBackendDeviceProperties(
    string? Name,
    string? Description,
    ulong MemoryTotalBytes,
    string? DeviceId,
    GgmlBackendDeviceType Type);

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaModelParams
{
    public nint devices;
    public nint tensor_buft_overrides;
    public int n_gpu_layers;
    public int split_mode;
    public int main_gpu;
    public nint tensor_split;
    public nint progress_callback;
    public nint progress_callback_user_data;
    public nint kv_overrides;

    [MarshalAs(UnmanagedType.I1)]
    public bool vocab_only;

    [MarshalAs(UnmanagedType.I1)]
    public bool use_mmap;

    [MarshalAs(UnmanagedType.I1)]
    public bool use_direct_io;

    [MarshalAs(UnmanagedType.I1)]
    public bool use_mlock;

    [MarshalAs(UnmanagedType.I1)]
    public bool check_tensors;

    [MarshalAs(UnmanagedType.I1)]
    public bool use_extra_bufts;

    [MarshalAs(UnmanagedType.I1)]
    public bool no_host;

    [MarshalAs(UnmanagedType.I1)]
    public bool no_alloc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaContextParams
{
    public uint n_ctx;
    public uint n_batch;
    public uint n_ubatch;
    public uint n_seq_max;
    public uint n_rs_seq;
    public uint n_outputs_max;
    public int n_threads;
    public int n_threads_batch;
    public int ctx_type;
    public int rope_scaling_type;
    public int pooling_type;
    public int attention_type;
    public int flash_attn_type;
    public float rope_freq_base;
    public float rope_freq_scale;
    public float yarn_ext_factor;
    public float yarn_attn_factor;
    public float yarn_beta_fast;
    public float yarn_beta_slow;
    public uint yarn_orig_ctx;
    public float defrag_thold;
    public nint cb_eval;
    public nint cb_eval_user_data;
    public int type_k;
    public int type_v;
    public nint abort_callback;
    public nint abort_callback_data;

    [MarshalAs(UnmanagedType.I1)]
    public bool embeddings;

    [MarshalAs(UnmanagedType.I1)]
    public bool offload_kqv;

    [MarshalAs(UnmanagedType.I1)]
    public bool no_perf;

    [MarshalAs(UnmanagedType.I1)]
    public bool op_offload;

    [MarshalAs(UnmanagedType.I1)]
    public bool swa_full;

    [MarshalAs(UnmanagedType.I1)]
    public bool kv_unified;

    public nint samplers;
    public nuint n_samplers;
    public nint ctx_other;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaSamplerChainParams
{
    [MarshalAs(UnmanagedType.I1)]
    public bool no_perf;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaBatch
{
    public int n_tokens;
    public nint token;
    public nint embd;
    public nint pos;
    public nint n_seq_id;
    public nint seq_id;
    public nint logits;
}
