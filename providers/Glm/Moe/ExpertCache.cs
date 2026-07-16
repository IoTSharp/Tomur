using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace Tomur.Providers.Glm;

internal sealed record ExpertCacheOptions(
    long BudgetBytes,
    int WorkerCount = 2,
    int QueueCapacity = 8,
    long SafetyMarginBytes = 0,
    int HotExpertCount = 0,
    int RepinInterval = 1)
{
    public static ExpertCacheOptions CreateAutomatic(
        ExpertDescriptorLayout layout,
        ModelMemoryPlan memoryPlan,
        int WorkerCount = 2,
        int QueueCapacity = 8,
        long SafetyMarginBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(memoryPlan);
        if (layout.MoeLayerCount <= 0)
        {
            throw new InvalidOperationException("The model has no MoE layers and does not require an expert cache.");
        }

        var available = Math.Max(
            0,
            Math.Max(0, memoryPlan.AvailableBytes - memoryPlan.RequiredBytes) - SafetyMarginBytes);
        var maximumCapacity = checked((int)Math.Min(
            layout.Configuration.RoutedExpertCount,
            available / checked(layout.SlotBudgetedBytes * layout.MoeLayerCount)));
        var targetCapacity = Math.Min(
            maximumCapacity,
            checked(layout.Configuration.ExpertsPerToken + Math.Max(1, layout.Configuration.ExpertsPerToken / 2)));
        var capacity = Math.Max(layout.Configuration.ExpertsPerToken, targetCapacity);
        var budget = checked(layout.SlotBudgetedBytes * layout.MoeLayerCount * capacity);
        return new ExpertCacheOptions(
            budget,
            WorkerCount,
            QueueCapacity,
            SafetyMarginBytes,
            HotExpertCount: Math.Max(0, capacity - layout.Configuration.ExpertsPerToken),
            RepinInterval: 128);
    }

    public void Validate()
    {
        if (BudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BudgetBytes));
        }

        if (WorkerCount <= 0 || WorkerCount > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerCount));
        }

        if (QueueCapacity <= 0 || QueueCapacity > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }

        if (SafetyMarginBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyMarginBytes));
        }

        if (HotExpertCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HotExpertCount));
        }

        if (RepinInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RepinInterval));
        }
    }
}

internal sealed record ExpertCacheSnapshot(
    long BudgetedBytes,
    int SlotCapacityPerLayer,
    int TotalSlotCount,
    long Hits,
    long Misses,
    long Evictions,
    long DiskReads,
    long DiskBytes,
    TimeSpan DiskReadTime,
    TimeSpan ForegroundWaitTime,
    int HotExpertCount,
    long Prefetches,
    long LiveRepins,
    IReadOnlyDictionary<ExpertKey, long> UsageHistogram);

internal sealed class ExpertCacheBudgetExceededException : InvalidOperationException
{
    public ExpertCacheBudgetExceededException(
        long requestedBytes,
        long minimumBytes,
        long availableBytes,
        long safetyMarginBytes)
        : base(
            $"Expert cache requested {requestedBytes} bytes, requires at least {minimumBytes} bytes " +
            $"for one top-k working set per MoE layer, and has {availableBytes} bytes available " +
            $"after the {safetyMarginBytes}-byte safety margin.")
    {
        RequestedBytes = requestedBytes;
        MinimumBytes = minimumBytes;
        AvailableBytes = availableBytes;
        SafetyMarginBytes = safetyMarginBytes;
    }

    public long RequestedBytes { get; }

    public long MinimumBytes { get; }

    public long AvailableBytes { get; }

    public long SafetyMarginBytes { get; }
}

internal sealed class ExpertCache : IDisposable
{
    private readonly ExpertDescriptorLayout layout;
    private readonly TensorDataSource source;
    private readonly LayerState?[] layers;
    private readonly Channel<ReadRequest> readQueue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private readonly long[][] usage;
    private readonly bool[][] pinnedExperts;
    private readonly int hotExpertCount;
    private readonly int repinInterval;
    private long accessClock;
    private long hits;
    private long misses;
    private long evictions;
    private long diskReads;
    private long diskBytes;
    private long diskReadTicks;
    private long foregroundWaitTicks;
    private long prefetches;
    private long usageSinceRepin;
    private long liveRepins;
    private int disposed;

