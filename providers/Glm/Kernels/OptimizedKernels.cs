using System.Numerics;

namespace Tomur.Providers.Glm;

internal static class OptimizedKernels
{
    private static readonly KernelExecutionOptions options = KernelExecutionOptions.FromEnvironment();

    static OptimizedKernels() => options.Validate();

    public static KernelExecutionOptions Options => options;

    public static string Description => options.EnableSimd && Vector.IsHardwareAccelerated
        ? $"SIMD Vector{options.SimdWidthBits}, parallelism {options.EffectiveMaxDegreeOfParallelism}"
        : "scalar reference";

    public static void MatVec(
        ReadOnlySpan<float> matrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> destination)
        => MatVec(matrix, rows, columns, rowStride, input, destination, options);

    internal static void MatVec(
        ReadOnlySpan<float> matrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> destination,
        KernelExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        executionOptions.Validate();
        ScalarKernels.ValidateMatVec(matrix, rows, columns, rowStride, input, destination);
        if (!CanVectorize(columns, executionOptions))
        {
            ScalarKernels.MatVec(matrix, rows, columns, rowStride, input, destination);
            return;
        }

        if (ShouldParallelize(rows, columns, executionOptions))
        {
            ParallelMatVec(matrix, rows, columns, rowStride, input, destination, executionOptions);
            return;
        }

        for (var row = 0; row < rows; row++)
        {
            destination[row] = Dot(
                matrix.Slice(checked(row * rowStride), columns),
                input);
        }
    }

    public static void MatVecPair(
        ReadOnlySpan<float> firstMatrix,
        ReadOnlySpan<float> secondMatrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination)
    {
        ScalarKernels.ValidateMatVec(firstMatrix, rows, columns, rowStride, input, firstDestination);
        ScalarKernels.ValidateMatVec(secondMatrix, rows, columns, rowStride, input, secondDestination);
        if (firstDestination.Overlaps(secondDestination))
        {
            throw new ArgumentException("Projection destinations cannot overlap.", nameof(secondDestination));
        }

        if (!CanVectorize(columns, options))
        {
            ScalarKernels.MatVec(firstMatrix, rows, columns, rowStride, input, firstDestination);
            ScalarKernels.MatVec(secondMatrix, rows, columns, rowStride, input, secondDestination);
            return;
        }

        if (ShouldParallelize(rows, checked(columns * 2), options))
        {
            ParallelMatVecPair(
                firstMatrix,
                secondMatrix,
                rows,
                columns,
                rowStride,
                input,
                firstDestination,
                secondDestination,
                options);
            return;
        }

        for (var row = 0; row < rows; row++)
        {
            var rowOffset = checked(row * rowStride);
            firstDestination[row] = Dot(firstMatrix.Slice(rowOffset, columns), input);
            secondDestination[row] = Dot(secondMatrix.Slice(rowOffset, columns), input);
        }
    }

    public static void Int8DequantMatVec(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination)
        => DequantMatVec(matrix, QuantizedTensorFormat.Int8, input, destination, options);

    public static void Int4DequantMatVec(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination)
        => DequantMatVec(matrix, QuantizedTensorFormat.Int4, input, destination, options);

