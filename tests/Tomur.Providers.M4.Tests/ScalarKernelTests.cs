using Tomur.Providers.Glm;

namespace Tomur.Providers.M4.Tests;

public sealed class ScalarKernelTests
{
    [Fact]
    public void EmbeddingAndRmsNormMatchTinyFixtureOracle()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);
        var tensorPath = Path.Combine(fixturePath, TinyFixtureFiles.Tensors);
        var catalog = SafeTensorCatalog.Read([tensorPath]);
        using var source = new TensorDataSource(catalog);
        using var embeddings = source.LoadFloat32(catalog.GetRequired("model.embed_tokens.weight"));
        using var normWeight = source.LoadFloat32(catalog.GetRequired("model.layers.0.input_layernorm.weight"));

        var tokenIds = oracle.Tokenization.Single(item => item.Text == "hello Tomur").TokenIds.ToArray();
        var embeddingCheckpoint = oracle.GetCheckpoint("embedding.lookup");
        var embeddingOutput = new float[embeddingCheckpoint.Values.Count];
        ScalarKernels.GatherEmbeddings(
            embeddings.ReadOnlySpan,
            oracle.ModelConfiguration.VocabularySize,
            oracle.ModelConfiguration.HiddenSize,
            oracle.ModelConfiguration.HiddenSize,
            tokenIds,
            embeddingOutput,
            oracle.ModelConfiguration.HiddenSize);

        var normInput = oracle.GetCheckpoint("rms_norm.input").Values.ToArray();
        var normOutput = new float[normInput.Length];
        ScalarKernels.RmsNorm(normInput, normWeight.ReadOnlySpan, 0.00001f, normOutput);

        AssertClose(embeddingCheckpoint.Values, embeddingOutput, oracle.Tolerances);
        AssertClose(oracle.GetCheckpoint("rms_norm.output").Values, normOutput, oracle.Tolerances);
    }

    [Fact]
    public void LayerNormUsesPopulationVarianceAndAffineParameters()
    {
        float[] input = [1.0f, 2.0f, 3.0f, 4.0f];
        float[] weight = [1.0f, 0.5f, 2.0f, -1.0f];
        float[] bias = [0.1f, -0.2f, 0.3f, 0.4f];
        var expected = ReferenceLayerNorm(input, weight, bias, 0.00001f);
        var actual = new float[input.Length];

        ScalarKernels.LayerNorm(input, weight, bias, 0.00001f, actual);

        AssertClose(expected, actual, new TinyOracleTolerance(1e-6f, 1e-6f));
    }

    [Fact]
    public void F32MatVecAndMatMulRespectRowStridesAndEmptyBatch()
    {
        float[] matrix = [1.0f, 2.0f, 3.0f, 99.0f, -1.0f, 0.5f, 2.0f];
        float[] input = [2.0f, -1.0f, 0.5f];
        var matVecOutput = new float[2];

        ScalarKernels.MatVec(matrix, 2, 3, 4, input, matVecOutput);

        Assert.Equal(new[] { 1.5f, -1.5f }, matVecOutput);

        float[] left = [1.0f, 2.0f, 99.0f, 3.0f, 4.0f];
        float[] right = [5.0f, 6.0f, 7.0f, 99.0f, 8.0f, 9.0f, 10.0f];
        var matMulOutput = Enumerable.Repeat(-99.0f, 7).ToArray();

        ScalarKernels.MatMul(left, 2, 2, 3, right, 3, 4, matMulOutput, 4);

        Assert.Equal(new[] { 21.0f, 24.0f, 27.0f }, matMulOutput.AsSpan(0, 3).ToArray());
        Assert.Equal(-99.0f, matMulOutput[3]);
        Assert.Equal(new[] { 47.0f, 54.0f, 61.0f }, matMulOutput.AsSpan(4, 3).ToArray());
        ScalarKernels.MatMul(
            Array.Empty<float>(),
            0,
            2,
            2,
            right,
            3,
            4,
            Array.Empty<float>(),
            3);
    }

    [Fact]
    public void QuantizedMatVecMatchesOfflineDequantizationForInt8AndOddInt4()
    {
        var int8Shape = new QuantizedTensorShape(QuantizedTensorFormat.Int8, 2, 3);
        byte[] int8Payload = [0xfe, 0x00, 0x04, 0x03, 0xff, 0x02];
        float[] int8Scales = [0.5f, 2.0f];
        float[] input = [1.0f, -2.0f, 0.25f];
        var int8View = new QuantizedTensorView(int8Shape, int8Payload, int8Scales);
        var int8Expected = OfflineDequantMatVec(int8View, input);
        var int8Actual = new float[int8Shape.Rows];

        ScalarKernels.Int8DequantMatVec(int8View, input, int8Actual);

        Assert.Equal(int8Expected, int8Actual);

        var int4Shape = new QuantizedTensorShape(QuantizedTensorFormat.Int4, 2, 3);
        byte[] int4Payload = [0x78, 0x01, 0xef, 0x03];
        float[] int4Scales = [0.25f, 1.5f];
        var int4View = new QuantizedTensorView(int4Shape, int4Payload, int4Scales);
        var int4Expected = OfflineDequantMatVec(int4View, input);
        var int4Actual = new float[int4Shape.Rows];

        ScalarKernels.Int4DequantMatVec(int4View, input, int4Actual);

        Assert.Equal(int4Expected, int4Actual);
        Assert.Throws<ArgumentException>(() => InvokeInt4KernelWithInt8Matrix(int8Payload, int8Scales, input));
    }

    [Fact]
    public void OffsetBinaryInt4UsesMinusEightNibbleBias()
    {
        var shape = new QuantizedTensorShape(
            QuantizedTensorFormat.Int4,
            rows: 1,
            columns: 4,
            QuantizedValueEncoding.OffsetBinary);
        byte[] payload = [0x80, 0xf7];
        var view = new QuantizedTensorView(shape, payload, [0.5f]);
        float[] output = [0];

        ScalarKernels.Int4DequantMatVec(view, [1, 1, 1, 1], output);

        int[] values =
        [
            view.GetQuantizedValue(0, 0),
            view.GetQuantizedValue(0, 1),
            view.GetQuantizedValue(0, 2),
            view.GetQuantizedValue(0, 3)
        ];
        Assert.Equal(new[] { -8, 0, -1, 7 }, values);
        Assert.Equal(-1.0f, output[0]);
    }

    [Fact]
    public void ActivationQuantizationUsesTiesToEvenAndStableZeroScale()
    {
        float[] input = [127.0f, 2.5f, 3.5f, -2.5f, -3.5f, 0.0f];
        var quantized = new sbyte[input.Length];

        var scale = ScalarKernels.QuantizeActivationToInt8(input, quantized);

        Assert.Equal(1.0f, scale);
        Assert.Equal(new sbyte[] { 127, 2, 4, -2, -4, 0 }, quantized);

        var zeroOutput = new sbyte[3];
        var zeroScale = ScalarKernels.QuantizeActivationToInt8(new float[3], zeroOutput);
        Assert.Equal(1.0f, zeroScale);
        Assert.Equal(new sbyte[3], zeroOutput);
    }

    [Fact]
    public void ActivationsSoftmaxAndElementwiseOperationsUseDeclaredAliasingRules()
    {
        float[] input = [-2.0f, 0.0f, 2.0f];
        var sigmoid = new float[input.Length];
        var silu = new float[input.Length];
        ScalarKernels.Sigmoid(input, sigmoid);
        ScalarKernels.SiLU(input, silu);

        for (var index = 0; index < input.Length; index++)
        {
            var expectedSigmoid = (float)(1.0 / (1.0 + Math.Exp(-input[index])));
            Assert.Equal(expectedSigmoid, sigmoid[index]);
            var expectedSilu = (float)(input[index] / (1.0 + Math.Exp(-input[index])));
            Assert.Equal(expectedSilu, silu[index]);
        }

        float[] logits = [1000.0f, 1001.0f, 999.0f];
        var probabilities = new float[logits.Length];
        ScalarKernels.Softmax(logits, probabilities);
        Assert.InRange(probabilities.Sum(), 0.999999f, 1.000001f);
        Assert.True(probabilities[1] > probabilities[0]);
        Assert.True(probabilities[0] > probabilities[2]);

        float[] left = [1.0f, 2.0f, 3.0f];
        float[] right = [4.0f, 5.0f, 6.0f];
        var destination = new float[3];
        ScalarKernels.Add(left, right, destination);
        Assert.Equal(new[] { 5.0f, 7.0f, 9.0f }, destination);
        ScalarKernels.Multiply(left, right, destination);
        Assert.Equal(new[] { 4.0f, 10.0f, 18.0f }, destination);
        ScalarKernels.AddResidual(destination, right);
        Assert.Equal(new[] { 8.0f, 15.0f, 24.0f }, destination);

        Assert.Throws<ArgumentException>(() => ScalarKernels.Add(left, right, left));
        Assert.Throws<ArgumentException>(() => ScalarKernels.Softmax(logits, logits));
    }

    [Fact]
    public void TopKSortsDescendingAndBreaksTiesByLowerSourceIndex()
    {
        float[] values = [0.5f, 1.0f, 1.0f, -1.0f, 1.0f];
        var indices = new int[3];
        var selected = new float[3];

        ScalarKernels.TopK(values, 3, indices, selected);

        Assert.Equal(new[] { 1, 2, 4 }, indices);
        Assert.Equal(new[] { 1.0f, 1.0f, 1.0f }, selected);
    }

    [Fact]
    public void InvalidShapesStridesAliasesAndNonFiniteInputsFailExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScalarKernels.GatherEmbeddings(new float[8], 2, 4, 3, new[] { 0 }, new float[4], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScalarKernels.GatherEmbeddings(new float[8], 2, 4, 4, new[] { 2 }, new float[4], 4));
        Assert.Throws<ArgumentException>(() =>
            ScalarKernels.MatVec(new float[5], 2, 3, 3, new float[3], new float[2]));
        Assert.Throws<ArgumentException>(() =>
            ScalarKernels.Softmax(new[] { 0.0f, float.NaN }, new float[2]));
        Assert.Throws<ArgumentException>(() =>
            ScalarKernels.QuantizeActivationToInt8(new[] { float.PositiveInfinity }, new sbyte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScalarKernels.TopK(new[] { 1.0f }, 2, new int[2], new float[2]));

        var norm = new[] { 1.0f, 2.0f };
        Assert.Throws<ArgumentException>(() =>
            ScalarKernels.RmsNorm(norm, new[] { 1.0f, 1.0f }, 0.00001f, norm));
    }

    private static float[] OfflineDequantMatVec(QuantizedTensorView matrix, ReadOnlySpan<float> input)
    {
        var dequantized = new float[matrix.Shape.Rows * matrix.Shape.Columns];
        for (var row = 0; row < matrix.Shape.Rows; row++)
        {
            for (var column = 0; column < matrix.Shape.Columns; column++)
            {
                dequantized[(row * matrix.Shape.Columns) + column] =
                    matrix.GetQuantizedValue(row, column) * matrix.Scales[row];
            }
        }

        var output = new float[matrix.Shape.Rows];
        ScalarKernels.MatVec(
            dequantized,
            matrix.Shape.Rows,
            matrix.Shape.Columns,
            matrix.Shape.Columns,
            input,
            output);
        return output;
    }

    private static void InvokeInt4KernelWithInt8Matrix(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<float> scales,
        ReadOnlySpan<float> input)
    {
        var shape = new QuantizedTensorShape(QuantizedTensorFormat.Int8, 2, 3);
        var matrix = new QuantizedTensorView(shape, payload, scales);
        ScalarKernels.Int4DequantMatVec(matrix, input, new float[shape.Rows]);
    }

    private static float[] ReferenceLayerNorm(
        IReadOnlyList<float> input,
        IReadOnlyList<float> weight,
        IReadOnlyList<float> bias,
        float epsilon)
    {
        var mean = input.Sum(static value => (double)value) / input.Count;
        var variance = input.Sum(value =>
        {
            var difference = value - mean;
            return difference * difference;
        }) / input.Count;
        var scale = 1.0 / Math.Sqrt(variance + epsilon);
        return input.Select((value, index) =>
                (float)(((value - mean) * scale * weight[index]) + bias[index]))
            .ToArray();
    }

    private static void AssertClose(
        IReadOnlyList<float> expected,
        IReadOnlyList<float> actual,
        TinyOracleTolerance tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var error = Math.Abs(expected[index] - actual[index]);
            var limit = tolerance.Absolute + (tolerance.Relative * Math.Abs(expected[index]));
            Assert.True(
                error <= limit,
                $"Value {index} differs: expected {expected[index]}, actual {actual[index]}, limit {limit}.");
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m4-{Guid.NewGuid():N}");
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
