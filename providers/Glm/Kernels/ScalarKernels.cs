namespace Tomur.Providers.Glm;

internal static class ScalarKernels
{
    public static void GatherEmbeddings(
        ReadOnlySpan<float> embeddings,
        int vocabularySize,
        int embeddingSize,
        int embeddingStride,
        ReadOnlySpan<int> tokenIds,
        Span<float> destination,
        int destinationStride)
    {
        ValidateMatrix(
            embeddings.Length,
            vocabularySize,
            embeddingSize,
            embeddingStride,
            allowEmptyRows: false,
            nameof(embeddings));
        ValidateMatrix(
            destination.Length,
            tokenIds.Length,
            embeddingSize,
            destinationStride,
            allowEmptyRows: true,
            nameof(destination));
        EnsureNoOverlap(embeddings, destination, nameof(embeddings), nameof(destination));

        for (var tokenIndex = 0; tokenIndex < tokenIds.Length; tokenIndex++)
        {
            if ((uint)tokenIds[tokenIndex] >= (uint)vocabularySize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tokenIds),
                    $"Token ID at index {tokenIndex} is outside the embedding vocabulary: {tokenIds[tokenIndex]}.");
            }
        }

        for (var tokenIndex = 0; tokenIndex < tokenIds.Length; tokenIndex++)
        {
            var sourceOffset = checked(tokenIds[tokenIndex] * embeddingStride);
            var destinationOffset = checked(tokenIndex * destinationStride);
            embeddings.Slice(sourceOffset, embeddingSize)
                .CopyTo(destination.Slice(destinationOffset, embeddingSize));
        }
    }

    public static void RmsNorm(
        ReadOnlySpan<float> input,
        ReadOnlySpan<float> weight,
        float epsilon,
        Span<float> destination)
    {
        RequireNonEmpty(input, nameof(input));
        RequireExactLength(weight.Length, input.Length, nameof(weight));
        RequireExactLength(destination.Length, input.Length, nameof(destination));
        RequirePositiveFinite(epsilon, nameof(epsilon));
        EnsureNoOverlap(input, destination, nameof(input), nameof(destination));
        EnsureNoOverlap(weight, destination, nameof(weight), nameof(destination));

        double sumOfSquares = 0;
        for (var index = 0; index < input.Length; index++)
        {
            sumOfSquares += (double)input[index] * input[index];
        }

        var scale = 1.0 / Math.Sqrt((sumOfSquares / input.Length) + epsilon);
        for (var index = 0; index < input.Length; index++)
        {
            destination[index] = (float)(input[index] * scale * weight[index]);
        }
    }

    public static void LayerNorm(
        ReadOnlySpan<float> input,
        ReadOnlySpan<float> weight,
        ReadOnlySpan<float> bias,
        float epsilon,
        Span<float> destination)
    {
        RequireNonEmpty(input, nameof(input));
        RequireExactLength(weight.Length, input.Length, nameof(weight));
        RequireExactLength(bias.Length, input.Length, nameof(bias));
        RequireExactLength(destination.Length, input.Length, nameof(destination));
        RequirePositiveFinite(epsilon, nameof(epsilon));
        EnsureNoOverlap(input, destination, nameof(input), nameof(destination));
        EnsureNoOverlap(weight, destination, nameof(weight), nameof(destination));
        EnsureNoOverlap(bias, destination, nameof(bias), nameof(destination));

        double sum = 0;
        for (var index = 0; index < input.Length; index++)
        {
            sum += input[index];
        }

        var mean = sum / input.Length;
        double sumOfSquaredDifferences = 0;
        for (var index = 0; index < input.Length; index++)
        {
            var difference = input[index] - mean;
            sumOfSquaredDifferences += difference * difference;
        }

        var scale = 1.0 / Math.Sqrt((sumOfSquaredDifferences / input.Length) + epsilon);
        for (var index = 0; index < input.Length; index++)
        {
            destination[index] = (float)(((input[index] - mean) * scale * weight[index]) + bias[index]);
        }
    }

    public static void MatVec(
        ReadOnlySpan<float> matrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ValidateMatVec(matrix, rows, columns, rowStride, input, destination);

        for (var row = 0; row < rows; row++)
        {
            double sum = 0;
            var rowOffset = checked(row * rowStride);
            for (var column = 0; column < columns; column++)
            {
                sum += (double)matrix[rowOffset + column] * input[column];
            }

            destination[row] = (float)sum;
        }
    }

    public static void MatMul(
        ReadOnlySpan<float> left,
        int leftRows,
        int sharedDimension,
        int leftRowStride,
        ReadOnlySpan<float> right,
        int rightColumns,
        int rightRowStride,
        Span<float> destination,
        int destinationRowStride)
    {
        ValidateMatrix(
            left.Length,
            leftRows,
            sharedDimension,
            leftRowStride,
            allowEmptyRows: true,
            nameof(left));
        ValidateMatrix(
            right.Length,
            sharedDimension,
            rightColumns,
            rightRowStride,
            allowEmptyRows: false,
            nameof(right));
        ValidateMatrix(
            destination.Length,
            leftRows,
            rightColumns,
            destinationRowStride,
            allowEmptyRows: true,
            nameof(destination));
        EnsureNoOverlap(left, destination, nameof(left), nameof(destination));
        EnsureNoOverlap(right, destination, nameof(right), nameof(destination));

        for (var row = 0; row < leftRows; row++)
        {
            var leftOffset = checked(row * leftRowStride);
            var destinationOffset = checked(row * destinationRowStride);
            for (var column = 0; column < rightColumns; column++)
            {
                double sum = 0;
                for (var shared = 0; shared < sharedDimension; shared++)
                {
                    sum += (double)left[leftOffset + shared] * right[(shared * rightRowStride) + column];
                }

                destination[destinationOffset + column] = (float)sum;
            }
        }
    }

    public static void Int8DequantMatVec(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination)
        => DequantMatVec(matrix, QuantizedTensorFormat.Int8, input, destination);

    public static void Int4DequantMatVec(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<float> destination)
        => DequantMatVec(matrix, QuantizedTensorFormat.Int4, input, destination);

    public static float QuantizeActivationToInt8(
        ReadOnlySpan<float> input,
        Span<sbyte> destination)
    {
        RequireNonEmpty(input, nameof(input));
        RequireExactLength(destination.Length, input.Length, nameof(destination));

        double maximumMagnitude = 0;
        for (var index = 0; index < input.Length; index++)
        {
            if (!float.IsFinite(input[index]))
            {
                throw new ArgumentException(
                    $"Activation at index {index} must be finite.",
                    nameof(input));
            }

            maximumMagnitude = Math.Max(maximumMagnitude, Math.Abs((double)input[index]));
        }

        if (maximumMagnitude == 0)
        {
            destination.Clear();
            return 1.0f;
        }

        var scale = (float)(maximumMagnitude / 127.0);
        if (scale == 0)
        {
            scale = float.Epsilon;
        }

        for (var index = 0; index < input.Length; index++)
        {
            var rounded = Math.Round(input[index] / (double)scale, MidpointRounding.ToEven);
            var clamped = Math.Clamp(rounded, -127.0, 127.0);
            destination[index] = checked((sbyte)(int)clamped);
        }

        return scale;
    }

    public static void Int8ActivationDot(
        QuantizedTensorView matrix,
        ReadOnlySpan<float> input,
        Span<sbyte> quantizedInput,
        Span<float> destination)
    {
        if (matrix.Shape.Format != QuantizedTensorFormat.Int8)
        {
            throw new ArgumentException("Activation integer dot-product requires an int8 weight matrix.", nameof(matrix));
        }

        ValidateDequantMatVec(matrix, QuantizedTensorFormat.Int8, input, destination);
        RequireExactLength(quantizedInput.Length, input.Length, nameof(quantizedInput));
        var scale = QuantizeActivationToInt8(input, quantizedInput);
        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            var sum = 0L;
            var offset = checked(row * matrix.Shape.Columns);
            for (var column = 0; column < matrix.Shape.Columns; column++)
            {
                sum += (long)unchecked((sbyte)matrix.Payload[offset + column]) * quantizedInput[column];
            }

            destination[row] = (float)(sum * matrix.Scales[row] * scale);
        }
    }

    public static void SiLU(ReadOnlySpan<float> input, Span<float> destination)
    {
        ValidateUnary(input, destination);
        for (var index = 0; index < input.Length; index++)
        {
            destination[index] = (float)(input[index] / (1.0 + Math.Exp(-input[index])));
        }
    }

    public static void Sigmoid(ReadOnlySpan<float> input, Span<float> destination)
    {
        ValidateUnary(input, destination);
        for (var index = 0; index < input.Length; index++)
        {
            destination[index] = (float)(1.0 / (1.0 + Math.Exp(-input[index])));
        }
    }

    public static void Softmax(ReadOnlySpan<float> input, Span<float> destination)
    {
        ValidateUnary(input, destination);
        var maximum = float.NegativeInfinity;
        for (var index = 0; index < input.Length; index++)
        {
            if (!float.IsFinite(input[index]))
            {
                throw new ArgumentException(
                    $"Softmax input at index {index} must be finite.",
                    nameof(input));
            }

            maximum = Math.Max(maximum, input[index]);
        }

        double denominator = 0;
        for (var index = 0; index < input.Length; index++)
        {
            denominator += Math.Exp(input[index] - maximum);
        }

        for (var index = 0; index < input.Length; index++)
        {
            destination[index] = (float)(Math.Exp(input[index] - maximum) / denominator);
        }
    }

    public static void SoftmaxInPlace(Span<float> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Buffer cannot be empty.", nameof(values));
        }

        var maximum = float.NegativeInfinity;
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new ArgumentException(
                    $"Softmax input at index {index} must be finite.",
                    nameof(values));
            }

            maximum = Math.Max(maximum, values[index]);
        }

        double denominator = 0;
        for (var index = 0; index < values.Length; index++)
        {
            denominator += Math.Exp(values[index] - maximum);
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(Math.Exp(values[index] - maximum) / denominator);
        }
    }

    public static void Add(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination)
    {
        ValidateBinary(left, right, destination);
        for (var index = 0; index < left.Length; index++)
        {
            destination[index] = left[index] + right[index];
        }
    }

    public static void Multiply(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination)
    {
        ValidateBinary(left, right, destination);
        for (var index = 0; index < left.Length; index++)
        {
            destination[index] = left[index] * right[index];
        }
    }

    public static void AddResidual(Span<float> destination, ReadOnlySpan<float> residual)
    {
        RequireExactLength(residual.Length, destination.Length, nameof(residual));
        EnsureNoOverlap(residual, destination, nameof(residual), nameof(destination));
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] += residual[index];
        }
    }

    public static void TopK(
        ReadOnlySpan<float> values,
        int count,
        Span<int> selectedIndices,
        Span<float> selectedValues)
    {
        RequireNonEmpty(values, nameof(values));
        if (count <= 0 || count > values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        RequireExactLength(selectedIndices.Length, count, nameof(selectedIndices));
        RequireExactLength(selectedValues.Length, count, nameof(selectedValues));
        EnsureNoOverlap(values, selectedValues, nameof(values), nameof(selectedValues));

        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new ArgumentException(
                    $"Top-k input at index {index} must be finite.",
                    nameof(values));
            }
        }

        var selectedCount = 0;
        for (var candidateIndex = 0; candidateIndex < values.Length; candidateIndex++)
        {
            var candidateValue = values[candidateIndex];
            var insertionIndex = selectedCount;
            for (var selectedIndex = 0; selectedIndex < selectedCount; selectedIndex++)
            {
                if (RanksBefore(
                        candidateValue,
                        candidateIndex,
                        selectedValues[selectedIndex],
                        selectedIndices[selectedIndex]))
                {
                    insertionIndex = selectedIndex;
                    break;
                }
            }

            if (insertionIndex >= count)
            {
                continue;
            }

            var lastIndex = Math.Min(selectedCount, count - 1);
            for (var selectedIndex = lastIndex; selectedIndex > insertionIndex; selectedIndex--)
            {
                selectedValues[selectedIndex] = selectedValues[selectedIndex - 1];
                selectedIndices[selectedIndex] = selectedIndices[selectedIndex - 1];
            }

            selectedValues[insertionIndex] = candidateValue;
            selectedIndices[insertionIndex] = candidateIndex;
            if (selectedCount < count)
            {
                selectedCount++;
            }
        }
    }

    private static void DequantMatVec(
        QuantizedTensorView matrix,
        QuantizedTensorFormat expectedFormat,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ValidateDequantMatVec(matrix, expectedFormat, input, destination);

        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            double sum = 0;
            var scale = matrix.Scales[row];
            for (var column = 0; column < matrix.Shape.Columns; column++)
            {
                var dequantized = matrix.GetQuantizedValue(row, column) * scale;
                sum += (double)dequantized * input[column];
            }

            destination[row] = (float)sum;
        }
    }

    internal static void ValidateMatVec(
        ReadOnlySpan<float> matrix,
        int rows,
        int columns,
        int rowStride,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        ValidateMatrix(
            matrix.Length,
            rows,
            columns,
            rowStride,
            allowEmptyRows: false,
            nameof(matrix));
        RequireExactLength(input.Length, columns, nameof(input));
        RequireExactLength(destination.Length, rows, nameof(destination));
        EnsureNoOverlap(matrix, destination, nameof(matrix), nameof(destination));
        EnsureNoOverlap(input, destination, nameof(input), nameof(destination));
    }

    internal static void ValidateDequantMatVec(
        QuantizedTensorView matrix,
        QuantizedTensorFormat expectedFormat,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        if (matrix.Shape.Format != expectedFormat)
        {
            throw new ArgumentException(
                $"Quantized matrix format must be {expectedFormat}.",
                nameof(matrix));
        }

        RequireExactLength(input.Length, matrix.Shape.Columns, nameof(input));
        RequireExactLength(destination.Length, matrix.Shape.Rows, nameof(destination));
        EnsureNoOverlap(input, destination, nameof(input), nameof(destination));
        EnsureNoOverlap(matrix.Scales, destination, "matrix scales", nameof(destination));

        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            var scale = matrix.Scales[row];
            if (!float.IsFinite(scale) || scale <= 0)
            {
                throw new ArgumentException(
                    $"Quantized matrix scale at row {row} must be positive and finite.",
                    nameof(matrix));
            }
        }
    }

    private static void ValidateUnary(ReadOnlySpan<float> input, Span<float> destination)
    {
        RequireNonEmpty(input, nameof(input));
        RequireExactLength(destination.Length, input.Length, nameof(destination));
        EnsureNoOverlap(input, destination, nameof(input), nameof(destination));
    }

    private static void ValidateBinary(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        Span<float> destination)
    {
        RequireExactLength(right.Length, left.Length, nameof(right));
        RequireExactLength(destination.Length, left.Length, nameof(destination));
        EnsureNoOverlap(left, destination, nameof(left), nameof(destination));
        EnsureNoOverlap(right, destination, nameof(right), nameof(destination));
    }

    private static void ValidateMatrix(
        int bufferLength,
        int rows,
        int columns,
        int rowStride,
        bool allowEmptyRows,
        string parameterName)
    {
        if (rows < 0 || (!allowEmptyRows && rows == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rowStride < columns)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride));
        }

        var requiredLength = rows == 0
            ? 0
            : checked(((rows - 1) * rowStride) + columns);
        if (bufferLength < requiredLength)
        {
            throw new ArgumentException(
                $"Matrix buffer requires at least {requiredLength} elements, found {bufferLength}.",
                parameterName);
        }
    }

    private static void RequireNonEmpty(ReadOnlySpan<float> values, string parameterName)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Buffer cannot be empty.", parameterName);
        }
    }

    private static void RequireExactLength(int actual, int expected, string parameterName)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"Buffer must contain exactly {expected} elements, found {actual}.",
                parameterName);
        }
    }

    private static void RequirePositiveFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void EnsureNoOverlap(
        ReadOnlySpan<float> source,
        Span<float> destination,
        string sourceName,
        string destinationName)
    {
        if (source.Overlaps(destination))
        {
            throw new ArgumentException(
                $"{sourceName} and {destinationName} cannot overlap.",
                destinationName);
        }
    }

    private static bool RanksBefore(
        float candidateValue,
        int candidateIndex,
        float selectedValue,
        int selectedIndex)
        => candidateValue > selectedValue ||
           (candidateValue == selectedValue && candidateIndex < selectedIndex);
}
