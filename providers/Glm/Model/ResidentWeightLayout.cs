namespace Tomur.Providers.Glm;

internal sealed record ResidentWeightSpec(
    TensorDescriptor Descriptor,
    QuantizedTensorDescriptor? Quantized = null)
{
    public long ResidentBytes => Quantized is null
        ? checked(Descriptor.ElementCount * sizeof(float))
        : checked(Quantized.Payload.PhysicalLength + Quantized.Scales.PhysicalLength);
}

internal static class ResidentWeightLayout
{
    public static IReadOnlyList<ResidentWeightSpec> Create(
        GlmModelConfiguration configuration,
        SafeTensorCatalog tensors,
        string quantization = "f32",
        string quantizationLayout = "separate-scales",
        AdvancedFeatureProbe? advancedFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tensors);

        var specs = new List<ResidentWeightSpec>();
        Add(specs, tensors, quantization, quantizationLayout, "model.embed_tokens.weight", configuration.VocabularySize, configuration.HiddenSize);
        Add(specs, tensors, quantization, quantizationLayout, "model.norm.weight", configuration.HiddenSize);
        Add(specs, tensors, quantization, quantizationLayout, "lm_head.weight", configuration.VocabularySize, configuration.HiddenSize);
        advancedFeatures ??= AdvancedFeatureProbe.Inspect(configuration, tensors);
        if (advancedFeatures.MtpHeadTensorName is { } mtpHead)
        {
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                mtpHead,
                configuration.VocabularySize,
                configuration.HiddenSize);
        }

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
            Add(specs, tensors, quantization, quantizationLayout, $"{prefix}input_layernorm.weight", configuration.HiddenSize);
            Add(specs, tensors, quantization, quantizationLayout, $"{prefix}post_attention_layernorm.weight", configuration.HiddenSize);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.q_a_proj.weight",
                configuration.QueryLoraRank,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.q_a_layernorm.weight",
                configuration.QueryLoraRank);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.q_b_proj.weight",
                queryProjectionSize,
                configuration.QueryLoraRank);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.kv_a_proj_with_mqa.weight",
                keyValueInputSize,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.kv_a_layernorm.weight",
                configuration.KeyValueLoraRank);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.kv_b_proj.weight",
                keyValueProjectionSize,
                configuration.KeyValueLoraRank);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}self_attn.o_proj.weight",
                configuration.HiddenSize,
                checked(configuration.AttentionHeadCount * configuration.ValueHeadSize));

            if (layer < configuration.FirstMoeLayer)
            {
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
                    $"{prefix}mlp.gate_proj.weight",
                    configuration.DenseIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
                    $"{prefix}mlp.up_proj.weight",
                    configuration.DenseIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
                    $"{prefix}mlp.down_proj.weight",
                    configuration.HiddenSize,
                    configuration.DenseIntermediateSize);
                continue;
            }

            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}mlp.gate.weight",
                configuration.RoutedExpertCount,
                configuration.HiddenSize);
            Add(
                specs,
                tensors,
                quantization,
                quantizationLayout,
                $"{prefix}mlp.gate.e_score_correction_bias",
                configuration.RoutedExpertCount);
            if (configuration.SharedExpertCount > 0)
            {
                var sharedIntermediateSize = checked(
                    configuration.SharedExpertCount * configuration.MoeIntermediateSize);
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
                    $"{prefix}mlp.shared_experts.gate_proj.weight",
                    sharedIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
                    $"{prefix}mlp.shared_experts.up_proj.weight",
                    sharedIntermediateSize,
                    configuration.HiddenSize);
                Add(
                    specs,
                    tensors,
                    quantization,
                    quantizationLayout,
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
        string quantization,
        string quantizationLayout,
        string name,
        params long[] expectedShape)
    {
        var descriptor = tensors.GetRequired(name);
        if (descriptor.DataType is TensorDataType.Float32 or TensorDataType.Float16 or TensorDataType.BFloat16)
        {
            if (!descriptor.LogicalShape.SequenceEqual(expectedShape))
            {
                throw new InvalidDataException(
                    $"Resident tensor '{name}' has shape [{string.Join(", ", descriptor.LogicalShape)}]; " +
                    $"expected [{string.Join(", ", expectedShape)}].");
            }

            if (descriptor.ElementCount > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Resident tensor '{name}' contains {descriptor.ElementCount} elements and exceeds the single-buffer limit.");
            }

            specs.Add(new ResidentWeightSpec(descriptor));
            return;
        }

        if (!quantizationLayout.Equals("packed-offset", StringComparison.OrdinalIgnoreCase) ||
            descriptor.DataType is not (TensorDataType.Int8 or TensorDataType.UInt8) ||
            expectedShape.Length != 2)
        {
            throw new InvalidDataException(
                $"Resident tensor '{name}' uses {descriptor.DataTypeName}; the declared quantization layout cannot decode it.");
        }

        if (expectedShape[0] > int.MaxValue || expectedShape[1] > int.MaxValue)
        {
            throw new InvalidDataException($"Resident tensor '{name}' shape exceeds managed indexing limits.");
        }

        var rows = (int)expectedShape[0];
        var columns = (int)expectedShape[1];
        var format = ResolvePackedFormat(descriptor, rows, columns, quantization);
        var shape = new QuantizedTensorShape(
            format,
            rows,
            columns,
            QuantizedValueEncoding.OffsetBinary);
        var scaleName = ExpertDescriptorLayout.GetScaleTensorName(name, quantizationLayout);
        var scales = tensors.GetRequired(scaleName);
        if (!scales.LogicalShape.SequenceEqual([expectedShape[0]]))
        {
            throw new InvalidDataException(
                $"Quantized resident scale tensor '{scaleName}' has shape " +
                $"[{string.Join(", ", scales.LogicalShape)}]; expected [{expectedShape[0]}].");
        }

        specs.Add(new ResidentWeightSpec(
            descriptor,
            new QuantizedTensorDescriptor(descriptor, scales, shape)));
    }

    internal static QuantizedTensorFormat ResolvePackedFormat(
        TensorDescriptor descriptor,
        int rows,
        int columns,
        string declaredQuantization)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (rows <= 0 || columns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                rows <= 0 ? nameof(rows) : nameof(columns));
        }

        var int8Bytes = checked((long)rows * columns);
        if (descriptor.PhysicalLength == int8Bytes)
        {
            return QuantizedTensorFormat.Int8;
        }

        var int4Bytes = checked((long)rows * (((long)columns + 1) / 2));
        if (descriptor.PhysicalLength == int4Bytes)
        {
            return QuantizedTensorFormat.Int4;
        }

        throw new InvalidDataException(
            $"Packed resident tensor '{descriptor.Name}' has {descriptor.PhysicalLength} payload bytes; " +
            $"expected {int8Bytes} for int8 or {int4Bytes} for int4 " +
            $"(declared model quantization '{declaredQuantization}').");
    }
}
