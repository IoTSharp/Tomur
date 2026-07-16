using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tomur.Providers.Glm;

internal sealed class KvCache : IDisposable
{
    private static readonly byte[] SnapshotMagic = "TOMURKV1"u8.ToArray();
    private const int SnapshotVersion = 1;
    private const int MaximumIdentityBytes = 4096;
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

    public void Save(
        Stream destination,
        string modelIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("KV snapshot destination must be writable.", nameof(destination));
        }

        var identity = Encoding.UTF8.GetBytes(modelIdentity);
        if (identity.Length > MaximumIdentityBytes)
        {
            throw new ArgumentException(
                $"KV snapshot model identity cannot exceed {MaximumIdentityBytes} UTF-8 bytes.",
                nameof(modelIdentity));
        }

        var buffers = GetLayerBuffers();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        WriteHashed(destination, hash, SnapshotMagic);
        WriteInt32(destination, hash, SnapshotVersion);
        WriteInt32(destination, hash, identity.Length);
        WriteHashed(destination, hash, identity);
        WriteInt32(destination, hash, LayerCount);
        WriteInt32(destination, hash, ContextSize);
        WriteInt32(destination, hash, CompressedSize);
        WriteInt32(destination, hash, RopeSize);
        for (var layer = 0; layer < LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validCount = validTokenCounts[layer];
            WriteInt32(destination, hash, validCount);
            WriteFloats(
                destination,
                hash,
                buffers[layer].AsSpan(0, checked(validCount * EntrySize)),
                cancellationToken);
        }

        destination.Write(hash.GetHashAndReset());
    }

    public KvCacheRestoreResult Restore(
        Stream source,
        string expectedModelIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelIdentity);
        if (!source.CanRead)
        {
            throw new ArgumentException("KV snapshot source must be readable.", nameof(source));
        }

        _ = GetLayerBuffers();
        if (validTokenCounts.Any(static count => count != 0))
        {
            throw new InvalidOperationException("KV snapshots can only be restored into an empty cache.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var magic = ReadHashed(source, hash, SnapshotMagic.Length, cancellationToken);
        if (!magic.AsSpan().SequenceEqual(SnapshotMagic))
        {
            throw new InvalidDataException("KV snapshot magic is invalid.");
        }

        var version = ReadInt32(source, hash, cancellationToken);
        if (version != SnapshotVersion)
        {
            throw new InvalidDataException(
                $"KV snapshot version {version} is not supported; expected {SnapshotVersion}.");
        }

        var identityLength = ReadInt32(source, hash, cancellationToken);
        if (identityLength <= 0 || identityLength > MaximumIdentityBytes)
        {
            throw new InvalidDataException("KV snapshot model identity length is invalid.");
        }

        var identity = Encoding.UTF8.GetString(
            ReadHashed(source, hash, identityLength, cancellationToken));
        if (!string.Equals(identity, expectedModelIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"KV snapshot belongs to model '{identity}', not '{expectedModelIdentity}'.");
        }

        var layerCount = ReadInt32(source, hash, cancellationToken);
        var contextSize = ReadInt32(source, hash, cancellationToken);
        var compressedSize = ReadInt32(source, hash, cancellationToken);
        var ropeSize = ReadInt32(source, hash, cancellationToken);
        if (layerCount != LayerCount || contextSize > ContextSize || contextSize <= 0 ||
            compressedSize != CompressedSize || ropeSize != RopeSize)
        {
            throw new InvalidDataException(
                "KV snapshot dimensions are not compatible with the current model context.");
        }

        var restoredBuffers = new float[LayerCount][];
        var restoredCounts = new int[LayerCount];
        for (var layer = 0; layer < LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validCount = ReadInt32(source, hash, cancellationToken);
            if (validCount < 0 || validCount > contextSize)
            {
                throw new InvalidDataException(
                    $"KV snapshot layer {layer} has invalid token count {validCount}.");
            }

            restoredCounts[layer] = validCount;
            restoredBuffers[layer] = ReadFloats(
                source,
                hash,
                checked(validCount * EntrySize),
                cancellationToken);
        }

        var expectedHash = hash.GetHashAndReset();
        var storedHash = ReadExactly(source, expectedHash.Length, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, storedHash))
        {
            throw new InvalidDataException("KV snapshot checksum validation failed.");
        }

        if (restoredCounts.Min() != restoredCounts.Max())
        {
            throw new InvalidDataException(
                "KV snapshot layers do not share one sequence position and cannot be resumed.");
        }

        var buffers = GetLayerBuffers();
        for (var layer = 0; layer < LayerCount; layer++)
        {
            restoredBuffers[layer].CopyTo(buffers[layer], 0);
            validTokenCounts[layer] = restoredCounts[layer];
        }

        return new KvCacheRestoreResult(identity, restoredCounts.Min(), restoredCounts.Max());
    }

    public KvCache CreateIsolatedCopy(GlmModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureCompatible(configuration, ContextSize);
        var clone = new KvCache(configuration, ContextSize);
        try
        {
            var source = GetLayerBuffers();
            var destination = clone.GetLayerBuffers();
            for (var layer = 0; layer < LayerCount; layer++)
            {
                var validCount = validTokenCounts[layer];
                source[layer].AsSpan(0, checked(validCount * EntrySize))
                    .CopyTo(destination[layer]);
                clone.validTokenCounts[layer] = validCount;
            }

            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

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

    private static void WriteInt32(Stream stream, IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        WriteHashed(stream, hash, bytes);
    }

    private static int ReadInt32(
        Stream stream,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        var bytes = ReadHashed(stream, hash, sizeof(int), cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void WriteFloats(
        Stream stream,
        IncrementalHash hash,
        ReadOnlySpan<float> values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BitConverter.IsLittleEndian)
        {
            WriteHashed(stream, hash, MemoryMarshal.AsBytes(values));
            return;
        }

        Span<byte> bytes = stackalloc byte[sizeof(float)];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            WriteHashed(stream, hash, bytes);
        }
    }

    private static float[] ReadFloats(
        Stream stream,
        IncrementalHash hash,
        int count,
        CancellationToken cancellationToken)
    {
        var values = GC.AllocateUninitializedArray<float>(count);
        if (count == 0)
        {
            return values;
        }

        if (BitConverter.IsLittleEndian)
        {
            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            ReadExactly(source: stream, destination: bytes, cancellationToken: cancellationToken);
            hash.AppendData(bytes);
        }
        else
        {
            Span<byte> bytes = stackalloc byte[sizeof(float)];
            for (var index = 0; index < count; index++)
            {
                ReadExactly(stream, bytes, cancellationToken);
                hash.AppendData(bytes);
                values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes);
            }
        }

        EnsureFinite(values, "restored KV cache");
        return values;
    }

    private static void WriteHashed(Stream stream, IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        stream.Write(bytes);
        hash.AppendData(bytes);
    }

    private static byte[] ReadHashed(
        Stream stream,
        IncrementalHash hash,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = ReadExactly(stream, length, cancellationToken);
        hash.AppendData(bytes);
        return bytes;
    }

    private static byte[] ReadExactly(
        Stream source,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        ReadExactly(source, bytes, cancellationToken);
        return bytes;
    }

    private static void ReadExactly(
        Stream source,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source.Read(destination[read..]);
            if (count == 0)
            {
                throw new EndOfStreamException("Unexpected end of KV snapshot.");
            }

            read += count;
        }
    }
}

internal sealed record KvCacheRestoreResult(
    string ModelIdentity,
    int MinimumTokenCount,
    int MaximumTokenCount);
