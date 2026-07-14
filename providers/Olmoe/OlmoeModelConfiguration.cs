using System.Text.Json;
using Tomur.Providers.Glm;

namespace Tomur.Providers.Olmoe;

internal sealed record OlmoeModelConfiguration(
    int HiddenSize,
    int LayerCount,
    int AttentionHeadCount,
    int KeyValueHeadCount,
    int RoutedExpertCount,
    int ExpertsPerToken,
    int IntermediateSize,
    int VocabularySize,
    int MaxPositionEmbeddings,
    int EosTokenId,
    bool NormalizeTopKProbabilities,
    float RmsNormEpsilon,
    float RopeTheta)
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;

    public int HeadSize => HiddenSize / AttentionHeadCount;

    public int KeyValueSize => checked(KeyValueHeadCount * HeadSize);

    public int QueryHeadsPerKeyValueHead => AttentionHeadCount / KeyValueHeadCount;

    public ExpertLayoutConfiguration ExpertConfiguration => new(
        HiddenSize,
        LayerCount,
        FirstMoeLayer: 0,
        RoutedExpertCount,
        ExpertsPerToken,
        IntermediateSize);

    public static OlmoeModelConfiguration Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumConfigBytes)
        {
            throw new InvalidDataException(
                $"Model configuration must exist and be between 1 and {MaximumConfigBytes} bytes: {path}");
        }

        using var stream = File.OpenRead(info.FullName);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Model configuration root must be a JSON object.");
        }

        var modelType = GetRequiredString(root, "model_type");
        if (!modelType.Equals("olmoe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Managed OLMoE configuration requires model_type 'olmoe', found '{modelType}'.");
        }

        var configuration = new OlmoeModelConfiguration(
            GetRequiredInt(root, "hidden_size"),
            GetRequiredInt(root, "num_hidden_layers"),
            GetRequiredInt(root, "num_attention_heads"),
            GetRequiredInt(root, "num_key_value_heads"),
            GetRequiredInt(root, "num_experts"),
            GetRequiredInt(root, "num_experts_per_tok"),
            GetRequiredInt(root, "intermediate_size"),
            GetRequiredInt(root, "vocab_size"),
            GetRequiredInt(root, "max_position_embeddings"),
            GetRequiredInt(root, "eos_token_id"),
            GetOptionalBool(root, "norm_topk_prob", false),
            GetOptionalFloat(root, "rms_norm_eps", 1e-5f),
            GetOptionalFloat(root, "rope_theta", 10000.0f));
        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        RequireRange(HiddenSize, 2, 1 << 20, "hidden_size");
        RequireRange(LayerCount, 1, 128, "num_hidden_layers");
        RequireRange(AttentionHeadCount, 1, 1024, "num_attention_heads");
        RequireRange(KeyValueHeadCount, 1, AttentionHeadCount, "num_key_value_heads");
        RequireRange(RoutedExpertCount, 1, 4096, "num_experts");
        RequireRange(ExpertsPerToken, 1, Math.Min(64, RoutedExpertCount), "num_experts_per_tok");
        RequireRange(IntermediateSize, 1, 1 << 20, "intermediate_size");
        RequireRange(VocabularySize, 1, 1 << 24, "vocab_size");
        RequireRange(MaxPositionEmbeddings, 1, 1 << 24, "max_position_embeddings");
        RequireRange(EosTokenId, 0, VocabularySize - 1, "eos_token_id");
        if (HiddenSize % AttentionHeadCount != 0)
        {
            throw new InvalidDataException("hidden_size must be divisible by num_attention_heads.");
        }

        if (AttentionHeadCount % KeyValueHeadCount != 0)
        {
            throw new InvalidDataException("num_attention_heads must be divisible by num_key_value_heads.");
        }

        if ((HeadSize & 1) != 0)
        {
            throw new InvalidDataException("OLMoE attention head size must be even for split-half RoPE.");
        }

        if (!float.IsFinite(RmsNormEpsilon) || RmsNormEpsilon <= 0 ||
            !float.IsFinite(RopeTheta) || RopeTheta <= 0)
        {
            throw new InvalidDataException("Model floating-point configuration values must be finite and positive.");
        }
    }

    private static int GetRequiredInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be an integer.");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be a string.");
        }

        return property.GetString()!;
    }

    private static float GetOptionalFloat(JsonElement root, string name, float fallback)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out var value))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be a number.");
        }

        return value;
    }

    private static bool GetOptionalBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be a boolean.");
        }

        return property.GetBoolean();
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Model configuration property '{name}' is {value}, outside [{minimum}, {maximum}].");
        }
    }
}