    internal static void Int8ActivationDot(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<sbyte> quantizedInput,
        Span<float> destination)
    {
        if (matrix.Shape.Format != QuantizedTensorFormat.Int8)
        {
            throw new ArgumentException("Activation integer dot-product requires an int8 weight matrix.", nameof(matrix));
        }

        ScalarKernels.ValidateDequantMatVec(matrix, QuantizedTensorFormat.Int8, input, destination);
        if (quantizedInput.Length != input.Length)
        {
            throw new ArgumentException(
                $"Buffer must contain exactly {input.Length} elements, found {quantizedInput.Length}.",
                nameof(quantizedInput));
        }

        var activationScale = ScalarKernels.QuantizeActivationToInt8(input, quantizedInput);
        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            var sum = 0L;
            var offset = checked(row * matrix.Shape.Columns);
            for (var column = 0; column < matrix.Shape.Columns; column++)
            {
                sum += (long)unchecked((sbyte)matrix.Payload[offset + column]) * quantizedInput[column];
            }

            destination[row] = sum * matrix.Scales[row] * activationScale;
        }
    }

    public static void DequantMatVecPair(
        QuantizedTensorView firstMatrix,
        QuantizedTensorView secondMatrix,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination)
    {
        if (firstMatrix.Shape.Format != secondMatrix.Shape.Format ||
            firstMatrix.Shape.Rows != secondMatrix.Shape.Rows ||
            firstMatrix.Shape.Columns != secondMatrix.Shape.Columns)
        {
            throw new ArgumentException("Quantized projection shapes and formats must match.", nameof(secondMatrix));
        }

        ScalarKernels.ValidateDequantMatVec(
            firstMatrix,
            firstMatrix.Shape.Format,
            input,
            firstDestination);
        ScalarKernels.ValidateDequantMatVec(
            secondMatrix,
            secondMatrix.Shape.Format,
            input,
            secondDestination);
        if (firstDestination.Overlaps(secondDestination))
        {
            throw new ArgumentException("Projection destinations cannot overlap.", nameof(secondDestination));
        }

        if (!CanVectorize(firstMatrix.Shape.Columns, options))
        {
            DequantScalar(firstMatrix, input, firstDestination);
            DequantScalar(secondMatrix, input, secondDestination);
            return;
        }

        for (var row = 0; row < firstMatrix.Shape.Rows; row++)
        {
            firstDestination[row] = DotQuantized(firstMatrix, row, input) * firstMatrix.Scales[row];
            secondDestination[row] = DotQuantized(secondMatrix, row, input) * secondMatrix.Scales[row];
        }
    }

    private static void DequantMatVec(
        QuantizedTensorView matrix,
        QuantizedTensorFormat expectedFormat,
        ReadOnlySpan<float> input,
        Span<float> destination,
        KernelExecutionOptions executionOptions)
    {
        ScalarKernels.ValidateDequantMatVec(matrix, expectedFormat, input, destination);
        if (!CanVectorize(matrix.Shape.Columns, executionOptions))
        {
            DequantScalar(matrix, input, destination);
            return;
        }

        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            destination[row] = DotQuantized(matrix, row, input) * matrix.Scales[row];
        }
    }

    private static void DequantScalar(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        if (matrix.Shape.Format == QuantizedTensorFormat.Int8)
        {
            ScalarKernels.Int8DequantMatVec(matrix, input, destination);
        }
        else
        {
            ScalarKernels.Int4DequantMatVec(matrix, input, destination);
        }
    }

    private static bool CanVectorize(int columns, KernelExecutionOptions executionOptions)
        => executionOptions.EnableSimd && Vector.IsHardwareAccelerated && columns >= Vector<float>.Count;

    private static bool ShouldParallelize(
        int rows,
        int columns,
        KernelExecutionOptions executionOptions)
        => executionOptions.EnableParallel &&
           executionOptions.EffectiveMaxDegreeOfParallelism > 1 &&
           rows >= executionOptions.ParallelRowThreshold &&
           checked((long)rows * columns) >= executionOptions.ParallelWorkThreshold;

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var width = Vector<float>.Count;
        var accumulator = Vector<float>.Zero;
        var index = 0;
        for (; index <= left.Length - width; index += width)
        {
            accumulator += new Vector<float>(left.Slice(index, width)) *
                           new Vector<float>(right.Slice(index, width));
        }

        double sum = Vector.Sum(accumulator);
        for (; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return (float)sum;
    }

    private static float DotQuantized(
        QuantizedTensorView matrix,
        int row,
        ReadOnlySpan<float> input)
    {
        var width = Vector<float>.Count;
        Span<float> decoded = stackalloc float[Vector<float>.Count];
        var accumulator = Vector<float>.Zero;
        var column = 0;
        for (; column <= matrix.Shape.Columns - width; column += width)
        {
            for (var lane = 0; lane < width; lane++)
            {
                decoded[lane] = DecodeQuantized(matrix, row, column + lane);
            }

            accumulator += new Vector<float>(decoded) *
                           new Vector<float>(input.Slice(column, width));
        }

        double sum = Vector.Sum(accumulator);
        for (; column < matrix.Shape.Columns; column++)
        {
            sum += (double)DecodeQuantized(matrix, row, column) * input[column];
        }

        return (float)sum;
    }

    private static int DecodeQuantized(QuantizedTensorView matrix, int row, int column)
    {
        if (matrix.Shape.Format == QuantizedTensorFormat.Int8)
        {
            return unchecked((sbyte)matrix.Payload[(row * matrix.Shape.Columns) + column]);
        }

        var storedColumns = (matrix.Shape.Columns + 1) / 2;
        var packed = matrix.Payload[(row * storedColumns) + (column / 2)];
        var nibble = (column & 1) == 0 ? packed & 0x0f : packed >> 4;
        return matrix.Shape.ValueEncoding == QuantizedValueEncoding.OffsetBinary
            ? nibble - 8
            : nibble >= 8 ? nibble - 16 : nibble;
    }

    private static unsafe void ParallelMatVec(
        ReadOnlySpan<float> matrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> destination,
        KernelExecutionOptions executionOptions)
    {
        // Parallel.For completes before fixed exits, so every worker observes pinned spans.
        fixed (float* matrixPointer = matrix)
        fixed (float* inputPointer = input)
        fixed (float* destinationPointer = destination)
        {
            var matrixAddress = (nint)matrixPointer;
            var inputAddress = (nint)inputPointer;
            var destinationAddress = (nint)destinationPointer;
            Parallel.For(
                0,
                rows,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = executionOptions.EffectiveMaxDegreeOfParallelism
                },
                row =>
                {
                    var source = new ReadOnlySpan<float>(
                        (void*)(matrixAddress + checked(row * rowStride * sizeof(float))),
                        columns);
                    var vector = new ReadOnlySpan<float>((void*)inputAddress, columns);
                    var output = new Span<float>((void*)destinationAddress, rows);
                    output[row] = Dot(source, vector);
                });
        }
    }

    private static unsafe void ParallelMatVecPair(
        ReadOnlySpan<float> firstMatrix,
        ReadOnlySpan<float> secondMatrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination,
        KernelExecutionOptions executionOptions)
    {
        // Pairing both projections in one row dispatch keeps input access local to each worker.
        fixed (float* firstMatrixPointer = firstMatrix)
        fixed (float* secondMatrixPointer = secondMatrix)
        fixed (float* inputPointer = input)
        fixed (float* firstDestinationPointer = firstDestination)
        fixed (float* secondDestinationPointer = secondDestination)
        {
            var firstMatrixAddress = (nint)firstMatrixPointer;
            var secondMatrixAddress = (nint)secondMatrixPointer;
            var inputAddress = (nint)inputPointer;
            var firstDestinationAddress = (nint)firstDestinationPointer;
            var secondDestinationAddress = (nint)secondDestinationPointer;
            Parallel.For(
                0,
                rows,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = executionOptions.EffectiveMaxDegreeOfParallelism
                },
                row =>
                {
                    var offset = checked(row * rowStride * sizeof(float));
                    var vector = new ReadOnlySpan<float>((void*)inputAddress, columns);
                    var firstOutput = new Span<float>((void*)firstDestinationAddress, rows);
                    var secondOutput = new Span<float>((void*)secondDestinationAddress, rows);
                    firstOutput[row] = Dot(
                        new ReadOnlySpan<float>((void*)(firstMatrixAddress + offset), columns),
                        vector);
                    secondOutput[row] = Dot(
                        new ReadOnlySpan<float>((void*)(secondMatrixAddress + offset), columns),
                        vector);
                });
        }
    }
}
