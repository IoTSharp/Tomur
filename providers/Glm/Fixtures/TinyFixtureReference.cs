namespace Tomur.Providers.Glm;

internal static class TinyFixtureReference
{
    private const int HiddenSize = 4;
    private const int RoutedExpertCount = 3;
    private const int ExpertsPerToken = 2;
    private const int DenseIntermediateSize = 6;
    private const int VocabularySize = 12;
    private const float RmsNormEpsilon = 0.00001f;

    public static TinyOracle Create(IReadOnlyList<TinyTensor> tensors, string configurationSha256)
    {
        var weights = tensors.ToDictionary(static tensor => tensor.Name, StringComparer.Ordinal);
        var promptTokenIds = new[] { 1, 4, 6 };
        var checkpoints = new List<TinyOracleCheckpoint>();

        var embeddings = promptTokenIds
            .SelectMany(tokenId => Lookup(Get(weights, "model.embed_tokens.weight"), tokenId, HiddenSize))
            .ToArray();
        AddCheckpoint(checkpoints, "embedding.lookup", [promptTokenIds.Length, HiddenSize], embeddings);

        float[] normInput = [0.25f, -0.5f, 0.75f, -1.0f];
        var normOutput = RmsNorm(
            normInput,
            Get(weights, "model.layers.0.input_layernorm.weight"));
        AddCheckpoint(checkpoints, "rms_norm.input", [HiddenSize], normInput);
        AddCheckpoint(checkpoints, "rms_norm.output", [HiddenSize], normOutput);
        AddCheckpoint(checkpoints, "dense_mlp.input", [HiddenSize], normOutput);
        AddCheckpoint(
            checkpoints,
            "dense_mlp.output",
            [HiddenSize],
            RunDenseMlp(normOutput, weights));

        var teacherForcing = ForwardSequence(promptTokenIds, weights);
        var captured = teacherForcing[^1];
        AddCheckpoint(checkpoints, "attention.q_latent", [2], captured.Attention.QueryLatent);
        AddCheckpoint(checkpoints, "attention.q_normalized", [2], captured.Attention.NormalizedQueryLatent);
        AddCheckpoint(checkpoints, "attention.kv_latent", [2], captured.Attention.KeyValueLatent);
        AddCheckpoint(checkpoints, "attention.kv_normalized", [2], captured.Attention.NormalizedKeyValueLatent);
        AddCheckpoint(checkpoints, "attention.query", [4], captured.Attention.Query);
        AddCheckpoint(checkpoints, "attention.key", [4], captured.Attention.Key);
        AddCheckpoint(checkpoints, "attention.value", [2], captured.Attention.Value);
        AddCheckpoint(checkpoints, "attention.scores", [captured.Attention.Scores.Length], captured.Attention.Scores);
        AddCheckpoint(
            checkpoints,
            "attention.probabilities",
            [captured.Attention.Probabilities.Length],
            captured.Attention.Probabilities);
        AddCheckpoint(checkpoints, "attention.output", [HiddenSize], captured.Attention.Output);
        AddCheckpoint(checkpoints, "router.input", [HiddenSize], captured.Router.Input);
        AddCheckpoint(checkpoints, "router.scores", [RoutedExpertCount], captured.Router.Scores);
        AddCheckpoint(
            checkpoints,
            "router.adjusted_scores",
            [RoutedExpertCount],
            captured.Router.AdjustedScores);
        AddCheckpoint(checkpoints, "router.weights", [ExpertsPerToken], captured.Router.Weights);
        AddCheckpoint(checkpoints, "router.output", [HiddenSize], captured.Router.Output);
        AddCheckpoint(checkpoints, "moe.output", [HiddenSize], captured.MoeOutput);
        AddCheckpoint(checkpoints, "layer.output", [HiddenSize], captured.LayerOutput);

        var teacherOracle = teacherForcing
            .Select(step => new TinyTeacherForcingStep(step.Position, step.InputTokenId, step.Logits))
            .ToArray();

        const int maxNewTokens = 4;
        var generated = new List<int>(maxNewTokens);
        var sequence = promptTokenIds.ToList();
        for (var step = 0; step < maxNewTokens; step++)
        {
            var positions = ForwardSequence(sequence, weights);
            var tokenId = ArgMax(positions[^1].Logits);
            generated.Add(tokenId);
            sequence.Add(tokenId);
        }

        return new TinyOracle(
            TinyFixtureBundle.SchemaVersion,
            TinyFixtureBundle.FixtureId,
            new TinyOracleGenerator(
                "managed-glm-scalar-reference",
                TinyFixtureBundle.GeneratorVersion,
                "F32 checkpoints with F64 accumulators",
                "IEEE 754 round-to-nearest, ties-to-even"),
            new TinyOracleConfiguration(
                configurationSha256,
                HiddenSize,
                1,
                1,
                RoutedExpertCount,
                ExpertsPerToken,
                VocabularySize,
                16),
            new TinyOracleTolerance(1e-5f, 1e-4f),
            CreateTokenizationCases(),
            checkpoints,
            new TinyRouterOracle(captured.Router.ExpertIds, captured.Router.Weights),
            teacherOracle,
            new TinyGreedyOracle(promptTokenIds, maxNewTokens, generated));
    }

