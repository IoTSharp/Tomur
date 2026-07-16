using System.Numerics;
using Tomur.Providers.Glm;

namespace Tomur.Providers.M11.Tests;

public sealed class OptimizedKernelTests
{
    [Fact]
    public void F32SimdAndParallelPathsMatchScalarReference()
    {
        var columns = checked((Vector<float>.Count * 2) + 3);
        const int rows = 17;
        var matrix = CreateValues(rows * columns, 17);
        var input = CreateValues(columns, 31);
        var expected = new float[rows];
        var simd = new float[rows];
        var parallel = new float[rows];

        ScalarKernels.MatVec(matrix, rows, columns, columns, input, expected);
        OptimizedKernels.MatVec(
            matrix,
            rows,
            columns,
            columns,
            input,
            simd,
            new KernelExecutionOptions(EnableParallel: false));
        OptimizedKernels.MatVec(
            matrix,
            rows,
            columns,
            columns,
            input,
            parallel,
            new KernelExecutionOptions(
                EnableParallel: true,
                ParallelRowThreshold: 1,
                ParallelWorkThreshold: 1,
                MaxDegreeOfParallelism: 2));

        AssertClose(expected, simd);
        AssertClose(expected, parallel);
    }

    [Fact]
    public void ScalarPolicyProvidesExactFallback()
    {
        float[] matrix = [1, 2, 3, 4, -2, 1, 0.5f, 8];
        float[] input = [0.5f, -1, 2, 0.25f];
        var expected = new float[2];
        var actual = new float[2];

        ScalarKernels.MatVec(matrix, 2, 4, 4, input, expected);
        OptimizedKernels.MatVec(
            matrix,
            2,
            4,
            4,
            input,
            actual,
            new KernelExecutionOptions(EnableSimd: false, EnableParallel: false));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Int8AndPackedInt4SimdPathsMatchScalarReference()
    {
        var columns = checked(Vector<float>.Count + 3);
        const int rows = 5;
        var input = CreateValues(columns, 47);
        var scales = Enumerable.Range(0, rows).Select(index => 0.01f * (index + 1)).ToArray();
        var int8Payload = new byte[rows * columns];
        for (var index = 0; index < int8Payload.Length; index++)
        {
            int8Payload[index] = unchecked((byte)(sbyte)((index % 31) - 15));
        }

        var int8 = new QuantizedTensorView(
            new QuantizedTensorShape(QuantizedTensorFormat.Int8, rows, columns),
            int8Payload,
            scales);
        var int8Expected = new float[rows];
        var int8Actual = new float[rows];
        ScalarKernels.Int8DequantMatVec(int8, input, int8Expected);
        OptimizedKernels.Int8DequantMatVec(int8, input, int8Actual);
        AssertClose(int8Expected, int8Actual);

        var storedColumns = (columns + 1) / 2;
        var int4Payload = new byte[rows * storedColumns];
        for (var index = 0; index < int4Payload.Length; index++)
        {
            int4Payload[index] = (byte)(((index + 7) & 0x0f) | (((index + 11) & 0x0f) << 4));
        }

        var int4 = new QuantizedTensorView(
            new QuantizedTensorShape(
                QuantizedTensorFormat.Int4,
                rows,
                columns,
                QuantizedValueEncoding.OffsetBinary),
            int4Payload,
            scales);
        var int4Expected = new float[rows];
        var int4Actual = new float[rows];
        ScalarKernels.Int4DequantMatVec(int4, input, int4Expected);
        OptimizedKernels.Int4DequantMatVec(int4, input, int4Actual);
        AssertClose(int4Expected, int4Actual);
    }

    [Fact]
    public void ActivationInt8IntegerDotMatchesReferenceWithinQuantizationError()
    {
        const int rows = 3;
        const int columns = 11;
        var input = CreateValues(columns, 71);
        var payload = new byte[rows * columns];
        var scales = new[] { 0.02f, 0.05f, 0.1f };
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)(sbyte)((index % 17) - 8));
        }

        var matrix = new QuantizedTensorView(
            new QuantizedTensorShape(QuantizedTensorFormat.Int8, rows, columns),
            payload,
            scales);
        var quantized = new sbyte[columns];
        var expected = new float[rows];
        var actual = new float[rows];

        ScalarKernels.Int8DequantMatVec(matrix, input, expected);
        OptimizedKernels.Int8ActivationDot(matrix, input, quantized, actual);

        for (var row = 0; row < rows; row++)
        {
            Assert.InRange(Math.Abs(expected[row] - actual[row]), 0, Math.Max(0.25f, Math.Abs(expected[row]) * 0.15f));
        }
    }

    [Fact]
    public void PairedDispatchMatchesIndependentProjections()
    {
        var columns = checked(Vector<float>.Count + 1);
        const int rows = 7;
        var first = CreateValues(rows * columns, 3);
        var second = CreateValues(rows * columns, 5);
        var input = CreateValues(columns, 7);
        var expectedFirst = new float[rows];
        var expectedSecond = new float[rows];
        var actualFirst = new float[rows];
        var actualSecond = new float[rows];

        ScalarKernels.MatVec(first, rows, columns, columns, input, expectedFirst);
        ScalarKernels.MatVec(second, rows, columns, columns, input, expectedSecond);
        OptimizedKernels.MatVecPair(
            first,
            second,
            rows,
            columns,
            columns,
            input,
            actualFirst,
            actualSecond);

        AssertClose(expectedFirst, actualFirst);
        AssertClose(expectedSecond, actualSecond);
    }

    private static float[] CreateValues(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => (float)((random.NextDouble() * 2) - 1))
            .ToArray();
    }

    private static void AssertClose(IReadOnlyList<float> expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var tolerance = 1e-4f + (1e-4f * Math.Abs(expected[index]));
            Assert.True(
                Math.Abs(expected[index] - actual[index]) <= tolerance,
                $"Value {index} differs: expected {expected[index]}, actual {actual[index]}.");
        }
    }
}
