namespace Tomur.Providers.Glm;

internal enum QuantizedTensorFormat
{
    Int8,
    Int4
}

internal readonly struct QuantizedTensorShape
{
    public QuantizedTensorShape(QuantizedTensorFormat format, int rows, int columns)
    {
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        Format = format;
        Rows = rows;
        Columns = columns;
        var storedColumns = format switch
        {
            QuantizedTensorFormat.Int8 => columns,
            QuantizedTensorFormat.Int4 => checked((columns + 1) / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
        PayloadByteLength = checked(rows * storedColumns);
    }

    public QuantizedTensorFormat Format { get; }

    public int Rows { get; }

    public int Columns { get; }

    public int PayloadByteLength { get; }

    public int ScaleCount => Rows;
}

internal sealed class QuantizedTensorDescriptor
{
    public QuantizedTensorDescriptor(
        TensorDescriptor payload,
        TensorDescriptor scales,
        QuantizedTensorShape shape)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(scales);

        var payloadTypeIsValid = shape.Format switch
        {
            QuantizedTensorFormat.Int8 => payload.DataType is TensorDataType.Int8 or TensorDataType.UInt8,
            QuantizedTensorFormat.Int4 => payload.DataType == TensorDataType.UInt8,
            _ => false
        };
        if (!payloadTypeIsValid)
        {
            throw new InvalidDataException(
                $"Quantized tensor '{payload.Name}' uses {payload.DataTypeName}, which is incompatible with {shape.Format}.");
        }

        if (payload.PhysicalLength != shape.PayloadByteLength)
        {
            throw new InvalidDataException(
                $"Quantized tensor '{payload.Name}' payload length must be {shape.PayloadByteLength} bytes, found {payload.PhysicalLength}.");
        }

        var expectedScaleBytes = checked((long)shape.ScaleCount * sizeof(float));
        if (scales.DataType != TensorDataType.Float32 || scales.PhysicalLength != expectedScaleBytes)
        {
            throw new InvalidDataException(
                $"Quantized tensor '{payload.Name}' requires {shape.ScaleCount} per-row F32 scales in '{scales.Name}'.");
        }

        Payload = payload.WithLogicalShape([shape.Rows, shape.Columns]);
        Scales = scales;
        Shape = shape;
    }

    public TensorDescriptor Payload { get; }

    public TensorDescriptor Scales { get; }

    public QuantizedTensorShape Shape { get; }
}

internal readonly ref struct QuantizedTensorView
{
    public QuantizedTensorView(
        QuantizedTensorShape shape,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<float> scales)
    {
        if (payload.Length != shape.PayloadByteLength)
        {
            throw new ArgumentException(
                $"Quantized payload must contain exactly {shape.PayloadByteLength} bytes.",
                nameof(payload));
        }

        if (scales.Length != shape.ScaleCount)
        {
            throw new ArgumentException(
                $"Quantized scales must contain exactly {shape.ScaleCount} values.",
                nameof(scales));
        }

        Shape = shape;
        Payload = payload;
        Scales = scales;
    }

    public QuantizedTensorShape Shape { get; }

    public ReadOnlySpan<byte> Payload { get; }

    public ReadOnlySpan<float> Scales { get; }

    public int GetQuantizedValue(int row, int column)
    {
        if ((uint)row >= (uint)Shape.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if ((uint)column >= (uint)Shape.Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        if (Shape.Format == QuantizedTensorFormat.Int8)
        {
            return unchecked((sbyte)Payload[(row * Shape.Columns) + column]);
        }

        var storedColumns = (Shape.Columns + 1) / 2;
        var packed = Payload[(row * storedColumns) + (column / 2)];
        var nibble = (column & 1) == 0 ? packed & 0x0f : packed >> 4;
        return nibble >= 8 ? nibble - 16 : nibble;
    }
}
