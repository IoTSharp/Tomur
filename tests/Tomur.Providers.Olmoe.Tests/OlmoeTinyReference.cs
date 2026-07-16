namespace Tomur.Providers.Olmoe.Tests;

internal sealed record OlmoeTinyOracle(
    IReadOnlyList<int> PromptTokenIds,
    IReadOnlyList<OlmoeReferenceStep> TeacherForcing,
    IReadOnlyList<int> GreedyTokenIds,
    float AbsoluteTolerance,
    float RelativeTolerance);

internal sealed record OlmoeReferenceStep(
    int Position,
    int InputTokenId,
    IReadOnlyList<float> Embedding,
    IReadOnlyList<float> AttentionInput,
    IReadOnlyList<float> AttentionOutput,
    IReadOnlyList<float> RouterInput,
    IReadOnlyList<int> SelectedExpertIds,
    IReadOnlyList<float> RouterWeights,
    IReadOnlyList<float> MoeOutput,
    IReadOnlyList<float> Logits);

internal static class OlmoeTinyReference
{
    private const int HiddenSize = 4;
    private const int AttentionHeadCount = 2;
    private const int KeyValueHeadCount = 2;
    private const int HeadSize = 2;
    private const int RoutedExpertCount = 2;
    private const int ExpertsPerToken = 1;
    private const int IntermediateSize = 3;
    private const int VocabularySize = 8;
    private const float RmsNormEpsilon = 0.00001f;

    public static OlmoeTinyOracle Create(OlmoeFixture fixture)
    {
        int[] promptTokenIds = [4, 5, 6];
        var teacherState = new ReferenceState(fixture.LogicalWeights);
        var teacherForcing = promptTokenIds
            .Select(teacherState.Forward)
            .ToArray();

        const int maxNewTokens = 4;
        var greedyState = new ReferenceState(fixture.LogicalWeights);
        OlmoeReferenceStep? last = null;
        foreach (var tokenId in promptTokenIds)
        {
            last = greedyState.Forward(tokenId);
        }

        var generated = new List<int>(maxNewTokens);
        for (var step = 0; step < maxNewTokens; step++)
        {
            var tokenId = ArgMax(last!.Logits);
            generated.Add(tokenId);
            if (step + 1 < maxNewTokens)
            {
                last = greedyState.Forward(tokenId);
            }
        }

        return new OlmoeTinyOracle(
            promptTokenIds,
            teacherForcing,
            generated,
            AbsoluteTolerance: 1e-5f,
            RelativeTolerance: 1e-4f);
    }

    private sealed class ReferenceState(IReadOnlyDictionary<string, float[]> weights)
    {
        private readonly List<float[]> keys = [];
        private readonly List<float[]> values = [];

        public OlmoeReferenceStep Forward(int tokenId)
        {
            var position = keys.Count;
            var embedding = Lookup(Get("model.embed_tokens.weight"), tokenId);
            var attentionInput = RmsNorm(
                embedding,
                Get("model.layers.0.input_layernorm.weight"));
            var attentionOutput = RunAttention(attentionInput, position);
            var attentionResidual = Add(embedding, attentionOutput);
            var routerInput = RmsNorm(
                attentionResidual,
                Get("model.layers.0.post_attention_layernorm.weight"));
            var moe = RunMoe(routerInput);
            var layerOutput = Add(attentionResidual, moe.Output);
            var normalized = RmsNorm(layerOutput, Get("model.norm.weight"));
            var logits = MatrixVector(Get("lm_head.weight"), VocabularySize, HiddenSize, normalized);

            return new OlmoeReferenceStep(
                position,
                tokenId,
                embedding,
                attentionInput,
                attentionOutput,
                routerInput,
                moe.ExpertIds,
                moe.Weights,
                moe.Output,
                logits);
        }

        private float[] RunAttention(float[] input, int position)
        {
            var query = MatrixVector(
                Get("model.layers.0.self_attn.q_proj.weight"),
                HiddenSize,
                HiddenSize,
                input);
            var key = MatrixVector(
                Get("model.layers.0.self_attn.k_proj.weight"),
                HiddenSize,
                HiddenSize,
                input);
            var value = MatrixVector(
                Get("model.layers.0.self_attn.v_proj.weight"),
                HiddenSize,
                HiddenSize,
                input);
            query = RmsNorm(query, Get("model.layers.0.self_attn.q_norm.weight"));
            key = RmsNorm(key, Get("model.layers.0.self_attn.k_norm.weight"));
            RotateHeads(query, AttentionHeadCount, position);
            RotateHeads(key, KeyValueHeadCount, position);

            var context = new float[HiddenSize];
            for (var queryHead = 0; queryHead < AttentionHeadCount; queryHead++)
            {
                var keyValueHead = queryHead;
                var queryOffset = queryHead * HeadSize;
                var keyValueOffset = keyValueHead * HeadSize;
                var scores = new float[position + 1];
                for (var tokenIndex = 0; tokenIndex <= position; tokenIndex++)
                {
                    var candidate = tokenIndex < keys.Count ? keys[tokenIndex] : key;
                    double dot = 0;
                    for (var component = 0; component < HeadSize; component++)
                    {
                        dot += (double)query[queryOffset + component] * candidate[keyValueOffset + component];
                    }

                    scores[tokenIndex] = (float)(dot / Math.Sqrt(HeadSize));
                }

                var probabilities = Softmax(scores);
                for (var tokenIndex = 0; tokenIndex <= position; tokenIndex++)
                {
                    var candidate = tokenIndex < values.Count ? values[tokenIndex] : value;
                    for (var component = 0; component < HeadSize; component++)
                    {
                        context[queryOffset + component] +=
                            probabilities[tokenIndex] * candidate[keyValueOffset + component];
                    }
                }
            }

            keys.Add(key);
            values.Add(value);
            return MatrixVector(
                Get("model.layers.0.self_attn.o_proj.weight"),
                HiddenSize,
                HiddenSize,
                context);
        }

