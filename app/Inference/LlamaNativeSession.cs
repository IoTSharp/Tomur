using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tomur.Inference;

internal sealed class LlamaNativeSession : IDisposable
{
    private const int PoolingTypeNone = 0;
    private const int PoolingTypeMean = 1;

    private readonly object gate = new();
    private readonly string modelId;
    private readonly string modelPath;
    private readonly int contextSize;
    private readonly bool embeddings;
    private readonly LlamaModelHandle modelHandle;
    private readonly LlamaContextHandle contextHandle;
    private readonly nint vocabHandle;
    private bool disposed;
    private long requestCount;
    private long promptTokens;
    private long completionTokens;

    public LlamaNativeSession(string modelId, string modelPath, int contextSize, int gpuLayers, bool embeddings)
    {
        this.modelId = modelId;
        this.modelPath = modelPath;
        this.contextSize = Math.Max(512, contextSize);
        this.embeddings = embeddings;

        var modelParams = LlamaNativeMethods.ModelDefaultParams();
        modelParams.n_gpu_layers = Math.Max(0, gpuLayers);
        var rawModelHandle = LlamaNativeMethods.ModelLoadFromFile(modelPath, modelParams);
        if (rawModelHandle == nint.Zero)
        {
            throw new InferenceException(
                "model_load_failed",
                "The local llama model could not be loaded.",
                [
                    "Verify the GGUF file is complete and not corrupted.",
                    "Run tomur pull with --force if the model was installed by Tomur.",
                    "Run tomur doctor to inspect native runtime status."
                ]);
        }

        modelHandle = new LlamaModelHandle(rawModelHandle);

        var threads = Math.Max(1, Environment.ProcessorCount);
        var contextParams = LlamaNativeMethods.ContextDefaultParams();
        contextParams.n_ctx = (uint)this.contextSize;
        contextParams.n_batch = (uint)this.contextSize;
        contextParams.n_ubatch = (uint)this.contextSize;
        contextParams.n_seq_max = 1;
        contextParams.n_threads = threads;
        contextParams.n_threads_batch = threads;
        contextParams.embeddings = embeddings;
        contextParams.pooling_type = embeddings ? PoolingTypeMean : PoolingTypeNone;
        contextParams.offload_kqv = gpuLayers > 0;
        contextParams.op_offload = gpuLayers > 0;

        var rawContextHandle = LlamaNativeMethods.InitFromModel(rawModelHandle, contextParams);
        if (rawContextHandle == nint.Zero)
        {
            modelHandle.Dispose();
            throw new InferenceException(
                "context_init_failed",
                "The local llama context could not be initialized.",
                [
                    "Lower the context size or unload other memory-heavy applications.",
                    "Use a smaller quantized model on low-memory machines.",
                    "Run tomur doctor to inspect local memory and runtime state."
                ]);
        }

        contextHandle = new LlamaContextHandle(rawContextHandle);
        LlamaNativeMethods.SetEmbeddings(rawContextHandle, embeddings);
        vocabHandle = LlamaNativeMethods.ModelGetVocab(rawModelHandle);
        if (vocabHandle == nint.Zero)
        {
            Dispose();
            throw new InferenceException(
                "vocab_unavailable",
                "The loaded model did not expose a llama vocabulary.",
                ["Use a llama.cpp-compatible GGUF text or embedding model."]);
        }

        LoadedAt = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset LoadedAt { get; }

    public CompletionResult Generate(string prompt, CompletionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InferenceException(
                "empty_prompt",
                "The prompt is empty after normalization.",
                ["Provide at least one non-empty user message or prompt string."]);
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            var tokens = Tokenize(prompt, addSpecial: true, parseSpecial: true);
            if (tokens.Length == 0)
            {
                throw new InferenceException(
                    "tokenize_failed",
                    "The prompt could not be tokenized by the loaded model.",
                    ["Check that the model file is compatible with llama.cpp."]);
            }

            if (tokens.Length >= contextSize)
            {
                throw new InferenceException(
                    "context_length_exceeded",
                    $"The request uses {tokens.Length} prompt tokens, which exceeds the configured context size {contextSize}.",
                    ["Reduce the prompt or start Tomur with a larger context configuration when available."]);
            }

            using var sampler = CreateSampler(options);
            LlamaNativeMethods.ClearMemory(contextHandle.DangerousGetHandle());
            LlamaNativeMethods.SamplerReset(sampler.DangerousGetHandle());

            var text = RunDecodeLoop(tokens, sampler.DangerousGetHandle(), options, cancellationToken, out var generatedTokenCount);
            stopwatch.Stop();

            Interlocked.Increment(ref requestCount);
            Interlocked.Add(ref promptTokens, tokens.Length);
            Interlocked.Add(ref completionTokens, generatedTokenCount);

            return new CompletionResult(
                text,
                new TokenUsage(tokens.Length, generatedTokenCount, tokens.Length + generatedTokenCount),
                stopwatch.Elapsed,
                [
                    $"model-id: {modelId}",
                    $"model-path: {modelPath}",
                    $"context-size: {contextSize}",
                    $"prompt-tokens: {tokens.Length}",
                    $"completion-tokens: {generatedTokenCount}"
                ]);
        }
    }

