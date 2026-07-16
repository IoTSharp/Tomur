namespace Tomur.Providers.Glm;

internal sealed class IsolatedKvContext : IDisposable
{
    private readonly GlmModelConfiguration configuration;
    private readonly KvContextBudget budget;
    private KvCache? cache;
    private SequenceState[]? sequences;
    private long reservedBytes;

    public IsolatedKvContext(GlmModelConfiguration configuration, int contextSize)
        : this(configuration, contextSize, new KvContextBudget(long.MaxValue))
    {
    }

    internal IsolatedKvContext(
        GlmModelConfiguration configuration,
        int contextSize,
        KvContextBudget budget)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(budget);
        if (contextSize <= 0 || contextSize > configuration.MaxPositionEmbeddings)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSize));
        }

        this.configuration = configuration;
        this.budget = budget;
        ContextSize = contextSize;
        reservedBytes = GetByteLength(configuration, contextSize);
        budget.Reserve(reservedBytes);
        try
        {
            cache = new KvCache(configuration, contextSize);
            sequences = CreateSequences(configuration, contextSize);
        }
        catch
        {
            cache?.Dispose();
            cache = null;
            sequences = null;
            budget.Release(Interlocked.Exchange(ref reservedBytes, 0));
            throw;
        }
    }

    private IsolatedKvContext(
        GlmModelConfiguration configuration,
        int contextSize,
        KvCache cache,
        SequenceState[] sequences,
        KvContextBudget budget,
        long reservedBytes)
    {
        this.configuration = configuration;
        this.budget = budget;
        ContextSize = contextSize;
        this.cache = cache;
        this.sequences = sequences;
        this.reservedBytes = reservedBytes;
    }

    public int ContextSize { get; }

    public int Position
    {
        get
        {
            var current = GetSequences();
            return current.Length == 0 ? 0 : current[0].Position;
        }
    }

    internal KvCache Cache => GetCache();

    internal SequenceState GetSequence(int layer)
    {
        var current = GetSequences();
        if ((uint)layer >= (uint)current.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }

        return current[layer];
    }

    public void Save(
        Stream destination,
        string modelIdentity,
        CancellationToken cancellationToken = default)
    {
        EnsureAligned();
        GetCache().Save(destination, modelIdentity, cancellationToken);
    }

    public KvCacheRestoreResult Restore(
        Stream source,
        string expectedModelIdentity,
        CancellationToken cancellationToken = default)
    {
        var result = GetCache().Restore(source, expectedModelIdentity, cancellationToken);
        if (result.MinimumTokenCount != result.MaximumTokenCount)
        {
            throw new InvalidDataException(
                "KV snapshot layers do not share one sequence position and cannot be resumed.");
        }

        foreach (var sequence in GetSequences())
        {
            sequence.RestorePersistent(
                result.MaximumTokenCount,
                result.MaximumTokenCount,
                cacheStart: 0);
        }

        return result;
    }

    public IsolatedKvContext Fork()
    {
        EnsureAligned();
        var bytes = GetCache().ByteLength;
        budget.Reserve(bytes);
        KvCache? cacheCopy = null;
        try
        {
            cacheCopy = GetCache().CreateIsolatedCopy(configuration);
            var sequenceCopies = CreateSequences(configuration, ContextSize);
            var current = GetSequences();
            for (var layer = 0; layer < current.Length; layer++)
            {
                var checkpoint = current[layer].Capture();
                sequenceCopies[layer].RestorePersistent(
                    checkpoint.Position,
                    checkpoint.ValidTokenCount,
                    checkpoint.CacheStart);
            }

            return new IsolatedKvContext(
                configuration,
                ContextSize,
                cacheCopy,
                sequenceCopies,
                budget,
                bytes);
        }
        catch
        {
            cacheCopy?.Dispose();
            budget.Release(bytes);
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref cache, null)?.Dispose();
        var current = Interlocked.Exchange(ref sequences, null);
        if (current is not null)
        {
            foreach (var sequence in current)
            {
                sequence.RestorePersistent(0, 0, 0);
            }
        }

        budget.Release(Interlocked.Exchange(ref reservedBytes, 0));
    }

    private void EnsureAligned()
    {
        var current = GetSequences();
        for (var layer = 0; layer < current.Length; layer++)
        {
            if (GetCache().GetValidTokenCount(layer) != current[layer].ValidTokenCount)
            {
                throw new InvalidOperationException(
                    $"Layer {layer} sequence and compressed KV token counts are not aligned.");
            }
        }
    }

    private KvCache GetCache()
    {
        ObjectDisposedException.ThrowIf(cache is null, this);
        return cache;
    }

    private SequenceState[] GetSequences()
    {
        ObjectDisposedException.ThrowIf(sequences is null, this);
        return sequences;
    }

    private static SequenceState[] CreateSequences(
        GlmModelConfiguration configuration,
        int contextSize)
        => Enumerable.Range(0, configuration.LayerCount)
            .Select(layer => new SequenceState(layer, configuration.LayerCount, contextSize))
            .ToArray();

    private static long GetByteLength(
        GlmModelConfiguration configuration,
        int contextSize)
        => checked(
            checked(
                checked((long)configuration.LayerCount * contextSize) *
                checked(configuration.KeyValueLoraRank + configuration.QueryKeyRopeHeadSize)) *
            sizeof(float));
}

internal sealed class KvContextBudget
{
    private readonly long maximumBytes;
    private long usedBytes;

    public KvContextBudget(long maximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        this.maximumBytes = maximumBytes;
    }

    public long UsedBytes => Interlocked.Read(ref usedBytes);

    public void Reserve(long bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        while (true)
        {
            var current = Interlocked.Read(ref usedBytes);
            long next;
            try
            {
                next = checked(current + bytes);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("Isolated KV context budget overflowed.", exception);
            }

            if (next > maximumBytes)
            {
                throw new InvalidOperationException(
                    $"Isolated KV contexts require {next} bytes, but their shared budget is {maximumBytes} bytes.");
            }

            if (Interlocked.CompareExchange(ref usedBytes, next, current) == current)
            {
                return;
            }
        }
    }

    public void Release(long bytes)
    {
        if (bytes == 0)
        {
            return;
        }

        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        while (true)
        {
            var current = Interlocked.Read(ref usedBytes);
            if (bytes > current)
            {
                throw new InvalidOperationException("Isolated KV context budget was released more than once.");
            }

            if (Interlocked.CompareExchange(ref usedBytes, current - bytes, current) == current)
            {
                return;
            }
        }
    }
}
