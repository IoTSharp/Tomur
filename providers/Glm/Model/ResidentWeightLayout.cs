namespace Tomur.Providers.Glm;

internal sealed record ResidentWeightSpec(TensorDescriptor Descriptor);

internal static class ResidentWeightLayout
{
    public static IReadOnlyList<ResidentWeightSpec> Create(
        GlmModelConfiguration configuration,
        SafeTensorCatalog tensors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tensors);

        var specs = new List<ResidentWeightSpec>();
        Add(specs, tensors, "model.embed_tokens.weight", configuration.VocabularySize, configuration.HiddenSize);
        Add(specs, tensors, "model.norm.weight", configuration.HiddenSize);
        Add(specs, tensors, "lm_head.weight", configuration.VocabularySize, configuration.HiddenSize);

        var queryProjectionSize = checked(
            configuration.AttentionHeadCount *
            checked(configuration.QueryKeyNopeHeadSize + configuration.QueryKeyRopeHeadSize));
        var keyValueInputSize = checked(
            configuration.KeyValueLoraRank + configuration.QueryKeyRopeHeadSize);
        var keyValueProjectionSize = checked(
            configuration.AttentionHeadCount *
            checked(configuration.QueryKeyNopeHeadSize + configuration.ValueHeadSize));

        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            var prefix = $"model.layers.{layer}.";
            Add(specs, tensors, $"{prefix}input_layernorm.weight", configuration.HiddenSize);
            Add(specs, tensors, $"{prefix}post_attention_layernorm.weight", configuration.HiddenSize);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.q_a_proj.weight",
                configuration.QueryLoraRank,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.q_a_layernorm.weight",
                configuration.QueryLoraRank);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.q_b_proj.weight",
                queryProjectionSize,
                configuration.QueryLoraRank);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.kv_a_proj_with_mqa.weight",
                keyValueInputSize,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.kv_a_layernorm.weight",
                configuration.KeyValueLoraRank);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.kv_b_proj.weight",
                keyValueProjectionSize,
                configuration.KeyValueLoraRank);
            Add(
                specs,
                tensors,
                $"{prefix}self_attn.o_proj.weight",
                configuration.HiddenSize,
                checked(configuration.AttentionHeadCount * configuration.ValueHeadSize));

            if (layer < configuration.FirstMoeLayer)
            {
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.gate_proj.weight",
                    configuration.DenseIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.up_proj.weight",
                    configuration.DenseIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.down_proj.weight",
                    configuration.HiddenSize,
                    configuration.DenseIntermediateSize);
                continue;
            }

            Add(
                specs,
                tensors,
                $"{prefix}mlp.gate.weight",
                configuration.RoutedExpertCount,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                $"{prefix}mlp.gate.e_score_correction_bias",
                configuration.RoutedExpertCount);
            if (configuration.SharedExpertCount > 0)
            {
                var sharedIntermediateSize = checked(
                    configuration.SharedExpertCount * configuration.MoeIntermediateSize);
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.shared_experts.gate_proj.weight",
                    sharedIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.shared_experts.up_proj.weight",
                    sharedIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    $"{prefix}mlp.shared_experts.down_proj.weight",
                    configuration.HiddenSize,
                    sharedIntermediateSize);
            }
        }

        return specs;
    }

    private static void Add(
        ICollection<ResidentWeightSpec> specs,
        SafeTensorCatalog tensors,
        string name,
        params long[] expectedShape)
    {
        var descriptor = tensors.GetRequired(name);
        if (!descriptor.LogicalShape.SequenceEqual(expectedShape))
        {
            throw new InvalidDataException(
                $"Resident tensor '{name}' has shape [{string.Join(", ", descriptor.LogicalShape)}]; " +
                $"expected [{string.Join(", ", expectedShape)}].");
        }

        if (descriptor.DataType is not (
                TensorDataType.Float32 or TensorDataType.Float16 or TensorDataType.BFloat16))
        {
            throw new InvalidDataException(
                $"Resident tensor '{name}' uses {descriptor.DataTypeName}. " +
                "M6 resident loading requires F32, F16 or BF16 storage; quantized resident weights require an explicit payload and scale layout.");
        }

        if (descriptor.ElementCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Resident tensor '{name}' contains {descriptor.ElementCount} elements and exceeds the single-buffer limit.");
        }

        specs.Add(new ResidentWeightSpec(descriptor));
    }
}
