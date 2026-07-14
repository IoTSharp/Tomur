using System.Text.Json;

namespace Tomur.Providers.Glm;

internal sealed record GlmModelConfiguration(
    int HiddenSize,
    int LayerCount,
    int AttentionHeadCount,
    int RoutedExpertCount,
    int ExpertsPerToken,
    int MoeIntermediateSize,
    int DenseIntermediateSize,
    int FirstMoeLayer,
    int QueryLoraRank,
    int KeyValueLoraRank,
    int QueryKeyNopeHeadSize,
    int QueryKeyRopeHeadSize,
    int ValueHeadSize,
    int SharedExpertCount,
    int VocabularySize,
    int ExpertGroupCount,
    int ExpertGroupsPerToken,
    float RmsNormEpsilon,
    float RoutedScalingFactor,
    float RopeTheta)
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;

    public static GlmModelConfiguration Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidDataException($"Model configuration was not found: {path}");
        }

        if (info.Length <= 0 || info.Length > MaximumConfigBytes)
        {
            throw new InvalidDataException($"Model configuration must be between 1 and {MaximumConfigBytes} bytes.");
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

        var configuration = new GlmModelConfiguration(
            GetRequiredInt(root, "hidden_size"),
            GetRequiredInt(root, "num_hidden_layers"),
            GetRequiredInt(root, "num_attention_heads"),
            GetRequiredInt(root, "n_routed_experts"),
            GetRequiredInt(root, "num_experts_per_tok"),
            GetRequiredInt(root, "moe_intermediate_size"),
            GetRequiredInt(root, "intermediate_size"),
            GetRequiredInt(root, "first_k_dense_replace"),
            GetRequiredInt(root, "q_lora_rank"),
            GetRequiredInt(root, "kv_lora_rank"),
            GetRequiredInt(root, "qk_nope_head_dim"),
            GetRequiredInt(root, "qk_rope_head_dim"),
            GetRequiredInt(root, "v_head_dim"),
            GetRequiredInt(root, "n_shared_experts"),
            GetRequiredInt(root, "vocab_size"),
            GetRequiredInt(root, "n_group"),
            GetRequiredInt(root, "topk_group"),
            GetOptionalFloat(root, "rms_norm_eps", 1e-5f),
            GetOptionalFloat(root, "routed_scaling_factor", 1.0f),
            GetRopeTheta(root));

        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        RequireRange(HiddenSize, 1, 1 << 20, "hidden_size");
        RequireRange(LayerCount, 1, 128, "num_hidden_layers");
        RequireRange(AttentionHeadCount, 1, 1024, "num_attention_heads");
        RequireRange(RoutedExpertCount, 1, 4096, "n_routed_experts");
        RequireRange(ExpertsPerToken, 1, Math.Min(64, RoutedExpertCount), "num_experts_per_tok");
        RequireRange(MoeIntermediateSize, 1, 1 << 20, "moe_intermediate_size");
        RequireRange(DenseIntermediateSize, 1, 1 << 24, "intermediate_size");
        RequireRange(FirstMoeLayer, 0, LayerCount, "first_k_dense_replace");
        RequireRange(QueryLoraRank, 1, 1 << 20, "q_lora_rank");
        RequireRange(KeyValueLoraRank, 1, 1 << 20, "kv_lora_rank");
        RequireRange(QueryKeyNopeHeadSize, 1, 1 << 16, "qk_nope_head_dim");
        RequireRange(QueryKeyRopeHeadSize, 1, 1 << 16, "qk_rope_head_dim");
        RequireRange(ValueHeadSize, 1, 1 << 16, "v_head_dim");
        RequireRange(SharedExpertCount, 0, 64, "n_shared_experts");
        RequireRange(VocabularySize, 1, 1 << 24, "vocab_size");
        RequireRange(ExpertGroupCount, 1, RoutedExpertCount, "n_group");
        RequireRange(ExpertGroupsPerToken, 1, ExpertGroupCount, "topk_group");

        if (ExpertGroupCount != 1)
        {
            throw new InvalidDataException("The current managed GLM provider requires n_group=1.");
        }

        if (!float.IsFinite(RmsNormEpsilon) || RmsNormEpsilon <= 0 ||
            !float.IsFinite(RoutedScalingFactor) || RoutedScalingFactor <= 0 ||
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

    private static float GetRopeTheta(JsonElement root)
    {
        if (!root.TryGetProperty("rope_parameters", out var rope) || rope.ValueKind != JsonValueKind.Object)
        {
            return 10000.0f;
        }

        return GetOptionalFloat(rope, "rope_theta", 10000.0f);
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
