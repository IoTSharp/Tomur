namespace Tomur.Providers.Glm;

internal sealed class ResidentWeight : IDisposable
{
    private ResidentTensor<float>? floating;
    private ResidentTensor<float>? scales;
    private byte[]? payload;

    private ResidentWeight(
        ResidentWeightSpec spec,
        ResidentTensor<float>? floating,
        byte[]? payload,
        ResidentTensor<float>? scales)
    {
        Spec = spec;
        this.floating = floating;
        this.payload = payload;
        this.scales = scales;
    }

    public ResidentWeightSpec Spec { get; }

    public long ResidentBytes => Spec.ResidentBytes;

    public static ResidentWeight Load(
        TensorDataSource source,
        ResidentWeightSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Quantized is null)
        {
            return new ResidentWeight(
                spec,
                source.LoadFloat32(spec.Descriptor, cancellationToken),
                null,
                null);
        }

        ResidentTensor<float>? loadedScales = null;
        try
        {
            var loadedPayload = source.ReadTensor(spec.Quantized.Payload, cancellationToken);
            loadedScales = source.LoadFloat32(spec.Quantized.Scales, cancellationToken);
            return new ResidentWeight(spec, null, loadedPayload, loadedScales);
        }
        catch
        {
            loadedScales?.Dispose();
            throw;
        }
    }

    public ReadOnlySpan<float> GetFloatingValues()
    {
        if (floating is null)
        {
            throw new InvalidOperationException(
                $"Resident tensor '{Spec.Descriptor.Name}' is quantized and has no F32 view.");
        }

        return floating.ReadOnlySpan;
    }

    public void Multiply(ReadOnlySpan<float> input, Span<float> destination)
    {
        if (Spec.Quantized is null)
        {
            var shape = Spec.Descriptor.LogicalShape;
            if (shape.Count != 2 || shape[0] > int.MaxValue || shape[1] > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Resident matrix '{Spec.Descriptor.Name}' must have a two-dimensional managed shape.");
            }

            OptimizedKernels.MatVec(
                GetFloatingValues(),
                (int)shape[0],
                (int)shape[1],
                (int)shape[1],
                input,
                destination);
            return;
        }

        var view = GetQuantizedView();
        if (view.Shape.Format == QuantizedTensorFormat.Int8)
        {
            OptimizedKernels.Int8DequantMatVec(view, input, destination);
        }
        else
        {
            OptimizedKernels.Int4DequantMatVec(view, input, destination);
        }
    }

    public void MultiplyPair(
        ResidentWeight second,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination)
    {
        ArgumentNullException.ThrowIfNull(second);
        if (Spec.Quantized is null && second.Spec.Quantized is null)
        {
            var firstShape = Spec.Descriptor.LogicalShape;
            var secondShape = second.Spec.Descriptor.LogicalShape;
            if (firstShape.Count != 2 || secondShape.Count != 2 ||
                firstShape[0] != secondShape[0] || firstShape[1] != secondShape[1] ||
                firstShape[0] > int.MaxValue || firstShape[1] > int.MaxValue)
            {
                throw new InvalidDataException("Paired resident projections must have the same two-dimensional managed shape.");
            }

            OptimizedKernels.MatVecPair(
                GetFloatingValues(),
                second.GetFloatingValues(),
                (int)firstShape[0],
                (int)firstShape[1],
                (int)firstShape[1],
                input,
                firstDestination,
                secondDestination);
            return;
        }

        if (Spec.Quantized is not null && second.Spec.Quantized is not null)
        {
            OptimizedKernels.DequantMatVecPair(
                GetQuantizedView(),
                second.GetQuantizedView(),
                input,
                firstDestination,
                secondDestination);
            return;
        }

        Multiply(input, firstDestination);
        second.Multiply(input, secondDestination);
    }

    public float GetValue(int row, int column)
    {
        if (Spec.Quantized is null)
        {
            var shape = Spec.Descriptor.LogicalShape;
            if (shape.Count != 2 || (uint)row >= (ulong)shape[0] || (uint)column >= (ulong)shape[1])
            {
                throw new ArgumentOutOfRangeException();
            }

            return GetFloatingValues()[checked((row * (int)shape[1]) + column)];
        }

        var view = GetQuantizedView();
        return view.GetQuantizedValue(row, column) * view.Scales[row];
    }

    public void GatherRows(ReadOnlySpan<int> rowIds, Span<float> destination)
    {
        if (Spec.Quantized is null)
        {
            var shape = Spec.Descriptor.LogicalShape;
            ScalarKernels.GatherEmbeddings(
                GetFloatingValues(),
                checked((int)shape[0]),
                checked((int)shape[1]),
                checked((int)shape[1]),
                rowIds,
                destination,
                checked((int)shape[1]));
            return;
        }

        var view = GetQuantizedView();
        if (destination.Length != checked(rowIds.Length * view.Shape.Columns))
        {
            throw new ArgumentException("Embedding destination size does not match the requested rows.", nameof(destination));
        }

        for (var rowIndex = 0; rowIndex < rowIds.Length; rowIndex++)
        {
            var row = rowIds[rowIndex];
            if ((uint)row >= (uint)view.Shape.Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIds));
            }

            var output = destination.Slice(rowIndex * view.Shape.Columns, view.Shape.Columns);
            var scale = view.Scales[row];
            for (var column = 0; column < output.Length; column++)
            {
                output[column] = view.GetQuantizedValue(row, column) * scale;
            }
        }
    }

    public void Dispose()
    {
        floating?.Dispose();
        floating = null;
        scales?.Dispose();
        scales = null;
        payload = null;
    }

    private QuantizedTensorView GetQuantizedView()
    {
        var quantized = Spec.Quantized
            ?? throw new InvalidOperationException($"Resident tensor '{Spec.Descriptor.Name}' is not quantized.");
        ObjectDisposedException.ThrowIf(payload is null || scales is null, this);
        return new QuantizedTensorView(quantized.Shape, payload, scales.ReadOnlySpan);
    }
}
