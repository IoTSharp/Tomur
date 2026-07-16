namespace Tomur.Providers.Glm;

internal sealed class RouterLookaheadPrefetcher
{
    private readonly ExpertCache cache;
    private readonly int[][] predictedExperts;
    private readonly bool[] hasPrediction;
    private long requestedExperts;

    public RouterLookaheadPrefetcher(
        GlmModelConfiguration configuration,
        ExpertCache cache)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cache);
        this.cache = cache;
        predictedExperts = new int[configuration.LayerCount][];
        hasPrediction = new bool[configuration.LayerCount];
        for (var layer = configuration.FirstMoeLayer; layer < configuration.LayerCount; layer++)
        {
            predictedExperts[layer] = new int[configuration.ExpertsPerToken];
        }
    }

    public long RequestedExperts => Interlocked.Read(ref requestedExperts);

    public void Observe(int layer, ReadOnlySpan<int> selectedExperts)
    {
        var destination = GetLayer(layer);
        if (selectedExperts.Length != destination.Length)
        {
            throw new ArgumentException(
                $"Router lookahead requires exactly {destination.Length} selected experts.",
                nameof(selectedExperts));
        }

        selectedExperts.CopyTo(destination);
        Volatile.Write(ref hasPrediction[layer], true);
    }

    public async ValueTask PrefetchAsync(
        int layer,
        CancellationToken cancellationToken = default)
    {
        var experts = GetLayer(layer);
        if (!Volatile.Read(ref hasPrediction[layer]) || experts.Length > cache.SlotCapacityPerLayer)
        {
            return;
        }

        await cache.PrefetchLayerAsync(layer, experts, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref requestedExperts, experts.Length);
    }

    private int[] GetLayer(int layer)
    {
        if ((uint)layer >= (uint)predictedExperts.Length || predictedExperts[layer] is not { } experts)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} is not a MoE layer.");
        }

        return experts;
    }
}
