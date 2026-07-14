using System.Text.Json;

namespace Tomur.Providers.Glm;

internal sealed record GlmModelConfiguration(
    string ModelType,
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
    int MaxPositionEmbeddings,
    int ExpertGroupCount,
    int ExpertGroupsPerToken,
    bool NormalizeTopKProbabilities,
    float RmsNormEpsilon,
    float RoutedScalingFactor,
    float RopeTheta)
{
    public const string DsaModelType = "glm_moe_dsa";
    public const string MoeLiteModelType = "glm4_moe_lite";
    private const int MaximumConfigBytes = 4 * 1024 * 1024;

    public static bool IsSupportedModelType(string modelType)
        => modelType.Equals(DsaModelType, StringComparison.OrdinalIgnoreCase) ||
            modelType.Equals(MoeLiteModelType, StringComparison.OrdinalIgnoreCase);

    public static GlmModelConfiguration Read(string path, string expectedModelType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelType);
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

        var modelType = GetRequiredString(root, "model_type");
        if (!IsSupportedModelType(modelType))
        {
            throw new InvalidDataException($"Managed GLM model type '{modelType}' is not supported.");
        }

        if (!modelType.Equals(expectedModelType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model configuration type '{modelType}' does not match manifest architecture '{expectedModelType}'.");
        }

        var configuration = new GlmModelConfiguration(
            modelType,
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
            GetRequiredInt(root, "max_position_embeddings"),
            GetRequiredInt(root, "n_group"),
            GetRequiredInt(root, "topk_group"),
            GetOptionalBool(root, "norm_topk_prob", true),
            GetOptionalFloat(root, "rms_norm_eps", 1e-5f),
            GetOptionalFloat(root, "routed_scaling_factor", 1.0f),
            GetRopeTheta(root));

        configuration.Validate(root);
        return configuration;
    }

    private void Validate(JsonElement root)
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
        RequireRange(MaxPositionEmbeddings, 1, 1 << 24, "max_position_embeddings");
        RequireRange(ExpertGroupCount, 1, RoutedExpertCount, "n_group");
        RequireRange(ExpertGroupsPerToken, 1, ExpertGroupCount, "topk_group");

        if (ExpertGroupCount != 1)
        {
            throw new InvalidDataException("The current managed GLM provider requires n_group=1.");
        }

        if ((QueryKeyRopeHeadSize & 1) != 0)
        {
            throw new InvalidDataException(
                "Model configuration property 'qk_rope_head_dim' must be even for interleaved partial RoPE.");
        }

        if (!float.IsFinite(RmsNormEpsilon) || RmsNormEpsilon <= 0 ||
            !float.IsFinite(RoutedScalingFactor) || RoutedScalingFactor <= 0 ||
            !float.IsFinite(RopeTheta) || RopeTheta <= 0)
        {
            throw new InvalidDataException("Model floating-point configuration values must be finite and positive.");
        }

        if (GetOptionalBool(root, "attention_bias", false))
        {
            throw new InvalidDataException("The current managed GLM provider requires attention_bias=false.");
        }

        if (!GetOptionalBool(root, "rope_interleave", true))
        {
            throw new InvalidDataException("The current managed GLM provider requires rope_interleave=true.");
        }

        RequireOptionalString(root, "hidden_act", "silu");
        RequireOptionalString(root, "scoring_func", "sigmoid");
        RequireOptionalString(root, "topk_method", "noaux_tc");

        var queryKeyHeadSize = GetOptionalInt(root, "qk_head_dim");
        if (queryKeyHeadSize.HasValue &&
            queryKeyHeadSize.Value != checked(QueryKeyNopeHeadSize + QueryKeyRopeHeadSize))
        {
            throw new InvalidDataException(
                "Model configuration property 'qk_head_dim' must equal qk_nope_head_dim + qk_rope_head_dim.");
        }

        var keyValueHeadCount = GetOptionalInt(root, "num_key_value_heads");
        if (keyValueHeadCount.HasValue && keyValueHeadCount.Value != AttentionHeadCount)
        {
            throw new InvalidDataException(
                "The current managed GLM MLA path requires num_key_value_heads to equal num_attention_heads.");
        }

        ValidateLayerTypes(root);
        if (root.TryGetProperty("rope_parameters", out var rope) &&
            !GetOptionalString(rope, "rope_type", "default").Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The current managed GLM provider requires default RoPE parameters.");
        }
    }

    private void ValidateLayerTypes(JsonElement root)
    {
        if (!root.TryGetProperty("mlp_layer_types", out var layerTypes))
        {
            return;
        }

        if (layerTypes.ValueKind != JsonValueKind.Array || layerTypes.GetArrayLength() != LayerCount)
        {
            throw new InvalidDataException(
                $"Model configuration property 'mlp_layer_types' must contain {LayerCount} entries.");
        }

        var layer = 0;
        foreach (var layerType in layerTypes.EnumerateArray())
        {
            var expected = layer < FirstMoeLayer ? "dense" : "sparse";
            if (layerType.ValueKind != JsonValueKind.String ||
                !string.Equals(layerType.GetString(), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Model layer {layer} must be declared '{expected}' in mlp_layer_types.");
            }

            layer++;
        }
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be a non-empty string.");
        }

        return property.GetString()!;
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

    private static int? GetOptionalInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be an integer.");
        }

        return value;
    }

    private static string GetOptionalString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Model configuration property '{name}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static void RequireOptionalString(JsonElement root, string name, string expected)
    {
        var value = GetOptionalString(root, name, expected);
        if (!value.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The current managed GLM provider requires {name}='{expected}'.");
        }
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

    private static float GetRopeTheta(JsonElement root)
    {
        if (!root.TryGetProperty("rope_parameters", out var rope))
        {
            return 10000.0f;
        }

        if (rope.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Model configuration property 'rope_parameters' must be an object.");
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
