using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tomur.Providers.Glm;

internal sealed class TensorDataSource : IDisposable
{
    private const int ConversionBufferBytes = 64 * 1024;
    private const int MaximumReadBytes = 1024 * 1024;
    private readonly Dictionary<string, Shard> shards;
    private readonly IReadOnlyDictionary<string, TensorDescriptor> descriptors;
    private readonly StringComparer pathComparer;
    private bool disposed;

    public TensorDataSource(SafeTensorCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        descriptors = catalog.Items.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        shards = new Dictionary<string, Shard>(pathComparer);

        try
        {
            foreach (var path in descriptors.Values.Select(static item => item.ShardPath).Distinct(pathComparer))
            {
                var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.RandomAccess);
                try
                {
                    var shard = new Shard(path, handle, RandomAccess.GetLength(handle));
                    shards.Add(path, shard);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            foreach (var descriptor in descriptors.Values)
            {
                var shard = shards[descriptor.ShardPath];
                if (checked(descriptor.Offset + descriptor.PhysicalLength) > shard.Length)
                {
                    throw new EndOfStreamException(
                        $"Tensor '{descriptor.Name}' extends beyond the current length of shard '{descriptor.ShardPath}'.");
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int ShardCount => shards.Count;

    public byte[] ReadTensor(
        TensorDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.PhysicalLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Tensor '{descriptor.Name}' is too large for one managed byte buffer; read it in bounded slices.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)descriptor.PhysicalLength));
        ReadExactly(descriptor, 0, bytes, cancellationToken);
        return bytes;
    }

    public void ReadExactly(
        TensorDescriptor descriptor,
        long relativeOffset,
        Span<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ObjectDisposedException.ThrowIf(disposed, this);
        var shard = ValidateDescriptor(descriptor);
        if (relativeOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeOffset));
        }

        var relativeEnd = checked(relativeOffset + destination.Length);
        if (relativeEnd > descriptor.PhysicalLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"Read range {relativeOffset}+{destination.Length} exceeds tensor '{descriptor.Name}' length {descriptor.PhysicalLength}.");
        }