    private static IReadOnlyList<TinyTokenizationCase> CreateTokenizationCases()
        =>
        [
            new("hello Tomur", true, [1, 4, 6]),
            new("本地 AI", true, [1, 7, 8]),
            new("C# !", true, [1, 10, 9]),
            new("🙂", true, [1, 11]),
            new("missing", true, [1, 3])
        ];

    private static IReadOnlyList<PositionResult> ForwardSequence(
        IReadOnlyList<int> tokenIds,
        IReadOnlyDictionary<string, TinyTensor> weights)
    {
        var keys = new List<float[]>(tokenIds.Count);
        var values = new List<float[]>(tokenIds.Count);
        var results = new List<PositionResult>(tokenIds.Count);
        for (var position = 0; position < tokenIds.Count; position++)
        {
            var embedding = Lookup(Get(weights, "model.embed_tokens.weight"), tokenIds[position], HiddenSize);
            var normalizedInput = RmsNorm(
                embedding,
                Get(weights, "model.layers.0.input_layernorm.weight"));
            var attention = RunAttention(normalizedInput, position, keys, values, weights);
            keys.Add(attention.Key);
            values.Add(attention.Value);

            var attentionResidual = Add(embedding, attention.Output);
            var routerInput = RmsNorm(
                attentionResidual,
                Get(weights, "model.layers.0.post_attention_layernorm.weight"));
            var (router, moeOutput) = RunMoe(routerInput, weights);
            var layerOutput = Add(attentionResidual, moeOutput);
            var finalOutput = RmsNorm(layerOutput, Get(weights, "model.norm.weight"));
            var logits = MatrixVector(
                Get(weights, "lm_head.weight"),
                VocabularySize,
                HiddenSize,
                finalOutput);
            results.Add(new PositionResult(
                position,
                tokenIds[position],
                attention,
                router,
                moeOutput,
                layerOutput,
                logits));
        }

        return results;
    }

