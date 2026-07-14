using System.Buffers.Binary;
using System.Text.Json;
using Tomur.Providers.Glm;

namespace Tomur.Providers.M3.Tests;

public sealed class TensorStorageTests
{
    [Fact]
    public void RandomSlicesWholeTensorsAndAdjacentRangesMatchShardBytes()
    {
        using var directory = new TemporaryDirectory();
        var firstPayload = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
        var secondPayload = new byte[] { 64, 65, 66, 67 };
        var path = SafeTensorTestFile.Write(
            directory.Path,
            new TestTensor("first", "U8", [64], firstPayload),
            new TestTensor("second", "U8", [4], secondPayload));
        var catalog = SafeTensorCatalog.Read([path]);
        var first = catalog.GetRequired("first");
        var second = catalog.GetRequired("second");
        using var source = new TensorDataSource(catalog);

        var random = new Random(0x5eed);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var offset = random.Next(firstPayload.Length);
            var length = random.Next(1, firstPayload.Length - offset + 1);
            var slice = new byte[length];
            source.ReadExactly(first, offset, slice);
            Assert.Equal(firstPayload.AsSpan(offset, length).ToArray(), slice);
        }

        var adjacent = new byte[firstPayload.Length + secondPayload.Length];
        source.ReadAdjacent([first, second], adjacent);

        Assert.Equal(firstPayload, source.ReadTensor(first));
        Assert.Equal(firstPayload.Concat(secondPayload).ToArray(), adjacent);
        Assert.Equal(2, catalog.Count);
        Assert.Equal(68L, catalog.TotalPayloadBytes);
    }

    [Fact]
    public void Float32Float16AndBFloat16LoadIntoResidentFloatStorage()
    {
        using var directory = new TemporaryDirectory();
        var expected = new[] { 1.0f, -2.5f, 0.5f, 8.0f };
        var path = SafeTensorTestFile.Write(
            directory.Path,
            new TestTensor("f32", "F32", [4], SafeTensorTestFile.Float32(expected)),
            new TestTensor("f16", "F16", [4], SafeTensorTestFile.Float16(expected)),
            new TestTensor("bf16", "BF16", [4], SafeTensorTestFile.BFloat16(expected)));
        var catalog = SafeTensorCatalog.Read([path]);
        using var source = new TensorDataSource(catalog);
        using var f32 = source.LoadFloat32(catalog.GetRequired("f32"));
        using var f16 = source.LoadFloat32(catalog.GetRequired("f16"));
        using var bf16 = source.LoadFloat32(catalog.GetRequired("bf16"));

        Assert.Equal(expected, f32.ReadOnlySpan.ToArray());
        Assert.Equal(expected, f16.ReadOnlySpan.ToArray());
        Assert.Equal(expected, bf16.ReadOnlySpan.ToArray());
        Assert.Equal(TensorDataType.Float16, f16.Descriptor.DataType);
        Assert.Equal(4, f16.Length);
    }

    [Fact]
    public void Int4OddColumnsAndInt8UsePerRowScaleViews()
    {
        using var directory = new TemporaryDirectory();
        var path = SafeTensorTestFile.Write(
            directory.Path,
            new TestTensor("gate.payload", "U8", [4], [0xf1, 0x07, 0x98, 0x05]),
            new TestTensor("up.payload", "U8", [4], [0x21, 0x03, 0x54, 0x06]),
            new TestTensor("down.payload", "I8", [3, 2], [0xff, 0x02, 0x03, 0xfc, 0x05, 0xfa]),
            new TestTensor("gate.scales", "F32", [2], SafeTensorTestFile.Float32([0.5f, 0.25f])),
            new TestTensor("up.scales", "F32", [2], SafeTensorTestFile.Float32([1.0f, 2.0f])),
            new TestTensor("down.scales", "F32", [3], SafeTensorTestFile.Float32([0.1f, 0.2f, 0.3f])));
        var catalog = SafeTensorCatalog.Read([path]);
        var gateShape = new QuantizedTensorShape(QuantizedTensorFormat.Int4, 2, 3);
        var upShape = new QuantizedTensorShape(QuantizedTensorFormat.Int4, 2, 3);
        var downShape = new QuantizedTensorShape(QuantizedTensorFormat.Int8, 3, 2);
        var gate = new QuantizedTensorDescriptor(
            catalog.GetRequired("gate.payload"),
            catalog.GetRequired("gate.scales"),
            gateShape);
        var up = new QuantizedTensorDescriptor(
            catalog.GetRequired("up.payload"),
            catalog.GetRequired("up.scales"),
            upShape);
        var down = new QuantizedTensorDescriptor(
            catalog.GetRequired("down.payload"),
            catalog.GetRequired("down.scales"),
            downShape);
        using var source = new TensorDataSource(catalog);
        using var slab = new ExpertSlab(gateShape, upShape, downShape);

        Assert.Throws<InvalidOperationException>(() => ReadGateValue(slab));
        slab.Load(source, gate, up, down);

        Assert.Equal(1, slab.Gate.GetQuantizedValue(0, 0));
        Assert.Equal(-1, slab.Gate.GetQuantizedValue(0, 1));
        Assert.Equal(7, slab.Gate.GetQuantizedValue(0, 2));
        Assert.Equal(-8, slab.Gate.GetQuantizedValue(1, 0));
        Assert.Equal(5, slab.Gate.GetQuantizedValue(1, 2));
        Assert.Equal(-1, slab.Down.GetQuantizedValue(0, 0));
        Assert.Equal(-6, slab.Down.GetQuantizedValue(2, 1));
        Assert.Equal(new[] { 0.5f, 0.25f }, slab.Gate.Scales.ToArray());
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, slab.Down.Scales.ToArray());
        Assert.Equal(14, slab.PayloadCapacity);

        var incompatible = new QuantizedTensorShape(QuantizedTensorFormat.Int4, 2, 5);
        Assert.Throws<InvalidDataException>(() => new QuantizedTensorDescriptor(
            catalog.GetRequired("gate.payload"),
            catalog.GetRequired("gate.scales"),
            incompatible));

        slab.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ReadGateValue(slab));
    }

    [Fact]
    public void BoundsOverflowTruncationAndDisposedStateFailExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var path = SafeTensorTestFile.Write(
            directory.Path,
            new TestTensor("payload", "U8", [4], [1, 2, 3, 4]));
        var catalog = SafeTensorCatalog.Read([path]);
        var descriptor = catalog.GetRequired("payload");
        var source = new TensorDataSource(catalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadExactly(descriptor, 3, new byte[2]));
        Assert.Throws<OperationCanceledException>(() =>
            source.ReadExactly(descriptor, 0, new byte[1], cancellation.Token));
        Assert.Throws<OverflowException>(() => new TensorDescriptor(
            "overflow",
            TensorDataType.UInt8,
            [1],
            path,
            long.MaxValue,
            1));

        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.ReadExactly(descriptor, 0, new byte[1]));

        using (var stream = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(stream.Length - 1);
        }

        Assert.Throws<EndOfStreamException>(() => new TensorDataSource(catalog));
    }

    [Fact]
    public void WorkspaceAndResidentBuffersRejectUseAfterDispose()
    {
        using var directory = new TemporaryDirectory();
        var path = SafeTensorTestFile.Write(
            directory.Path,
            new TestTensor("value", "F32", [1], SafeTensorTestFile.Float32([3.0f])));
        var catalog = SafeTensorCatalog.Read([path]);
        using var source = new TensorDataSource(catalog);
        var resident = source.LoadFloat32(catalog.GetRequired("value"));
        var workspace = new TensorWorkspace(8, 4, 6);

        workspace.GetActivations(2)[0] = 7.0f;
        Assert.Equal(7.0f, workspace.GetActivations(2)[0]);
        Assert.Equal(60L, workspace.BudgetedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.GetOutputs(7));

        resident.Dispose();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resident.Span.Clear());
        Assert.Throws<ObjectDisposedException>(() => workspace.GetQuantized(1).Clear());
    }

    [Fact]
    public void RepeatedDataSourceDisposeReleasesShardHandles()
    {
        using var directory = new TemporaryDirectory();
        var path = SafeTensorTestFile.Write(
            directory.Path,
            "first.safetensors",
            new TestTensor("first.payload", "U8", [4], [1, 2, 3, 4]));
        var secondPath = SafeTensorTestFile.Write(
            directory.Path,
            "second.safetensors",
            new TestTensor("second.payload", "U8", [3], [5, 6, 7]));
        var catalog = SafeTensorCatalog.Read([path, secondPath]);

        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var source = new TensorDataSource(catalog);
            Assert.Equal(2, source.ShardCount);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, source.ReadTensor(catalog.GetRequired("first.payload")));
            Assert.Equal(new byte[] { 5, 6, 7 }, source.ReadTensor(catalog.GetRequired("second.payload")));
        }

        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var secondExclusive = File.Open(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        Assert.True(secondExclusive.CanWrite);
    }

    private static int ReadGateValue(ExpertSlab slab)
        => slab.Gate.GetQuantizedValue(0, 0);
}