        ReadFileExactly(
            shard,
            destination,
            checked(descriptor.Offset + relativeOffset),
            descriptor.Name,
            cancellationToken);
    }

    public void ReadAdjacent(
        IReadOnlyList<TensorDescriptor> adjacent,
        Span<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adjacent);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (adjacent.Count == 0)
        {
            throw new ArgumentException("At least one tensor descriptor is required.", nameof(adjacent));
        }

        var first = adjacent[0] ?? throw new ArgumentException("Tensor descriptor cannot be null.", nameof(adjacent));
        var shard = ValidateDescriptor(first);
        var expectedOffset = first.Offset;
        long totalLength = 0;
        foreach (var descriptor in adjacent)
        {
            if (descriptor is null)
            {
                throw new ArgumentException("Tensor descriptor cannot be null.", nameof(adjacent));
            }

            var currentShard = ValidateDescriptor(descriptor);
            if (!pathComparer.Equals(currentShard.Path, shard.Path) || descriptor.Offset != expectedOffset)
            {
                throw new InvalidDataException(
                    $"Tensor '{descriptor.Name}' is not physically adjacent to the preceding tensor in shard '{shard.Path}'.");
            }

            totalLength = checked(totalLength + descriptor.PhysicalLength);
            expectedOffset = checked(descriptor.Offset + descriptor.PhysicalLength);
        }

        if (totalLength != destination.Length)
        {
            throw new ArgumentException(
                $"Destination must contain exactly {totalLength} bytes for the adjacent tensor read.",
                nameof(destination));
        }

        ReadFileExactly(
            shard,
            destination,
            first.Offset,
            string.Join(", ", adjacent.Select(static item => item.Name)),
            cancellationToken);
    }

    public ResidentTensor<float> LoadFloat32(
        TensorDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var resident = new ResidentTensor<float>(descriptor);
        try
        {
            ReadFloat32(descriptor, resident.Span, cancellationToken);
            return resident;
        }
        catch
        {
            resident.Dispose();
            throw;
        }
    }

    public void ReadFloat32(
        TensorDescriptor descriptor,
        Span<float> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ObjectDisposedException.ThrowIf(disposed, this);
        _ = ValidateDescriptor(descriptor);
        if (descriptor.DataType is not (
                TensorDataType.Float32 or TensorDataType.Float16 or TensorDataType.BFloat16))
        {
            throw new InvalidDataException(
                $"Tensor '{descriptor.Name}' uses {descriptor.DataTypeName}; only F32, F16 and BF16 can be converted to F32.");
        }

        if (descriptor.ElementCount != destination.Length)
        {
            throw new ArgumentException(
                $"Destination length must equal tensor '{descriptor.Name}' element count {descriptor.ElementCount}.",
                nameof(destination));
        }

        var elementSize = descriptor.DataType.GetElementSize();
        var expectedBytes = checked(descriptor.ElementCount * elementSize);
        if (descriptor.PhysicalLength != expectedBytes)
        {
            throw new InvalidDataException(
                $"Tensor '{descriptor.Name}' physical length does not match its logical shape and dtype.");
        }

        var elementsPerChunk = ConversionBufferBytes / elementSize;
        if (descriptor.DataType == TensorDataType.Float32 && BitConverter.IsLittleEndian)
        {
            for (var elementOffset = 0; elementOffset < destination.Length; elementOffset += elementsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elementCount = Math.Min(elementsPerChunk, destination.Length - elementOffset);
                var output = destination.Slice(elementOffset, elementCount);
                ReadExactly(
                    descriptor,
                    checked((long)elementOffset * elementSize),
                    MemoryMarshal.AsBytes(output),
                    cancellationToken);
            }

            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ConversionBufferBytes);
        try
        {
            for (var elementOffset = 0; elementOffset < destination.Length; elementOffset += elementsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elementCount = Math.Min(elementsPerChunk, destination.Length - elementOffset);
                var bytes = buffer.AsSpan(0, checked(elementCount * elementSize));
                ReadExactly(
                    descriptor,
                    checked((long)elementOffset * elementSize),
                    bytes,
                    cancellationToken);
                ConvertToFloat32(descriptor.DataType, bytes, destination.Slice(elementOffset, elementCount));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var shard in shards.Values)
        {
            shard.Handle.Dispose();
        }

        shards.Clear();
    }

    private Shard ValidateDescriptor(TensorDescriptor descriptor)
    {
        if (!descriptors.TryGetValue(descriptor.Name, out var registered) ||
            registered.DataType != descriptor.DataType ||
            registered.Offset != descriptor.Offset ||
            registered.PhysicalLength != descriptor.PhysicalLength ||
            !pathComparer.Equals(registered.ShardPath, descriptor.ShardPath))
        {
            throw new ArgumentException(
                $"Tensor descriptor '{descriptor.Name}' does not belong to this data source.",
                nameof(descriptor));
        }

        if (!shards.TryGetValue(descriptor.ShardPath, out var shard))
        {
            throw new InvalidDataException(
                $"Tensor shard is not open for '{descriptor.Name}': {descriptor.ShardPath}");
        }

        return shard;
    }

    private static void ReadFileExactly(
        Shard shard,
        Span<byte> destination,
        long fileOffset,
        string label,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Math.Min(MaximumReadBytes, destination.Length - read);
            var count = RandomAccess.Read(
                shard.Handle,
                destination.Slice(read, remaining),
                checked(fileOffset + read));
            if (count == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of shard '{shard.Path}' while reading tensor data for {label}.");
            }

            read += count;
        }
    }

    private static void ConvertToFloat32(
        TensorDataType dataType,
        ReadOnlySpan<byte> source,
        Span<float> destination)
    {
        var elementSize = dataType.GetElementSize();
        for (var index = 0; index < destination.Length; index++)
        {
            var bytes = source.Slice(index * elementSize, elementSize);
            destination[index] = dataType switch
            {
                TensorDataType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                TensorDataType.Float16 => (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
                TensorDataType.BFloat16 => BitConverter.UInt32BitsToSingle(
                    (uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes) << 16),
                _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
            };
        }
    }

    private sealed record Shard(string Path, SafeFileHandle Handle, long Length);
}