    public EmbeddingResult Embed(string input, CompletionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InferenceException(
                "empty_embedding_input",
                "The embedding input is empty after normalization.",
                ["Provide at least one non-empty string in the input field."]);
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            var tokens = Tokenize(input, addSpecial: true, parseSpecial: true);
            if (tokens.Length == 0)
            {
                throw new InferenceException(
                    "tokenize_failed",
                    "The embedding input could not be tokenized by the loaded model.",
                    ["Check that the model file is compatible with llama.cpp."]);
            }

            if (tokens.Length >= contextSize)
            {
                throw new InferenceException(
                    "context_length_exceeded",
                    $"The embedding input uses {tokens.Length} tokens, which exceeds the configured context size {contextSize}.",
                    ["Reduce the embedding input or choose a larger context configuration when available."]);
            }

            LlamaNativeMethods.ClearMemory(contextHandle.DangerousGetHandle());
            using var batch = EmbeddingBatch.Create(tokens);
            var decodeResult = LlamaNativeMethods.Decode(contextHandle.DangerousGetHandle(), batch.Batch);
            if (decodeResult != 0)
            {
                throw new InferenceException(
                    "embedding_decode_failed",
                    $"The embedding decode step failed with native return code {decodeResult}.",
                    ["Use an embedding-capable GGUF model for /v1/embeddings."]);
            }

            var embeddingPtr = ResolveEmbeddingPointer(tokens.Length);
            var embeddingSize = ResolveEmbeddingSize();
            if (embeddingPtr == nint.Zero || embeddingSize <= 0)
            {
                throw new InferenceException(
                    "embedding_unavailable",
                    "The loaded model did not return an embedding vector.",
                    ["Use an embedding-capable GGUF model for /v1/embeddings."]);
            }

            var vector = new float[embeddingSize];
            Marshal.Copy(embeddingPtr, vector, 0, vector.Length);
            stopwatch.Stop();

            Interlocked.Increment(ref requestCount);
            Interlocked.Add(ref promptTokens, tokens.Length);

            return new EmbeddingResult(
                vector,
                new TokenUsage(tokens.Length, 0, tokens.Length),
                stopwatch.Elapsed,
                [
                    $"model-id: {modelId}",
                    $"model-path: {modelPath}",
                    $"context-size: {contextSize}",
                    $"input-tokens: {tokens.Length}",
                    $"embedding-size: {vector.Length}"
                ]);
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        return new SessionSnapshot(
            Loaded: true,
            ModelId: modelId,
            ModelPath: modelPath,
            Mode: embeddings ? "embeddings" : "generation",
            LoadedAt: LoadedAt,
            RequestCount: Interlocked.Read(ref requestCount),
            PromptTokens: Interlocked.Read(ref promptTokens),
            CompletionTokens: Interlocked.Read(ref completionTokens),
            Diagnostics:
            [
                $"context-size: {contextSize}",
                $"mode: {(embeddings ? "embeddings" : "generation")}",
                $"model-path: {modelPath}"
            ]);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            contextHandle?.Dispose();
            modelHandle?.Dispose();
            disposed = true;
        }
    }

    private int[] Tokenize(string text, bool addSpecial, bool parseSpecial)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var buffer = new int[Math.Max(16, byteCount + 32)];
        var count = TokenizeCore(text, byteCount, buffer, addSpecial, parseSpecial);
        if (count < 0)
        {
            buffer = new int[-count];
            count = TokenizeCore(text, byteCount, buffer, addSpecial, parseSpecial);
        }

        return count <= 0 ? [] : buffer.AsSpan(0, count).ToArray();
    }

    private int TokenizeCore(string text, int byteCount, int[] buffer, bool addSpecial, bool parseSpecial)
    {
        unsafe
        {
            fixed (int* tokenPointer = buffer)
            {
                return LlamaNativeMethods.Tokenize(
                    vocabHandle,
                    text,
                    byteCount,
                    (nint)tokenPointer,
                    buffer.Length,
                    addSpecial,
                    parseSpecial);
            }
        }
    }

    private string RunDecodeLoop(
        int[] promptTokens,
        nint samplerHandle,
        CompletionOptions options,
        CancellationToken cancellationToken,
        out int generatedTokenCount)
    {
        unsafe
        {
            fixed (int* tokenPointer = promptTokens)
            {
                var prefill = LlamaNativeMethods.BatchGetOne((nint)tokenPointer, promptTokens.Length);
                var prefillResult = LlamaNativeMethods.Decode(contextHandle.DangerousGetHandle(), prefill);
                if (prefillResult != 0)
                {
                    throw new InferenceException(
                        "prompt_decode_failed",
                        $"The prompt decode step failed with native return code {prefillResult}.",
                        ["Reduce the prompt length or use a different GGUF model."]);
                }
            }
        }

        var output = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        Span<char> charBuffer = stackalloc char[512];
        var emittedTokenCount = 0;

        for (var generated = 0; generated < Math.Max(1, options.MaxOutputTokens); generated++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = LlamaNativeMethods.SamplerSample(samplerHandle, contextHandle.DangerousGetHandle(), -1);
            if (LlamaNativeMethods.TokenIsEog(vocabHandle, token))
            {
                break;
            }

            LlamaNativeMethods.SamplerAccept(samplerHandle, token);
            emittedTokenCount++;
            var piece = TokenToPiece(token);
            if (piece.Length > 0)
            {
                AppendDecodedText(piece, decoder, charBuffer, output);
            }

            if (TryFindStop(output, options.StopSequences, out var stopIndex))
            {
                generatedTokenCount = emittedTokenCount;
                return output.ToString(0, stopIndex);
            }

            unsafe
            {
                var nextToken = token;
                var nextBatch = LlamaNativeMethods.BatchGetOne((nint)(&nextToken), 1);
                var decodeResult = LlamaNativeMethods.Decode(contextHandle.DangerousGetHandle(), nextBatch);
                if (decodeResult != 0)
                {
                    throw new InferenceException(
                        "generation_decode_failed",
                        $"The generation decode step failed with native return code {decodeResult}.",
                        ["Try a lower max_tokens value or reload the model."]);
                }
            }
        }

        AppendDecodedText([], decoder, charBuffer, output, flush: true);

        if (TryFindStop(output, options.StopSequences, out var finalStopIndex))
        {
            generatedTokenCount = emittedTokenCount;
            return output.ToString(0, finalStopIndex);
        }

        generatedTokenCount = emittedTokenCount;
        return output.ToString();
    }

    private LlamaSamplerHandle CreateSampler(CompletionOptions options)
    {
        var chainParams = LlamaNativeMethods.SamplerChainDefaultParams();
        var chainHandle = LlamaNativeMethods.SamplerChainInit(chainParams);
        if (chainHandle == nint.Zero)
        {
            throw new InferenceException(
                "sampler_init_failed",
                "The llama sampler chain could not be initialized.",
                ["Run tomur doctor to inspect native runtime status."]);
        }

        try
        {
            var penaltyLastTokens = options.PenaltyLastTokens < -1 ? -1 : options.PenaltyLastTokens;
            if (penaltyLastTokens != 0 &&
                (Math.Abs(options.RepeatPenalty - 1.0f) > 0.0001f ||
                 Math.Abs(options.FrequencyPenalty) > 0.0001f ||
                 Math.Abs(options.PresencePenalty) > 0.0001f))
            {
                AddSampler(
                    chainHandle,
                    LlamaNativeMethods.SamplerInitPenalties(
                        penaltyLastTokens,
                        Math.Max(0.01f, options.RepeatPenalty),
                        NormalizePenalty(options.FrequencyPenalty),
                        NormalizePenalty(options.PresencePenalty)));
            }

            AddSampler(chainHandle, LlamaNativeMethods.SamplerInitTopK(Math.Max(1, options.TopK)));
            AddSampler(chainHandle, LlamaNativeMethods.SamplerInitTopP(Math.Clamp(options.TopP, 0.01f, 1.0f), 1));
            AddSampler(chainHandle, LlamaNativeMethods.SamplerInitTemp(options.Temperature <= 0.0f ? 0.01f : options.Temperature));
            AddSampler(chainHandle, LlamaNativeMethods.SamplerInitDist(ResolveSeed(options.Seed)));

            return new LlamaSamplerHandle(chainHandle);
        }
        catch
        {
            LlamaNativeMethods.SamplerFree(chainHandle);
            throw;
        }
    }

    private byte[] TokenToPiece(int token)
    {
        Span<byte> stackBuffer = stackalloc byte[512];
        var written = TokenToPieceCore(token, stackBuffer);
        if (written == 0)
        {
            return [];
        }

        if (written > 0)
        {
            return stackBuffer[..written].ToArray();
        }

        if (written == int.MinValue)
        {
            return [];
        }

        var rented = new byte[-written];
        var retryWritten = TokenToPieceCore(token, rented);
        return retryWritten > 0 ? rented.AsSpan(0, retryWritten).ToArray() : [];
    }

    private int TokenToPieceCore(int token, Span<byte> buffer)
    {
        unsafe
        {
            fixed (byte* bufferPointer = buffer)
            {
                return LlamaNativeMethods.TokenToPiece(vocabHandle, token, (nint)bufferPointer, buffer.Length, 0, false);
            }
        }
    }

    private nint ResolveEmbeddingPointer(int tokenCount)
    {
        var pooled = LlamaNativeMethods.GetEmbeddingsSeq(contextHandle.DangerousGetHandle(), 0);
        if (pooled != nint.Zero)
        {
            return pooled;
        }

        var lastToken = LlamaNativeMethods.GetEmbeddingsIth(contextHandle.DangerousGetHandle(), tokenCount - 1);
        return lastToken != nint.Zero
            ? lastToken
            : LlamaNativeMethods.GetEmbeddings(contextHandle.DangerousGetHandle());
    }

    private int ResolveEmbeddingSize()
    {
        var outputSize = LlamaNativeMethods.ModelNEmbdOut(modelHandle.DangerousGetHandle());
        return outputSize > 0
            ? outputSize
            : LlamaNativeMethods.ModelNEmbd(modelHandle.DangerousGetHandle());
    }

    private static void AddSampler(nint chainHandle, nint samplerHandle)
    {
        if (samplerHandle != nint.Zero)
        {
            LlamaNativeMethods.SamplerChainAdd(chainHandle, samplerHandle);
        }
    }

    private static uint ResolveSeed(int seed)
        => seed < 0 ? (uint)Random.Shared.Next(1, int.MaxValue) : (uint)seed;

    private static float NormalizePenalty(float value)
        => float.IsFinite(value) ? value : 0.0f;

    private static void AppendDecodedText(
        ReadOnlySpan<byte> bytes,
        Decoder decoder,
        Span<char> charBuffer,
        StringBuilder target,
        bool flush = false)
    {
        do
        {
            decoder.Convert(bytes, charBuffer, flush, out var bytesUsed, out var charsUsed, out var completed);
            if (charsUsed > 0)
            {
                target.Append(charBuffer[..charsUsed]);
            }

            bytes = bytes[bytesUsed..];
            if (completed)
            {
                break;
            }
        }
        while (!bytes.IsEmpty || flush);
    }

    private static bool TryFindStop(StringBuilder builder, IReadOnlyList<string> stopSequences, out int stopIndex)
    {
        stopIndex = -1;
        if (stopSequences.Count == 0)
        {
            return false;
        }

        var text = builder.ToString();
        foreach (var stop in stopSequences)
        {
            if (string.IsNullOrEmpty(stop))
            {
                continue;
            }

            var index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (stopIndex < 0 || index < stopIndex))
            {
                stopIndex = index;
            }
        }

        return stopIndex >= 0;
    }

    private sealed class EmbeddingBatch : IDisposable
    {
        private readonly nint tokenBuffer;
        private readonly nint positionBuffer;
        private readonly nint sequenceCountsBuffer;
        private readonly nint sequenceIdsBuffer;
        private readonly nint sequencePointersBuffer;
        private readonly nint logitsBuffer;

        private EmbeddingBatch(
            nint tokenBuffer,
            nint positionBuffer,
            nint sequenceCountsBuffer,
            nint sequenceIdsBuffer,
            nint sequencePointersBuffer,
            nint logitsBuffer,
            int tokenCount)
        {
            this.tokenBuffer = tokenBuffer;
            this.positionBuffer = positionBuffer;
            this.sequenceCountsBuffer = sequenceCountsBuffer;
            this.sequenceIdsBuffer = sequenceIdsBuffer;
            this.sequencePointersBuffer = sequencePointersBuffer;
            this.logitsBuffer = logitsBuffer;

            Batch = new LlamaBatch
            {
                n_tokens = tokenCount,
                token = tokenBuffer,
                embd = nint.Zero,
                pos = positionBuffer,
                n_seq_id = sequenceCountsBuffer,
                seq_id = sequencePointersBuffer,
                logits = logitsBuffer
            };
        }

        public LlamaBatch Batch { get; }

        public static EmbeddingBatch Create(ReadOnlySpan<int> tokens)
        {
            var tokenBuffer = Marshal.AllocHGlobal(sizeof(int) * tokens.Length);
            var positionBuffer = Marshal.AllocHGlobal(sizeof(int) * tokens.Length);
            var sequenceCountsBuffer = Marshal.AllocHGlobal(sizeof(int) * tokens.Length);
            var sequenceIdsBuffer = Marshal.AllocHGlobal(sizeof(int) * tokens.Length);
            var sequencePointersBuffer = Marshal.AllocHGlobal(IntPtr.Size * tokens.Length);
            var logitsBuffer = Marshal.AllocHGlobal(tokens.Length);

            unsafe
            {
                var tokenSpan = new Span<int>((void*)tokenBuffer, tokens.Length);
                var positionSpan = new Span<int>((void*)positionBuffer, tokens.Length);
                var sequenceCountSpan = new Span<int>((void*)sequenceCountsBuffer, tokens.Length);
                var sequenceIdSpan = new Span<int>((void*)sequenceIdsBuffer, tokens.Length);
                var sequencePointerSpan = new Span<nint>((void*)sequencePointersBuffer, tokens.Length);
                var logitsSpan = new Span<byte>((void*)logitsBuffer, tokens.Length);

                tokens.CopyTo(tokenSpan);
                for (var index = 0; index < tokens.Length; index++)
                {
                    positionSpan[index] = index;
                    sequenceCountSpan[index] = 1;
                    sequenceIdSpan[index] = 0;
                    sequencePointerSpan[index] = sequenceIdsBuffer + (index * sizeof(int));
                    logitsSpan[index] = 1;
                }
            }

            return new EmbeddingBatch(
                tokenBuffer,
                positionBuffer,
                sequenceCountsBuffer,
                sequenceIdsBuffer,
                sequencePointersBuffer,
                logitsBuffer,
                tokens.Length);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(logitsBuffer);
            Marshal.FreeHGlobal(sequencePointersBuffer);
            Marshal.FreeHGlobal(sequenceIdsBuffer);
            Marshal.FreeHGlobal(sequenceCountsBuffer);
            Marshal.FreeHGlobal(positionBuffer);
            Marshal.FreeHGlobal(tokenBuffer);
        }
    }
}