internal sealed record TestTensor(
    string Name,
    string DataType,
    IReadOnlyList<long> Shape,
    byte[] Payload);

internal static class SafeTensorTestFile
{
    public static string Write(string directory, params TestTensor[] tensors)
        => Write(directory, "storage.safetensors", tensors);

    public static string Write(string directory, string fileName, params TestTensor[] tensors)
    {
        var path = Path.Combine(directory, fileName);
        using var header = new MemoryStream();
        long offset = 0;
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            foreach (var tensor in tensors)
            {
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", tensor.DataType);
                writer.WritePropertyName("shape");
                writer.WriteStartArray();
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("data_offsets");
                writer.WriteStartArray();
                writer.WriteNumberValue(offset);
                offset = checked(offset + tensor.Payload.Length);
                writer.WriteNumberValue(offset);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        while ((header.Length & 7) != 0)
        {
            header.WriteByte((byte)' ');
        }

        using var file = File.Create(path);
        Span<byte> headerLength = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(headerLength, checked((ulong)header.Length));
        file.Write(headerLength);
        header.Position = 0;
        header.CopyTo(file);
        foreach (var tensor in tensors)
        {
            file.Write(tensor.Payload);
        }

        return path;
    }

    public static byte[] Float32(IReadOnlyList<float> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(float))];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                values[index]);
        }

        return bytes;
    }

    public static byte[] Float16(IReadOnlyList<float> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(ushort))];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)values[index]));
        }

        return bytes;
    }

    public static byte[] BFloat16(IReadOnlyList<float> values)
    {
        var bytes = new byte[checked(values.Count * sizeof(ushort))];
        for (var index = 0; index < values.Count; index++)
        {
            var bits = BitConverter.SingleToUInt32Bits(values[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                checked((ushort)(bits >> 16)));
        }

        return bytes;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
