using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tomur.Providers.Glm;

internal static class OptimizedKernels
{
    private static readonly KernelExecutionOptions options = KernelExecutionOptions.FromEnvironment();

    static OptimizedKernels() => options.Validate();

    public static KernelExecutionOptions Options => options;

    public static string Description => options.EnableSimd && Avx2.IsSupported
        ? $"AVX2 packed int4/int8 and Vector{options.SimdWidthBits} F32, parallelism {options.EffectiveMaxDegreeOfParallelism}"
        : options.EnableSimd && Vector.IsHardwareAccelerated
            ? $"SIMD Vector{options.SimdWidthBits} F32 with scalar quantized decode, parallelism {options.EffectiveMaxDegreeOfParallelism}"
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
        => DequantMatVecPair(
            firstMatrix,
            secondMatrix,
            input,
            firstDestination,
            secondDestination,
            options);

    internal static void DequantMatVecPair(
        QuantizedTensorView firstMatrix,
        QuantizedTensorView secondMatrix,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination,
        KernelExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        executionOptions.Validate();
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

        if (!CanVectorize(firstMatrix.Shape.Columns, executionOptions))
        {
            DequantScalar(firstMatrix, input, firstDestination);
            DequantScalar(secondMatrix, input, secondDestination);
            return;
        }

        if (ShouldParallelize(
                firstMatrix.Shape.Rows,
                checked(firstMatrix.Shape.Columns * 2),
                executionOptions))
        {
            ParallelDequantMatVecPair(
                firstMatrix,
                secondMatrix,
                input,
                firstDestination,
                secondDestination,
                executionOptions);
            return;
        }

        for (var row = 0; row < firstMatrix.Shape.Rows; row++)
        {
            firstDestination[row] = DotQuantized(firstMatrix, row, input) * firstMatrix.Scales[row];
            secondDestination[row] = DotQuantized(secondMatrix, row, input) * secondMatrix.Scales[row];
        }
    }

    internal static void DequantMatVec(
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

        if (ShouldParallelize(matrix.Shape.Rows, matrix.Shape.Columns, executionOptions))
        {
            ParallelDequantMatVec(matrix, input, destination, executionOptions);
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
        if (Avx2.IsSupported && Avx.IsSupported && matrix.Shape.Columns >= Vector256<float>.Count)
        {
            return DotQuantizedAvx2(matrix, row, input);
        }

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

    private static unsafe float DotQuantizedAvx2(
        QuantizedTensorView matrix,
        int row,
        ReadOnlySpan<float> input)
    {
        var columns = matrix.Shape.Columns;
        var accumulator = Vector256<float>.Zero;
        var column = 0;
        fixed (byte* payloadPointer = matrix.Payload)
        fixed (float* inputPointer = input)
        {
            if (matrix.Shape.Format == QuantizedTensorFormat.Int8)
            {
                var rowOffset = checked(row * columns);
                for (; column <= columns - 16; column += 16)
                {
                    var packed = Sse2.LoadVector128((sbyte*)(payloadPointer + rowOffset + column));
                    accumulator = MultiplyAdd(
                        Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(packed)),
                        Avx.LoadVector256(inputPointer + column),
                        accumulator);
                    accumulator = MultiplyAdd(
                        Avx.ConvertToVector256Single(
                            Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(packed.AsByte(), 8).AsSByte())),
                        Avx.LoadVector256(inputPointer + column + 8),
                        accumulator);
                }
            }
            else
            {
                var storedColumns = (columns + 1) / 2;
                var rowOffset = checked(row * storedColumns);
                var nibbleMask = Vector128.Create((byte)0x0f);
                for (; column <= columns - 32; column += 32)
                {
                    var packed = Sse2.LoadVector128(payloadPointer + rowOffset + (column / 2));
                    var low = Sse2.And(packed, nibbleMask);
                    var high = Sse2.And(
                        Sse2.ShiftRightLogical(packed.AsUInt16(), 4).AsByte(),
                        nibbleMask);
                    var first = Sse2.UnpackLow(low, high);
                    var second = Sse2.UnpackHigh(low, high);

                    accumulator = MultiplyDecodedNibbles(first, inputPointer + column, matrix.Shape.ValueEncoding, accumulator);
                    accumulator = MultiplyDecodedNibbles(second, inputPointer + column + 16, matrix.Shape.ValueEncoding, accumulator);
                }
            }
        }

        double sum = Sum(accumulator);
        for (; column < columns; column++)
        {
            sum += (double)DecodeQuantized(matrix, row, column) * input[column];
        }

        return (float)sum;
    }

    private static unsafe Vector256<float> MultiplyDecodedNibbles(
        Vector128<byte> nibbles,
        float* input,
        QuantizedValueEncoding encoding,
        Vector256<float> accumulator)
    {
        var lower = DecodeNibbles(nibbles, encoding);
        var upper = DecodeNibbles(Sse2.ShiftRightLogical128BitLane(nibbles, 8), encoding);
        accumulator = MultiplyAdd(
            Avx.ConvertToVector256Single(lower),
            Avx.LoadVector256(input),
            accumulator);
        return MultiplyAdd(
            Avx.ConvertToVector256Single(upper),
            Avx.LoadVector256(input + 8),
            accumulator);
    }