    private static AttentionResult RunAttention(
        float[] input,
        int position,
        IReadOnlyList<float[]> existingKeys,
        IReadOnlyList<float[]> existingValues,
        IReadOnlyDictionary<string, TinyTensor> weights)
    {
        var queryLatent = MatrixVector(
            Get(weights, "model.layers.0.self_attn.q_a_proj.weight"),
            2,
            HiddenSize,
            input);
        var normalizedQueryLatent = RmsNorm(
            queryLatent,
            Get(weights, "model.layers.0.self_attn.q_a_layernorm.weight"));
        var queryProjection = MatrixVector(
            Get(weights, "model.layers.0.self_attn.q_b_proj.weight"),
            4,
            2,
            normalizedQueryLatent);
        var rotatedQuery = ApplyRope(queryProjection[2..4], position);
        float[] query = [queryProjection[0], queryProjection[1], rotatedQuery[0], rotatedQuery[1]];

        var keyValueProjection = MatrixVector(
            Get(weights, "model.layers.0.self_attn.kv_a_proj_with_mqa.weight"),
            4,
            HiddenSize,
            input);
        var keyValueLatent = keyValueProjection[0..2];
        var normalizedKeyValueLatent = RmsNorm(
            keyValueLatent,
            Get(weights, "model.layers.0.self_attn.kv_a_layernorm.weight"));
        var keyValueExpansion = MatrixVector(
            Get(weights, "model.layers.0.self_attn.kv_b_proj.weight"),
            4,
            2,
            normalizedKeyValueLatent);
        var rotatedKey = ApplyRope(keyValueProjection[2..4], position);
        float[] key = [keyValueExpansion[0], keyValueExpansion[1], rotatedKey[0], rotatedKey[1]];
        float[] value = [keyValueExpansion[2], keyValueExpansion[3]];

        var scores = new float[existingKeys.Count + 1];
        for (var index = 0; index < scores.Length; index++)
        {
            var candidate = index < existingKeys.Count ? existingKeys[index] : key;
            scores[index] = (float)(Dot(query, candidate) / Math.Sqrt(query.Length));
        }

        var probabilities = Softmax(scores);
        var context = new float[value.Length];
        for (var component = 0; component < context.Length; component++)
        {
            double sum = 0;
            for (var index = 0; index < probabilities.Length; index++)
            {
                var candidate = index < existingValues.Count ? existingValues[index] : value;
                sum += (double)probabilities[index] * candidate[component];
            }

            context[component] = (float)sum;
        }

        var output = MatrixVector(
            Get(weights, "model.layers.0.self_attn.o_proj.weight"),
            HiddenSize,
            2,
            context);
        return new AttentionResult(
            queryLatent,
            normalizedQueryLatent,
            keyValueLatent,
            normalizedKeyValueLatent,
            query,
            key,
            value,
            scores,
            probabilities,
            output);
    }

    private static (RouterResult Router, float[] MoeOutput) RunMoe(
        float[] input,
        IReadOnlyDictionary<string, TinyTensor> weights)
    {
        var rawScores = MatrixVector(
            Get(weights, "model.layers.0.mlp.gate.weight"),
            RoutedExpertCount,
            HiddenSize,
            input);
        var scores = rawScores.Select(static value => (float)(1.0 / (1.0 + Math.Exp(-value)))).ToArray();
        var correction = Get(weights, "model.layers.0.mlp.gate.e_score_correction_bias");
        var adjustedScores = scores.Zip(correction, static (score, bias) => score + bias).ToArray();
        var expertIds = Enumerable.Range(0, RoutedExpertCount)
            .OrderByDescending(index => adjustedScores[index])
            .ThenBy(static index => index)
            .Take(ExpertsPerToken)
            .ToArray();
        var selectedSum = expertIds.Sum(index => (double)scores[index]);
        var routingWeights = expertIds.Select(index => (float)(scores[index] / selectedSum)).ToArray();

        var expertOutputs = expertIds
            .Select(expertId => RunExpert(input, $"model.layers.0.mlp.experts.{expertId}.", weights))
            .ToArray();
        var routedOutput = new float[HiddenSize];
        for (var component = 0; component < routedOutput.Length; component++)
        {
            double sum = 0;
            for (var route = 0; route < expertIds.Length; route++)
            {
                sum += (double)routingWeights[route] * expertOutputs[route][component];
            }

            routedOutput[component] = (float)sum;
        }

        var sharedOutput = RunExpert(input, "model.layers.0.mlp.shared_experts.", weights);
        var moeOutput = Add(routedOutput, sharedOutput);
        return (
            new RouterResult(input, scores, adjustedScores, expertIds, routingWeights, routedOutput),
            moeOutput);
    }

    private static float[] RunExpert(
        float[] input,
        string prefix,
        IReadOnlyDictionary<string, TinyTensor> weights)
    {
        var gate = MatrixVector(Get(weights, $"{prefix}gate_proj.weight"), 3, HiddenSize, input);
        var up = MatrixVector(Get(weights, $"{prefix}up_proj.weight"), 3, HiddenSize, input);
        var activated = new float[gate.Length];
        for (var index = 0; index < activated.Length; index++)
        {
            var silu = gate[index] / (1.0 + Math.Exp(-gate[index]));
            activated[index] = (float)(silu * up[index]);
        }

        return MatrixVector(Get(weights, $"{prefix}down_proj.weight"), HiddenSize, 3, activated);
    }

