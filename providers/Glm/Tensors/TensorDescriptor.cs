using System.Collections.ObjectModel;

namespace Tomur.Providers.Glm;

internal enum TensorDataType
{
    Float32,
    Float16,
    BFloat16,
    Int8,
    UInt8
}

internal static class TensorDataTypes
{
    public static TensorDataType Parse(string value, string tensorName, string shardPath)
        => value switch
        {
            "F32" => TensorDataType.Float32,
            "F16" => TensorDataType.Float16,
            "BF16" => TensorDataType.BFloat16,
            "I8" => TensorDataType.Int8,
            "U8" => TensorDataType.UInt8,
            _ => throw new InvalidDataException(
                $"Tensor data type is not supported for '{tensorName}' in {shardPath}: {value}")
        };

    public static string ToSafeTensorName(this TensorDataType dataType)
        => dataType switch
        {
            TensorDataType.Float32 => "F32",
            TensorDataType.Float16 => "F16",
            TensorDataType.BFloat16 => "BF16",
            TensorDataType.Int8 => "I8",
            TensorDataType.UInt8 => "U8",
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };

    public static int GetElementSize(this TensorDataType dataType)
        => dataType switch
        {
            TensorDataType.Float32 => sizeof(float),
            TensorDataType.Float16 or TensorDataType.BFloat16 => sizeof(ushort),
            TensorDataType.Int8 or TensorDataType.UInt8 => sizeof(byte),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };
}

internal sealed class TensorDescriptor
{
    private const int MaximumRank = 16;
    private readonly ReadOnlyCollection<long> logicalShape;

    public TensorDescriptor(
        string name,
        TensorDataType dataType,
        IReadOnlyList<long> logicalShape,
        string shardPath,
        long offset,
        long physicalLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logicalShape);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardPath);

        if (logicalShape.Count > MaximumRank)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalShape),
                $"Tensor rank cannot exceed {MaximumRank}.");
        }

        var shape = new long[logicalShape.Count];
        long elementCount = 1;
        for (var index = 0; index < logicalShape.Count; index++)
        {
            var dimension = logicalShape[index];
            if (dimension <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalShape),
                    $"Tensor dimension {index} must be positive.");
            }

            shape[index] = dimension;
            elementCount = checked(elementCount * dimension);
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (physicalLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalLength));
        }

        _ = checked(offset + physicalLength);

        Name = name;
        DataType = dataType;
        this.logicalShape = Array.AsReadOnly(shape);
        ElementCount = elementCount;
        ShardPath = Path.GetFullPath(shardPath);
        Offset = offset;
        PhysicalLength = physicalLength;
    }

    public string Name { get; }

    public TensorDataType DataType { get; }

    public string DataTypeName => DataType.ToSafeTensorName();

    public IReadOnlyList<long> LogicalShape => logicalShape;

    public long ElementCount { get; }

    public string ShardPath { get; }

    public long Offset { get; }

    public long PhysicalLength { get; }

    public TensorDescriptor WithLogicalShape(IReadOnlyList<long> shape)
        => new(Name, DataType, shape, ShardPath, Offset, PhysicalLength);

    public override string ToString()
        => $"{Name} [{string.Join(", ", logicalShape)}] {DataTypeName} at {Path.GetFileName(ShardPath)}:{Offset}+{PhysicalLength}";
}
