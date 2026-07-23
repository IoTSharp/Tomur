namespace Tomur.Providers.Glm;

internal sealed record AdvancedFeatureProbe(
    bool DsaConfigured,
    int DsaTensorCount,
    bool DsaDenseEquivalentAtContextLimit,
    bool MtpConfigured,
    int MtpTensorCount,
    string? MtpHeadTensorName)
{
    /// <summary>
    /// 检查 DSA 与 MTP 配置对应的张量集合，并拒绝不符合 full/shared 声明的 indexer。
    /// </summary>
    public static AdvancedFeatureProbe Inspect(
        GlmModelConfiguration configuration,
        SafeTensorCatalog tensors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tensors);

        var dsaTensors = tensors.Items
            .Where(static tensor => IsDsaTensor(tensor.Name))
            .ToArray();
        var mtpTensors = tensors.Items
            .Where(tensor => IsMtpTensor(tensor.Name, configuration.LayerCount))
            .ToArray();

        if (configuration.HasDsaConfiguration)
        {
            var indexedLayers = dsaTensors
                .Select(static tensor => TryReadLayer(tensor.Name))
                .Where(static layer => layer.HasValue)
                .Select(static layer => layer!.Value)
                .Where(layer => layer >= configuration.DsaStartLayer && layer < configuration.LayerCount)
                .ToHashSet();
            var unexpectedLayers = indexedLayers
                .Where(layer => configuration.DsaIndexerTypes is not null &&
                    !configuration.DsaIndexerTypes[layer].Equals("full", StringComparison.OrdinalIgnoreCase))
                .Order()
                .ToArray();
            if (unexpectedLayers.Length > 0)
            {
                throw new InvalidDataException(
                    $"DSA indexer tensors were found on shared layer(s) " +
                    $"{string.Join(", ", unexpectedLayers)}.");
            }

            foreach (var tensor in dsaTensors)
            {
                if (tensor.LogicalShape.Count is < 1 or > 3)
                {
                    throw new InvalidDataException(
                        $"DSA indexer tensor '{tensor.Name}' must have rank 1, 2 or 3.");
                }
            }
        }

        if (configuration.HasMtpConfiguration && mtpTensors.Length == 0)
        {
            throw new InvalidDataException(
                "MTP is configured, but no MTP or next-token prediction tensors were found.");
        }

        var mtpHead = mtpTensors.FirstOrDefault(tensor =>
            IsMtpHead(tensor, configuration));

        return new AdvancedFeatureProbe(
            configuration.HasDsaConfiguration,
            dsaTensors.Length,
            configuration.HasDsaConfiguration &&
                configuration.DsaTopK >= configuration.MaxPositionEmbeddings,
            configuration.HasMtpConfiguration,
            mtpTensors.Length,
            configuration.HasMtpConfiguration ? mtpHead?.Name : null);
    }

    private static bool IsDsaTensor(string name)
        => name.Contains(".self_attn.indexer.", StringComparison.Ordinal) ||
            name.Contains(".self_attn.indexer_", StringComparison.Ordinal);

    private static bool IsMtpTensor(string name, int baseLayerCount)
        => name.Contains(".mtp.", StringComparison.Ordinal) ||
            name.Contains(".mtp_", StringComparison.Ordinal) ||
            name.Contains("nextn", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith($"model.layers.{baseLayerCount}.", StringComparison.Ordinal);

    private static bool IsMtpHead(
        TensorDescriptor tensor,
        GlmModelConfiguration configuration)
        => tensor.LogicalShape.Count == 2 &&
            tensor.LogicalShape[0] == configuration.VocabularySize &&
            tensor.LogicalShape[1] == configuration.HiddenSize &&
            (tensor.Name.EndsWith(".head.weight", StringComparison.Ordinal) ||
             tensor.Name.EndsWith("mtp_head.weight", StringComparison.Ordinal));

    private static int? TryReadLayer(string name)
    {
        const string marker = "model.layers.";
        var start = name.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = name.IndexOf('.', start);
        return end > start && int.TryParse(name.AsSpan(start, end - start), out var layer)
            ? layer
            : null;
    }
}
