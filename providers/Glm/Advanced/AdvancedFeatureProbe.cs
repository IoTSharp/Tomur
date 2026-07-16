namespace Tomur.Providers.Glm;

internal sealed record AdvancedFeatureProbe(
    bool DsaConfigured,
    int DsaTensorCount,
    bool DsaDenseEquivalentAtContextLimit,
    bool MtpConfigured,
    int MtpTensorCount,
    string? MtpHeadTensorName)
{
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
            var expectedLayerCount = configuration.LayerCount - configuration.DsaStartLayer;
            var indexedLayers = dsaTensors
                .Select(static tensor => TryReadLayer(tensor.Name))
                .Where(static layer => layer.HasValue)
                .Select(static layer => layer!.Value)
                .Where(layer => layer >= configuration.DsaStartLayer && layer < configuration.LayerCount)
                .Distinct()
                .Count();
            if (indexedLayers < expectedLayerCount)
            {
                throw new InvalidDataException(
                    $"DSA is configured for {expectedLayerCount} layer(s), but indexer tensors were found for only {indexedLayers} layer(s).");
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
        if (configuration.HasMtpConfiguration && mtpHead is null)
        {
            throw new InvalidDataException(
                "MTP is configured, but no vocabulary projection head with the expected shape was found.");
        }

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
