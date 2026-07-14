using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Tomur.Providers.Glm;

internal sealed record SafeTensorInfo(
    string Name,
    string DataType,
    IReadOnlyList<long> Shape,
    long ElementCount,
    string FilePath,
    long DataOffset,
    long ByteLength);

internal sealed class SafeTensorCatalog
{
    private const int MaximumHeaderBytes = 64 * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, SafeTensorInfo> tensors;

    private SafeTensorCatalog(IReadOnlyDictionary<string, SafeTensorInfo> tensors, long totalPayloadBytes)
    {
        this.tensors = tensors;
        TotalPayloadBytes = totalPayloadBytes;
    }

    public int Count => tensors.Count;

    public long TotalPayloadBytes { get; }

    public bool Contains(string name) => tensors.ContainsKey(name);

    public static SafeTensorCatalog Read(IEnumerable<string> paths)
    {
        var tensors = new Dictionary<string, SafeTensorInfo>(StringComparer.Ordinal);
        long totalPayloadBytes = 0;

        foreach (var path in paths.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var tensor in ReadFile(path))
            {
                if (!tensors.TryAdd(tensor.Name, tensor))
                {
                    throw new InvalidDataException($"Duplicate tensor name across model shards: {tensor.Name}");
                }

                totalPayloadBytes = checked(totalPayloadBytes + tensor.ByteLength);
            }
        }

        if (tensors.Count == 0)
        {
            throw new InvalidDataException("The model tensor files do not contain any tensors.");
        }

        return new SafeTensorCatalog(tensors, totalPayloadBytes);
    }

    private static IReadOnlyList<SafeTensorInfo> ReadFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < sizeof(ulong))
        {
            throw new InvalidDataException($"Safetensors shard is missing or too small: {path}");
        }

        using SafeFileHandle handle = File.OpenHandle(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        Span<byte> lengthBytes = stackalloc byte[sizeof(ulong)];
        ReadExactly(handle, lengthBytes, 0);
        var headerLengthValue = BinaryPrimitives.ReadUInt64LittleEndian(lengthBytes);
        if (headerLengthValue == 0 || headerLengthValue > MaximumHeaderBytes || headerLengthValue > int.MaxValue)
        {
            throw new InvalidDataException($"Safetensors header length is invalid in {path}: {headerLengthValue}");
        }

        var headerLength = (int)headerLengthValue;
        var dataStart = checked(sizeof(ulong) + (long)headerLength);
        if (dataStart > file.Length)
        {
            throw new InvalidDataException($"Safetensors header extends beyond the file: {path}");
        }

        var header = GC.AllocateUninitializedArray<byte>(headerLength);
        ReadExactly(handle, header, sizeof(ulong));
        using var document = JsonDocument.Parse(header, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Safetensors header root must be an object: {path}");
        }

        var tensors = new List<SafeTensorInfo>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("__metadata__"))
            {
                continue;
            }

            tensors.Add(ParseTensor(property, file.FullName, file.Length, dataStart));
        }

        return tensors;
    }

    private static SafeTensorInfo ParseTensor(
        JsonProperty property,
        string path,
        long fileLength,
        long dataStart)
    {
        var descriptor = property.Value;
        if (descriptor.ValueKind != JsonValueKind.Object ||
            !descriptor.TryGetProperty("dtype", out var dataTypeProperty) ||
            dataTypeProperty.ValueKind != JsonValueKind.String ||
            !descriptor.TryGetProperty("shape", out var shapeProperty) ||
            shapeProperty.ValueKind != JsonValueKind.Array ||
            !descriptor.TryGetProperty("data_offsets", out var offsetsProperty) ||
            offsetsProperty.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Tensor metadata is incomplete for '{property.Name}' in {path}.");
        }

        var shape = new List<long>();
        long elementCount = 1;
        foreach (var dimension in shapeProperty.EnumerateArray())
        {
            if (!dimension.TryGetInt64(out var value) || value < 0)
            {
                throw new InvalidDataException($"Tensor shape is invalid for '{property.Name}' in {path}.");
            }

            shape.Add(value);
            elementCount = checked(elementCount * value);
        }

        var offsets = offsetsProperty.EnumerateArray().ToArray();
        if (offsets.Length != 2 ||
            !offsets[0].TryGetInt64(out var start) ||
            !offsets[1].TryGetInt64(out var end) ||
            start < 0 || end < start)
        {
            throw new InvalidDataException($"Tensor data offsets are invalid for '{property.Name}' in {path}.");
        }

        var absoluteStart = checked(dataStart + start);
        var absoluteEnd = checked(dataStart + end);
        if (absoluteEnd > fileLength)
        {
            throw new InvalidDataException($"Tensor data extends beyond its shard for '{property.Name}' in {path}.");
        }

        var dataType = dataTypeProperty.GetString();
        if (string.IsNullOrWhiteSpace(dataType))
        {
            throw new InvalidDataException($"Tensor data type is empty for '{property.Name}' in {path}.");
        }

        return new SafeTensorInfo(
            property.Name,
            dataType,
            shape,
            elementCount,
            path,
            absoluteStart,
            end - start);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long fileOffset)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = RandomAccess.Read(handle, destination[read..], fileOffset + read);
            if (count == 0)
            {
                throw new EndOfStreamException("Unexpected end of file while reading model metadata.");
            }

            read += count;
        }
    }
}