    public ExpertCache(
        ExpertDescriptorLayout layout,
        TensorDataSource source,
        ModelMemoryPlan memoryPlan,
        ExpertCacheOptions options)
        : this(layout, source, memoryPlan.AvailableBytes, memoryPlan.RequiredBytes, options)
    {
    }

    public ExpertCache(
        ExpertDescriptorLayout layout,
        TensorDataSource source,
        long availableMemoryBytes,
        long requiredMemoryBytes,
        ExpertCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (availableMemoryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableMemoryBytes));
        }

        if (requiredMemoryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredMemoryBytes));
        }

        options.Validate();
        this.layout = layout;
        this.source = source;

        if (layout.MoeLayerCount <= 0)
        {
            throw new InvalidOperationException("The model has no MoE layers and does not require an expert cache.");
        }

        var afterModel = Math.Max(0, availableMemoryBytes - requiredMemoryBytes);
        var available = Math.Max(0, afterModel - options.SafetyMarginBytes);
        var minimumBytes = checked(
            layout.SlotBudgetedBytes *
            layout.MoeLayerCount *
            layout.Configuration.ExpertsPerToken);
        if (options.BudgetBytes > available || options.BudgetBytes < minimumBytes)
        {
            throw new ExpertCacheBudgetExceededException(
                options.BudgetBytes,
                minimumBytes,
                available,
                options.SafetyMarginBytes);
        }

        SlotCapacityPerLayer = Math.Min(
            layout.Configuration.RoutedExpertCount,
            checked((int)(options.BudgetBytes /
                checked(layout.SlotBudgetedBytes * layout.MoeLayerCount))));
        if (SlotCapacityPerLayer < layout.Configuration.ExpertsPerToken)
        {
            throw new ExpertCacheBudgetExceededException(
                options.BudgetBytes,
                minimumBytes,
                available,
                options.SafetyMarginBytes);
        }


        hotExpertCount = Math.Min(options.HotExpertCount, Math.Max(0, SlotCapacityPerLayer - 1));
        repinInterval = options.RepinInterval;

        TotalSlotCount = checked(SlotCapacityPerLayer * layout.MoeLayerCount);
        BudgetedBytes = checked((long)TotalSlotCount * layout.SlotBudgetedBytes);
        layers = new LayerState?[layout.Configuration.LayerCount];
        usage = new long[layout.Configuration.LayerCount][];
        pinnedExperts = new bool[layout.Configuration.LayerCount][];
        for (var layer = 0; layer < usage.Length; layer++)
        {
            usage[layer] = new long[layout.Configuration.RoutedExpertCount];
            pinnedExperts[layer] = new bool[layout.Configuration.RoutedExpertCount];
        }
        readQueue = Channel.CreateBounded<ReadRequest>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.WorkerCount == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        var startedWorkers = new List<Task>();
        try
        {
            for (var layer = layout.Configuration.FirstMoeLayer;
                 layer < layout.Configuration.LayerCount;
                 layer++)
            {
                layers[layer] = new LayerState(SlotCapacityPerLayer, layout);
            }

            var workerCount = Math.Min(options.WorkerCount, TotalSlotCount);
            for (var index = 0; index < workerCount; index++)
            {
                startedWorkers.Add(Task.Run(WorkerAsync));
            }

            workers = startedWorkers.ToArray();
        }
        catch
        {
            shutdown.Cancel();
            readQueue.Writer.TryComplete();
            try
            {
                Task.WhenAll(startedWorkers).GetAwaiter().GetResult();
            }
            catch
            {
            }

            foreach (var layer in layers)
            {
                layer?.Dispose();
            }

            shutdown.Dispose();
            throw;
        }
    }

    public long BudgetedBytes { get; }

    public int SlotCapacityPerLayer { get; }

    public int TotalSlotCount { get; }

    internal ExpertDescriptorLayout Layout => layout;

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public async ValueTask<ExpertLeaseBatch> AcquireLayerAsync(
        int layer,
        ReadOnlyMemory<int> expertIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return await AcquireLayerCoreAsync(
            layer,
            expertIds,
            countUsage: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PrefetchLayerAsync(
        int layer,
        ReadOnlyMemory<int> expertIds,
        CancellationToken cancellationToken = default)
    {
        using var leases = await AcquireLayerCoreAsync(
            layer,
            expertIds,
            countUsage: false,
            cancellationToken).ConfigureAwait(false);
        await leases.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref prefetches, leases.Count);
    }

    private async ValueTask<ExpertLeaseBatch> AcquireLayerCoreAsync(
        int layer,
        ReadOnlyMemory<int> expertIds,
        bool countUsage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var state = GetLayer(layer);
        if (expertIds.IsEmpty)
        {
            throw new ArgumentException("At least one expert ID is required.", nameof(expertIds));
        }

        var unique = GetUniqueExpertIds(expertIds);

        if (unique.Length > SlotCapacityPerLayer)
        {
            throw new InvalidOperationException(
                $"Layer {layer} requested {unique.Length} unique experts, but its cache has {SlotCapacityPerLayer} slots.");
        }

        var leases = new ExpertLease[unique.Length];
        var acquired = 0;
        try
        {
            foreach (var expertId in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                leases[acquired++] = await AcquireOneAsync(
                    state,
                    layer,
                    expertId,
                    countUsage,
                    cancellationToken).ConfigureAwait(false);
            }

            return new ExpertLeaseBatch(this, leases);
        }
        catch
        {
            for (var index = 0; index < acquired; index++)
            {
                leases[index].Dispose();
            }

            throw;
        }
    }

    public ExpertCacheSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var histogram = new Dictionary<ExpertKey, long>();
        for (var layer = layout.Configuration.FirstMoeLayer;
             layer < layout.Configuration.LayerCount;
             layer++)
        {
            for (var expertId = 0; expertId < layout.Configuration.RoutedExpertCount; expertId++)
            {
                var count = Interlocked.Read(ref usage[layer][expertId]);
                if (count > 0)
                {
                    histogram.Add(new ExpertKey(layer, expertId, layout.Format), count);
                }
            }
        }

        return new ExpertCacheSnapshot(
            BudgetedBytes,
            SlotCapacityPerLayer,
            TotalSlotCount,
            Interlocked.Read(ref hits),
            Interlocked.Read(ref misses),
            Interlocked.Read(ref evictions),
            Interlocked.Read(ref diskReads),
            Interlocked.Read(ref diskBytes),
            TimeSpan.FromTicks(Interlocked.Read(ref diskReadTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref foregroundWaitTicks)),
            hotExpertCount,
            Interlocked.Read(ref prefetches),
            Interlocked.Read(ref liveRepins),
            new ReadOnlyDictionary<ExpertKey, long>(histogram));
    }

    public void RepinHotExperts()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        for (var layer = layout.Configuration.FirstMoeLayer;
             layer < layout.Configuration.LayerCount;
             layer++)
        {
            RepinLayer(layer);
        }

        Interlocked.Exchange(ref usageSinceRepin, 0);
        Interlocked.Increment(ref liveRepins);
    }

    public IReadOnlyList<int> GetPinnedExperts(int layer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        _ = GetLayer(layer);
        var result = new List<int>(hotExpertCount);
        for (var expertId = 0; expertId < pinnedExperts[layer].Length; expertId++)
        {
            if (Volatile.Read(ref pinnedExperts[layer][expertId]))
            {
                result.Add(expertId);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();
        readQueue.Writer.TryComplete();
        try
        {
            Task.WhenAll(workers).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var layer in layers)
        {
            layer?.Dispose();
        }

        shutdown.Dispose();
    }

    internal void Run(
        ExpertSlot slot,
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (slot.State != SlotState.Loaded || slot.ReferenceCount <= 0)
        {
            throw new InvalidOperationException("Expert cache lease is not ready for execution.");
        }

        slot.Buffer.Run(input, workspace, destination);
    }

    internal async ValueTask WaitReadyAsync(
        ExpertSlot slot,
        CancellationToken cancellationToken)
    {
        var completion = slot.Completion?.Task
            ?? throw new InvalidOperationException("Expert cache slot has no load completion.");
        if (completion.IsCompletedSuccessfully)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(
                ref foregroundWaitTicks,
                Stopwatch.GetElapsedTime(started).Ticks);
        }
    }

    internal void Release(ExpertSlot slot)
    {
        var state = GetLayer(slot.Layer);
        lock (state.Gate)
        {
            if (slot.ReferenceCount <= 0)
            {
                throw new InvalidOperationException("Expert cache lease was released more than once.");
            }

            slot.ReferenceCount--;
            slot.LastAccess = Interlocked.Increment(ref accessClock);
            state.Pulse();
        }
    }

    private async ValueTask<ExpertLease> AcquireOneAsync(
        LayerState state,
        int layer,
        int expertId,
        bool countUsage,
        CancellationToken cancellationToken)
    {
        var key = new ExpertKey(layer, expertId, layout.Format);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadRequest? request = null;
            ExpertLease? lease = null;
            Task? changed = null;
            lock (state.Gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                ExpertSlot? existing = null;
                foreach (var slot in state.Slots)
                {
                    if (slot.Key == key && slot.State is SlotState.Loading or SlotState.Loaded)
                    {
                        existing = slot;
                        break;
                    }
                }

                if (existing is not null)
                {
                    existing.ReferenceCount++;
                    existing.LastAccess = Interlocked.Increment(ref accessClock);
                    if (existing.State == SlotState.Loaded)
                    {
                        Interlocked.Increment(ref hits);
                    }

                    if (countUsage)
                    {
                        Interlocked.Increment(ref usage[layer][expertId]);
                        MaybeRepin();
                    }

                    lease = new ExpertLease(this, existing);
                }
                else
                {
                    var candidate = SelectCandidate(state, layer);
                    if (candidate is null)
                    {
                        changed = state.Changed.Task;
                    }
                    else
                    {
                        if (candidate.State == SlotState.Loaded)
                        {
                            Interlocked.Increment(ref evictions);
                        }

                        candidate.Generation++;
                        candidate.Key = key;
                        candidate.State = SlotState.Loading;
                        candidate.ReferenceCount = 1;
                        candidate.LastAccess = Interlocked.Increment(ref accessClock);
                        candidate.Completion = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        Interlocked.Increment(ref misses);
                        if (countUsage)
                        {
                            Interlocked.Increment(ref usage[layer][expertId]);
                            MaybeRepin();
                        }

                        request = new ReadRequest(state, candidate, candidate.Generation, layout.Get(layer, expertId));
                        lease = new ExpertLease(this, candidate);
                    }
                }
            }

            if (request is not null)
            {
                try
                {
                    await readQueue.Writer.WriteAsync(request, shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    FailReservation(request, exception);
                    lease!.Dispose();
                    throw;
                }

                return lease!;
            }

            if (lease is not null)
            {
                return lease;
            }

            await changed!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ExpertSlot? SelectCandidate(LayerState state, int layer)
    {
        foreach (var slot in state.Slots)
        {
            if (slot.State == SlotState.Empty)
            {
                return slot;
            }

            if (slot.ReferenceCount == 0 && slot.State == SlotState.Failed)
            {
                return slot;
            }
        }

        ExpertSlot? oldestUnpinned = null;
        ExpertSlot? oldest = null;
        foreach (var slot in state.Slots)
        {
            if (slot.ReferenceCount != 0 || slot.State is not (SlotState.Loaded or SlotState.Failed))
            {
                continue;
            }

            if (oldest is null || slot.LastAccess < oldest.LastAccess)
            {
                oldest = slot;
            }

            if (!IsHot(slot, layer) &&
                (oldestUnpinned is null || slot.LastAccess < oldestUnpinned.LastAccess))
            {
                oldestUnpinned = slot;
            }
        }

        return oldestUnpinned ?? oldest;
    }

    private bool IsHot(ExpertSlot slot, int layer)
    {
        if (hotExpertCount == 0 || slot.Key is not { } key)
        {
            return false;
        }

        return Volatile.Read(ref pinnedExperts[layer][key.ExpertId]);
    }

    private void MaybeRepin()
    {
        if (hotExpertCount == 0 || Interlocked.Increment(ref usageSinceRepin) < repinInterval)
        {
            return;
        }

        RepinHotExperts();
    }

    private void RepinLayer(int layer)
    {
        var pins = pinnedExperts[layer];
        Array.Clear(pins);
        if (hotExpertCount == 0)
        {
            return;
        }

        var selectedIds = new int[hotExpertCount];
        var selectedUsage = new long[hotExpertCount];
        Array.Fill(selectedIds, -1);
        for (var expertId = 0; expertId < layout.Configuration.RoutedExpertCount; expertId++)
        {
            var count = Interlocked.Read(ref usage[layer][expertId]);
            if (count <= 0)
            {
                continue;
            }

            var insertAt = hotExpertCount;
            for (var index = 0; index < hotExpertCount; index++)
            {
                if (count > selectedUsage[index] ||
                    (count == selectedUsage[index] && expertId < selectedIds[index]))
                {
                    insertAt = index;
                    break;
                }
            }

            if (insertAt == hotExpertCount)
            {
                continue;
            }

            for (var index = hotExpertCount - 1; index > insertAt; index--)
            {
                selectedUsage[index] = selectedUsage[index - 1];
                selectedIds[index] = selectedIds[index - 1];
            }

            selectedUsage[insertAt] = count;
            selectedIds[insertAt] = expertId;
        }

        foreach (var expertId in selectedIds)
        {
            if (expertId >= 0)
            {
                Volatile.Write(ref pins[expertId], true);
            }
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var request in readQueue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                if (!TryBeginRead(request))
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    request.Slot.Buffer.Load(source, request.Descriptor, shutdown.Token);
                    CompleteRead(request, null, Stopwatch.GetElapsedTime(started));
                }
                catch (Exception exception)
                {
                    CompleteRead(request, exception, Stopwatch.GetElapsedTime(started));
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private bool TryBeginRead(ReadRequest request)
    {
        lock (request.Layer.Gate)
        {
            if (request.Slot.Generation != request.Generation || request.Slot.State != SlotState.Loading)
            {
                return false;
            }

            if (request.Slot.ReferenceCount > 0)
            {
                return true;
            }

            request.Slot.State = SlotState.Empty;
            request.Slot.Key = null;
            request.Slot.Completion?.TrySetCanceled();
            request.Layer.Pulse();
            return false;
        }
    }

    private void CompleteRead(ReadRequest request, Exception? error, TimeSpan elapsed)
    {
        lock (request.Layer.Gate)
        {
            if (request.Slot.Generation != request.Generation || request.Slot.State != SlotState.Loading)
            {
                return;
            }

            Interlocked.Increment(ref diskReads);
            Interlocked.Add(ref diskReadTicks, elapsed.Ticks);
            if (error is null)
            {
                request.Slot.State = SlotState.Loaded;
                Interlocked.Add(ref diskBytes, request.Descriptor.DiskBytes);
                request.Slot.Completion!.TrySetResult(true);
            }
            else
            {
                request.Slot.State = SlotState.Failed;
                request.Slot.Completion!.TrySetException(error);
            }

            request.Layer.Pulse();
        }
    }

    private void FailReservation(ReadRequest request, Exception error)
    {
        lock (request.Layer.Gate)
        {
            if (request.Slot.Generation != request.Generation || request.Slot.State != SlotState.Loading)
            {
                return;
            }

            request.Slot.State = SlotState.Failed;
            request.Slot.Completion!.TrySetException(error);
            request.Layer.Pulse();
        }
    }

    private LayerState GetLayer(int layer)
    {
        if (layer < layout.Configuration.FirstMoeLayer || layer >= layout.Configuration.LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} is not a MoE layer.");
        }

        return layers[layer] ?? throw new InvalidOperationException($"Layer cache is unavailable: {layer}.");
    }

    private int[] GetUniqueExpertIds(ReadOnlyMemory<int> expertIds)
    {
        var unique = new int[expertIds.Length];
        var count = 0;
        foreach (var expertId in expertIds.Span)
        {
            if ((uint)expertId >= (uint)layout.Configuration.RoutedExpertCount)
            {
                throw new ArgumentOutOfRangeException(nameof(expertIds), $"Expert ID is out of range: {expertId}.");
            }

            var found = false;
            for (var index = 0; index < count; index++)
            {
                if (unique[index] == expertId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                unique[count++] = expertId;
            }
        }

        if (count != unique.Length)
        {
            Array.Resize(ref unique, count);
        }

        return unique;
    }

    internal enum SlotState
    {
        Empty,
        Loading,
        Loaded,
        Failed
    }

    private sealed class LayerState : IDisposable
    {
        public LayerState(int capacity, ExpertDescriptorLayout layout)
        {
            Slots = new ExpertSlot[capacity];
            var created = 0;
            try
            {
                for (; created < Slots.Length; created++)
                {
                    Slots[created] = new ExpertSlot(ExpertWeightBuffer.Create(layout));
                }
            }
            catch
            {
                for (var index = 0; index < created; index++)
                {
                    Slots[index].Dispose();
                }

                throw;
            }
        }

        public object Gate { get; } = new();

        public ExpertSlot[] Slots { get; }

        public TaskCompletionSource<bool> Changed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Pulse()
        {
            var changed = Changed;
            Changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult(true);
        }

        public void Dispose()
        {
            lock (Gate)
            {
                foreach (var slot in Slots)
                {
                    slot.Completion?.TrySetException(new ObjectDisposedException(nameof(ExpertCache)));
                    slot.Dispose();
                }

                Pulse();
            }
        }
    }

    internal sealed class ExpertSlot : IDisposable
    {
        public ExpertSlot(ExpertWeightBuffer buffer)
        {
            Buffer = buffer;
        }

        internal ExpertWeightBuffer Buffer { get; }

        internal ExpertKey? Key { get; set; }

        internal SlotState State { get; set; }

        internal int ReferenceCount { get; set; }

        internal long LastAccess { get; set; }

        internal long Generation { get; set; }

        internal int Layer => Key?.Layer ?? throw new InvalidOperationException("Expert cache slot has no key.");

        internal TaskCompletionSource<bool>? Completion { get; set; }

        public void Dispose() => Buffer.Dispose();
    }

    private sealed record ReadRequest(
        LayerState Layer,
        ExpertSlot Slot,
        long Generation,
        ExpertDescriptor Descriptor);
}

internal sealed class ExpertLease : IDisposable
{
    private ExpertCache? cache;
    private ExpertCache.ExpertSlot? slot;

    public ExpertLease(ExpertCache cache, ExpertCache.ExpertSlot slot)
    {
        this.cache = cache;
        this.slot = slot;
    }

    public ValueTask WaitReadyAsync(CancellationToken cancellationToken = default)
        => GetCache().WaitReadyAsync(GetSlot(), cancellationToken);

    public void Run(
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        Span<float> destination)
        => GetCache().Run(GetSlot(), input, workspace, destination);

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref cache, null);
        var leasedSlot = Interlocked.Exchange(ref slot, null);
        if (owner is not null && leasedSlot is not null)
        {
            owner.Release(leasedSlot);
        }
    }

    private ExpertCache GetCache()
    {
        ObjectDisposedException.ThrowIf(cache is null, this);
        return cache;
    }

    private ExpertCache.ExpertSlot GetSlot()
    {
        ObjectDisposedException.ThrowIf(slot is null, this);
        return slot;
    }
}

internal sealed class ExpertLeaseBatch : IDisposable
{
    private readonly ExpertCache owner;
    private ExpertLease[]? leases;

    public ExpertLeaseBatch(ExpertCache owner, ExpertLease[] leases)
    {
        this.owner = owner;
        this.leases = leases;
    }

    public int Count => GetLeases().Length;

    public ExpertLease this[int index] => GetLeases()[index];

    public async ValueTask WaitReadyAsync(CancellationToken cancellationToken = default)
    {
        _ = owner.Layout;
        foreach (var lease in GetLeases())
        {
            await lease.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref leases, null);
        if (current is null)
        {
            return;
        }

        foreach (var lease in current)
        {
            lease.Dispose();
        }
    }

    private ExpertLease[] GetLeases()
    {
        ObjectDisposedException.ThrowIf(leases is null, this);
        return leases;
    }
}