    private static float[] RunDenseMlp(
        float[] input,
        IReadOnlyDictionary<string, TinyTensor> weights)
    {
        const string prefix = "model.layers.0.mlp.";
        var gate = MatrixVector(
            Get(weights, $"{prefix}gate_proj.weight"),
            DenseIntermediateSize,
            HiddenSize,
            input);
        var up = MatrixVector(
            Get(weights, $"{prefix}up_proj.weight"),
            DenseIntermediateSize,
            HiddenSize,
            input);
        var activated = new float[DenseIntermediateSize];
        for (var index = 0; index < activated.Length; index++)
        {
            var silu = (float)(gate[index] / (1.0 + Math.Exp(-gate[index])));
            activated[index] = silu * up[index];
        }

        return MatrixVector(
            Get(weights, $"{prefix}down_proj.weight"),
            HiddenSize,
            DenseIntermediateSize,
            activated);
    }

    private static float[] RmsNorm(float[] input, float[] weight)
    {
        var sum = input.Sum(static value => (double)value * value);
        var scale = 1.0 / Math.Sqrt(sum / input.Length + RmsNormEpsilon);
        var output = new float[input.Length];
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (float)(input[index] * scale * weight[index]);
        }

        return output;
    }

    private static float[] MatrixVector(float[] matrix, int rows, int columns, float[] input)
    {
        if (matrix.Length != checked(rows * columns) || input.Length != columns)
        {
            throw new InvalidDataException("Tiny reference matrix shape does not match its input.");
        }

        var output = new float[rows];
        for (var row = 0; row < rows; row++)
        {
            double sum = 0;
            for (var column = 0; column < columns; column++)
            {
                sum += (double)matrix[row * columns + column] * input[column];
            }

            output[row] = (float)sum;
        }

        return output;
    }

    private static float[] Lookup(float[] embeddings, int tokenId, int hiddenSize)
    {
        if (tokenId < 0 || tokenId >= embeddings.Length / hiddenSize)
        {
            throw new InvalidDataException($"Tiny reference token ID is out of range: {tokenId}");
        }

        return embeddings.AsSpan(tokenId * hiddenSize, hiddenSize).ToArray();
    }

    private static float[] ApplyRope(float[] values, int position)
    {
        if (values.Length != 2)
        {
            throw new InvalidDataException("Tiny reference RoPE dimension must be two.");
        }

        var cosine = Math.Cos(position);
        var sine = Math.Sin(position);
        return
        [
            (float)(values[0] * cosine - values[1] * sine),
            (float)(values[0] * sine + values[1] * cosine)
        ];
    }

    private static float[] Softmax(float[] values)
    {
        var maximum = values.Max();
        var exponentials = values.Select(value => Math.Exp(value - maximum)).ToArray();
        var denominator = exponentials.Sum();
        return exponentials.Select(value => (float)(value / denominator)).ToArray();
    }

    private static double Dot(float[] left, float[] right)
    {
        double sum = 0;
        for (var index = 0; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return sum;
    }

    private static float[] Add(float[] left, float[] right)
    {
        var result = new float[left.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = left[index] + right[index];
        }

        return result;
    }

    private static int ArgMax(IReadOnlyList<float> values)
    {
        var bestIndex = 0;
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] > values[bestIndex])
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static float[] Get(IReadOnlyDictionary<string, TinyTensor> weights, string name)
        => weights.TryGetValue(name, out var tensor)
            ? tensor.Values
            : throw new InvalidDataException($"Tiny reference tensor is missing: {name}");

    private static void AddCheckpoint(
        ICollection<TinyOracleCheckpoint> checkpoints,
        string name,
        int[] shape,
        float[] values)
        => checkpoints.Add(new TinyOracleCheckpoint(name, "F32", shape, values));

    private sealed record PositionResult(
        int Position,
        int InputTokenId,
        AttentionResult Attention,
        RouterResult Router,
        float[] MoeOutput,
        float[] LayerOutput,
        float[] Logits);

    private sealed record AttentionResult(
        float[] QueryLatent,
        float[] NormalizedQueryLatent,
        float[] KeyValueLatent,
        float[] NormalizedKeyValueLatent,
        float[] Query,
        float[] Key,
        float[] Value,
        float[] Scores,
        float[] Probabilities,
        float[] Output);

    private sealed record RouterResult(
        float[] Input,
        float[] Scores,
        float[] AdjustedScores,
        int[] ExpertIds,
        float[] Weights,
        float[] Output);
}