    private static Vector256<int> DecodeNibbles(
        Vector128<byte> nibbles,
        QuantizedValueEncoding encoding)
    {
        var values = Avx2.ConvertToVector256Int32(nibbles);
        if (encoding == QuantizedValueEncoding.OffsetBinary)
        {
            return Avx2.Subtract(values, Vector256.Create(8));
        }

        var negative = Avx2.CompareGreaterThan(values, Vector256.Create(7));
        var correction = Avx2.And(negative, Vector256.Create(16));
        return Avx2.Subtract(values, correction);
    }

    private static Vector256<float> MultiplyAdd(
        Vector256<float> left,
        Vector256<float> right,
        Vector256<float> accumulator)
        => Avx.Add(accumulator, Avx.Multiply(left, right));

    private static double Sum(Vector256<float> value)
    {
        double sum = 0;
        for (var index = 0; index < Vector256<float>.Count; index++)
        {
            sum += value.GetElement(index);
        }

        return sum;
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

    private static unsafe void ParallelDequantMatVec(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination,
        KernelExecutionOptions executionOptions)
    {
        fixed (byte* payloadPointer = matrix.Payload)
        fixed (float* scalesPointer = matrix.Scales)
        fixed (float* inputPointer = input)
        fixed (float* destinationPointer = destination)
        {
            var payloadAddress = (nint)payloadPointer;
            var scalesAddress = (nint)scalesPointer;
            var inputAddress = (nint)inputPointer;
            var destinationAddress = (nint)destinationPointer;
            var shape = matrix.Shape;
            Parallel.For(
                0,
                shape.Rows,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = executionOptions.EffectiveMaxDegreeOfParallelism
                },
                row =>
                {
                    var localMatrix = new QuantizedTensorView(
                        shape,
                        new ReadOnlySpan<byte>((void*)payloadAddress, shape.PayloadByteLength),
                        new ReadOnlySpan<float>((void*)scalesAddress, shape.ScaleCount));
                    var localInput = new ReadOnlySpan<float>((void*)inputAddress, shape.Columns);
                    var localDestination = new Span<float>((void*)destinationAddress, shape.Rows);
                    localDestination[row] =
                        DotQuantized(localMatrix, row, localInput) * localMatrix.Scales[row];
                });
        }
    }

    private static unsafe void ParallelDequantMatVecPair(
        QuantizedTensorView firstMatrix,
        QuantizedTensorView secondMatrix,
        ReadOnlySpan<float> input,
        Span<float> firstDestination,
        Span<float> secondDestination,
        KernelExecutionOptions executionOptions)
    {
        fixed (byte* firstPayloadPointer = firstMatrix.Payload)
        fixed (float* firstScalesPointer = firstMatrix.Scales)
        fixed (byte* secondPayloadPointer = secondMatrix.Payload)
        fixed (float* secondScalesPointer = secondMatrix.Scales)
        fixed (float* inputPointer = input)
        fixed (float* firstDestinationPointer = firstDestination)
        fixed (float* secondDestinationPointer = secondDestination)
        {
            var firstPayloadAddress = (nint)firstPayloadPointer;
            var firstScalesAddress = (nint)firstScalesPointer;
            var secondPayloadAddress = (nint)secondPayloadPointer;
            var secondScalesAddress = (nint)secondScalesPointer;
            var inputAddress = (nint)inputPointer;
            var firstDestinationAddress = (nint)firstDestinationPointer;
            var secondDestinationAddress = (nint)secondDestinationPointer;
            var firstShape = firstMatrix.Shape;
            var secondShape = secondMatrix.Shape;
            Parallel.For(
                0,
                firstShape.Rows,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = executionOptions.EffectiveMaxDegreeOfParallelism
                },
                row =>
                {
                    var localFirstMatrix = new QuantizedTensorView(
                        firstShape,
                        new ReadOnlySpan<byte>((void*)firstPayloadAddress, firstShape.PayloadByteLength),
                        new ReadOnlySpan<float>((void*)firstScalesAddress, firstShape.ScaleCount));
                    var localSecondMatrix = new QuantizedTensorView(
                        secondShape,
                        new ReadOnlySpan<byte>((void*)secondPayloadAddress, secondShape.PayloadByteLength),
                        new ReadOnlySpan<float>((void*)secondScalesAddress, secondShape.ScaleCount));
                    var localInput = new ReadOnlySpan<float>((void*)inputAddress, firstShape.Columns);
                    var localFirstDestination = new Span<float>((void*)firstDestinationAddress, firstShape.Rows);
                    var localSecondDestination = new Span<float>((void*)secondDestinationAddress, secondShape.Rows);
                    localFirstDestination[row] =
                        DotQuantized(localFirstMatrix, row, localInput) * localFirstMatrix.Scales[row];
                    localSecondDestination[row] =
                        DotQuantized(localSecondMatrix, row, localInput) * localSecondMatrix.Scales[row];
                });
        }
    }
}
