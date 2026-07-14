namespace Tomur.Providers.Glm;

internal sealed class KvCache : IDisposable
{
    private readonly int[] validTokenCounts;
    private float[][]? layerBuffers;

    public KvCache(GlmModelConfiguration configuration, int contextSize)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (contextSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSize));
        }

        LayerCount = configuration.LayerCount;
        ContextSize = contextSize;
        CompressedSize = configuration.KeyValueLoraRank;
        RopeSize = configuration.QueryKeyRopeHeadSize;
        EntrySize = checked(CompressedSize + RopeSize);
        var layerElementCount = checked((long)contextSize * EntrySize);
        if (layerElementCount > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"One compressed KV cache layer requires {layerElementCount} elements and exceeds the managed array limit.");
        }

        var buffers = new float[LayerCount][];
        for (var layer = 0; layer < buffers.Length; layer++)
        {
            buffers[layer] = GC.AllocateUninitializedArray<float>(checked((int)layerElementCount));
        }

        layerBuffers = buffers;
        validTokenCounts = new int[LayerCount];
        ByteLength = checked(checked(layerElementCount * LayerCount) * sizeof(float));
    }

    public int LayerCount { get; }

    public int ContextSize { get; }

    public int CompressedSize { get; }

    public int RopeSize { get; }

    public int EntrySize { get; }

    public long ByteLength { get; }

    public int GetValidTokenCount(int layer)
    {
        ValidateLayer(layer);
        _ = GetLayerBuffers();
        return validTokenCounts[layer];
    }

    public ReadOnlySpan<float> GetCompressed(int layer, int tokenIndex)
    {
        var entry = GetEntry(layer, tokenIndex);
        return entry[..CompressedSize];
    }

    public ReadOnlySpan<float> GetRopeKey(int layer, int tokenIndex)
    {
        var entry = GetEntry(layer, tokenIndex);
        return entry.Slice(CompressedSize, RopeSize);
    }

    internal void EnsureCompatible(GlmModelConfiguration configuration, int contextLimit)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = GetLayerBuffers();
        if (LayerCount != configuration.LayerCount ||
            CompressedSize != configuration.KeyValueLoraRank ||
            RopeSize != configuration.QueryKeyRopeHeadSize)
        {
            throw new ArgumentException("Compressed KV cache dimensions do not match the model configuration.");
        }

        if (contextLimit > ContextSize)
        {
            throw new ArgumentException(
                $"Sequence context limit {contextLimit} exceeds KV cache capacity {ContextSize}.");
        }
    }

    internal void Append(
        int layer,
        ReadOnlySpan<float> compressed,
        ReadOnlySpan<float> ropeKey)
    {
        ValidateLayer(layer);
        var buffers = GetLayerBuffers();
        if (compressed.Length != CompressedSize)
        {
            throw new ArgumentException(
                $"Compressed KV entry must contain {CompressedSize} elements.",
                nameof(compressed));
        }

        if (ropeKey.Length != RopeSize)
        {
            throw new ArgumentException(
                $"RoPE key entry must contain {RopeSize} elements.",
                nameof(ropeKey));
        }

        EnsureFinite(compressed, nameof(compressed));
        EnsureFinite(ropeKey, nameof(ropeKey));

        var tokenIndex = validTokenCounts[layer];
        if (tokenIndex >= ContextSize)
        {
            throw new ContextLengthExceededException(tokenIndex, 1, ContextSize);
        }

        var entry = buffers[layer].AsSpan(checked(tokenIndex * EntrySize), EntrySize);
        compressed.CopyTo(entry);
        ropeKey.CopyTo(entry[CompressedSize..]);
        validTokenCounts[layer] = checked(tokenIndex + 1);
    }

    internal void RollbackLayer(int layer, int validTokenCount)
    {
        ValidateLayer(layer);
        var buffers = GetLayerBuffers();
        var current = validTokenCounts[layer];
        if ((uint)validTokenCount > (uint)current)
        {
            throw new ArgumentOutOfRangeException(nameof(validTokenCount));
        }

        if (validTokenCount == current)
        {
            return;
        }

        var offset = checked(validTokenCount * EntrySize);
        var length = checked((current - validTokenCount) * EntrySize);
        buffers[layer].AsSpan(offset, length).Clear();
        validTokenCounts[layer] = validTokenCount;
    }

    public void Dispose()
    {
        var buffers = Interlocked.Exchange(ref layerBuffers, null);
        if (buffers is null)
        {
            return;
        }

        foreach (var buffer in buffers)
        {
            buffer.AsSpan().Clear();
        }

        validTokenCounts.AsSpan().Clear();
    }

    private ReadOnlySpan<float> GetEntry(int layer, int tokenIndex)
    {
        ValidateLayer(layer);
        var buffers = GetLayerBuffers();
        if ((uint)tokenIndex >= (uint)validTokenCounts[layer])
        {
            throw new ArgumentOutOfRangeException(nameof(tokenIndex));
        }

        return buffers[layer].AsSpan(checked(tokenIndex * EntrySize), EntrySize);
    }

    private float[][] GetLayerBuffers()
    {
        ObjectDisposedException.ThrowIf(layerBuffers is null, this);
        return layerBuffers;
    }

    private void ValidateLayer(int layer)
    {
        if ((uint)layer >= (uint)LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
    }

    private static void EnsureFinite(ReadOnlySpan<float> values, string parameterName)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException(
                    $"{parameterName} value at index {index} must be finite.");
            }
        }
    }
}
