using System.Buffers;

namespace Tomur.Providers.Glm;

internal sealed class ExpertSlab : IDisposable
{
    private readonly Slot gate;
    private readonly Slot up;
    private readonly Slot down;
    private byte[]? payloads;
    private float[]? scales;
    private bool loaded;

    public ExpertSlab(
        QuantizedTensorShape gateShape,
        QuantizedTensorShape upShape,
        QuantizedTensorShape downShape)
    {
        ValidateShapeArgument(gateShape, nameof(gateShape));
        ValidateShapeArgument(upShape, nameof(upShape));
        ValidateShapeArgument(downShape, nameof(downShape));
        gate = new Slot(gateShape, 0, 0);
        up = new Slot(
            upShape,
            gateShape.PayloadByteLength,
            gateShape.ScaleCount);
        down = new Slot(
            downShape,
            checked(gateShape.PayloadByteLength + upShape.PayloadByteLength),
            checked(gateShape.ScaleCount + upShape.ScaleCount));

        PayloadCapacity = checked(
            gateShape.PayloadByteLength + upShape.PayloadByteLength + downShape.PayloadByteLength);
        ScaleCapacity = checked(gateShape.ScaleCount + upShape.ScaleCount + downShape.ScaleCount);

        byte[]? rentedPayloads = null;
        float[]? rentedScales = null;
        try
        {
            rentedPayloads = ArrayPool<byte>.Shared.Rent(PayloadCapacity);
            rentedScales = ArrayPool<float>.Shared.Rent(ScaleCapacity);
            payloads = rentedPayloads;
            scales = rentedScales;
        }
        catch
        {
            Return(rentedPayloads);
            Return(rentedScales);
            throw;
        }
    }

    public int PayloadCapacity { get; }

    public int ScaleCapacity { get; }

    public long BudgetedBytes => checked((long)PayloadCapacity + ((long)ScaleCapacity * sizeof(float)));

    public QuantizedTensorView Gate => GetView(gate);

    public QuantizedTensorView Up => GetView(up);

    public QuantizedTensorView Down => GetView(down);

    public void Load(
        TensorDataSource source,
        QuantizedTensorDescriptor gateTensor,
        QuantizedTensorDescriptor upTensor,
        QuantizedTensorDescriptor downTensor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(gateTensor);
        ArgumentNullException.ThrowIfNull(upTensor);
        ArgumentNullException.ThrowIfNull(downTensor);
        var payloadBuffer = GetPayloads();
        var scaleBuffer = GetScales();
        loaded = false;

        ValidateShape(gate, gateTensor, "gate");
        ValidateShape(up, upTensor, "up");
        ValidateShape(down, downTensor, "down");
        cancellationToken.ThrowIfCancellationRequested();

        var tensors = new[] { gateTensor, upTensor, downTensor };
        if (AreAdjacent(tensors.Select(static item => item.Payload).ToArray()))
        {
            source.ReadAdjacent(
                tensors.Select(static item => item.Payload).ToArray(),
                payloadBuffer.AsSpan(0, PayloadCapacity),
                cancellationToken);
        }
        else
        {
            ReadPayload(source, gate, gateTensor, payloadBuffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReadPayload(source, up, upTensor, payloadBuffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReadPayload(source, down, downTensor, payloadBuffer, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        source.ReadFloat32(
            gateTensor.Scales,
            scaleBuffer.AsSpan(gate.ScaleOffset, gate.Shape.ScaleCount),
            cancellationToken);
        source.ReadFloat32(
            upTensor.Scales,
            scaleBuffer.AsSpan(up.ScaleOffset, up.Shape.ScaleCount),
            cancellationToken);
        source.ReadFloat32(
            downTensor.Scales,
            scaleBuffer.AsSpan(down.ScaleOffset, down.Shape.ScaleCount),
            cancellationToken);
        loaded = true;
    }

    public void Dispose()
    {
        loaded = false;
        var payloadBuffer = Interlocked.Exchange(ref payloads, null);
        var scaleBuffer = Interlocked.Exchange(ref scales, null);
        Return(payloadBuffer);
        Return(scaleBuffer);
    }

    private QuantizedTensorView GetView(Slot slot)
    {
        var payloadBuffer = GetPayloads();
        var scaleBuffer = GetScales();
        if (!loaded)
        {
            throw new InvalidOperationException("Expert slab does not contain a complete expert.");
        }

        return new QuantizedTensorView(
            slot.Shape,
            payloadBuffer.AsSpan(slot.PayloadOffset, slot.Shape.PayloadByteLength),
            scaleBuffer.AsSpan(slot.ScaleOffset, slot.Shape.ScaleCount));
    }

    private static void ValidateShape(Slot slot, QuantizedTensorDescriptor tensor, string projection)
    {
        var actual = tensor.Shape;
        var expected = slot.Shape;
        if (actual.Format != expected.Format || actual.Rows != expected.Rows || actual.Columns != expected.Columns)
        {
            throw new InvalidDataException(
                $"Expert {projection} tensor shape does not match the reusable slab layout.");
        }
    }

    private static void ValidateShapeArgument(QuantizedTensorShape shape, string parameterName)
    {
        if (shape.Rows <= 0 || shape.Columns <= 0 || shape.PayloadByteLength <= 0)
        {
            throw new ArgumentException("Expert slab shapes must be initialized and non-empty.", parameterName);
        }
    }

    private static bool AreAdjacent(IReadOnlyList<TensorDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var path = descriptors[0].ShardPath;
        var expectedOffset = descriptors[0].Offset;
        foreach (var descriptor in descriptors)
        {
            if (!comparer.Equals(path, descriptor.ShardPath) || descriptor.Offset != expectedOffset)
            {
                return false;
            }

            expectedOffset = checked(descriptor.Offset + descriptor.PhysicalLength);
        }

        return true;
    }

    private static void ReadPayload(
        TensorDataSource source,
        Slot slot,
        QuantizedTensorDescriptor tensor,
        byte[] payloadBuffer,
        CancellationToken cancellationToken)
        => source.ReadExactly(
            tensor.Payload,
            0,
            payloadBuffer.AsSpan(slot.PayloadOffset, slot.Shape.PayloadByteLength),
            cancellationToken);

    private byte[] GetPayloads()
    {
        ObjectDisposedException.ThrowIf(payloads is null, this);
        return payloads;
    }

    private float[] GetScales()
    {
        ObjectDisposedException.ThrowIf(scales is null, this);
        return scales;
    }

    private static void Return<T>(T[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<T>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed record Slot(QuantizedTensorShape Shape, int PayloadOffset, int ScaleOffset);
}