        private MoeResult RunMoe(float[] input)
        {
            var routerLogits = MatrixVector(
                Get("model.layers.0.mlp.gate.weight"),
                RoutedExpertCount,
                HiddenSize,
                input);
            var probabilities = Softmax(routerLogits);
            var expertIds = Enumerable.Range(0, RoutedExpertCount)
                .OrderByDescending(index => probabilities[index])
                .ThenBy(static index => index)
                .Take(ExpertsPerToken)
                .ToArray();
            var selectedWeights = expertIds.Select(index => probabilities[index]).ToArray();
            var output = new float[HiddenSize];
            for (var route = 0; route < expertIds.Length; route++)
            {
                var expertOutput = RunExpert(input, expertIds[route]);
                for (var component = 0; component < output.Length; component++)
                {
                    output[component] += selectedWeights[route] * expertOutput[component];
                }
            }

            return new MoeResult(expertIds, selectedWeights, output);
        }

        private float[] RunExpert(float[] input, int expertId)
        {
            var prefix = $"model.layers.0.mlp.experts.{expertId}.";
            var gate = MatrixVector(
                Get($"{prefix}gate_proj.weight"),
                IntermediateSize,
                HiddenSize,
                input);
            var up = MatrixVector(
                Get($"{prefix}up_proj.weight"),
                IntermediateSize,
                HiddenSize,
                input);
            var activated = new float[IntermediateSize];
            for (var index = 0; index < activated.Length; index++)
            {
                var silu = gate[index] / (1.0 + Math.Exp(-gate[index]));
                activated[index] = (float)(silu * up[index]);
            }

            return MatrixVector(
                Get($"{prefix}down_proj.weight"),
                HiddenSize,
                IntermediateSize,
                activated);
        }

        private float[] Get(string name)
            => weights.TryGetValue(name, out var weight)
                ? weight
                : throw new InvalidDataException($"Tiny OLMoE reference tensor is missing: {name}");
    }

    private static float[] Lookup(float[] embeddings, int tokenId)
    {
        if ((uint)tokenId >= (uint)VocabularySize)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenId));
        }

        return embeddings.AsSpan(tokenId * HiddenSize, HiddenSize).ToArray();
    }

    private static float[] MatrixVector(
        float[] matrix,
        int rows,
        int columns,
        IReadOnlyList<float> input)
    {
        if (matrix.Length != checked(rows * columns) || input.Count != columns)
        {
            throw new InvalidDataException("Tiny OLMoE reference matrix dimensions are invalid.");
        }

        var output = new float[rows];
        for (var row = 0; row < rows; row++)
        {
            double sum = 0;
            for (var column = 0; column < columns; column++)
            {
                sum += (double)matrix[(row * columns) + column] * input[column];
            }

            output[row] = (float)sum;
        }

        return output;
    }

    private static float[] RmsNorm(IReadOnlyList<float> input, IReadOnlyList<float> weight)
    {
        double sum = 0;
        for (var index = 0; index < input.Count; index++)
        {
            sum += (double)input[index] * input[index];
        }

        var scale = 1.0 / Math.Sqrt((sum / input.Count) + RmsNormEpsilon);
        var output = new float[input.Count];
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (float)(input[index] * scale * weight[index]);
        }

        return output;
    }

    private static void RotateHeads(float[] values, int headCount, int position)
    {
        for (var head = 0; head < headCount; head++)
        {
            var offset = head * HeadSize;
            var first = values[offset];
            var second = values[offset + 1];
            var angle = (double)position;
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            values[offset] = (float)((first * cosine) - (second * sine));
            values[offset + 1] = (float)((second * cosine) + (first * sine));
        }
    }

    private static float[] Softmax(IReadOnlyList<float> values)
    {
        var maximum = values.Max();
        var exponentials = values.Select(value => Math.Exp(value - maximum)).ToArray();
        var denominator = exponentials.Sum();
        return exponentials.Select(value => (float)(value / denominator)).ToArray();
    }

    private static float[] Add(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var result = new float[left.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = left[index] + right[index];
        }

        return result;
    }

    private static int ArgMax(IReadOnlyList<float> values)
    {
        var best = 0;
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] > values[best])
            {
                best = index;
            }
        }

        return best;
    }

    private sealed record MoeResult(int[] ExpertIds, float[] Weights, float[] Output);
}
