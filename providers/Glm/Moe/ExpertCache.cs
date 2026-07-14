using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace Tomur.Providers.Glm;

internal sealed record ExpertCacheOptions(
    long BudgetBytes,
    int WorkerCount = 2,
    int QueueCapacity = 8,
    long SafetyMarginBytes = 0)
{
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
    private long accessClock;
    private long hits;
    private long misses;
    private long evictions;
    private long diskReads;
    private long diskBytes;
    private long diskReadTicks;
    private long foregroundWaitTicks;
    private int disposed;

    public ExpertCache(
        ExpertDescriptorLayout layout,
        TensorDataSource source,
        ModelMemoryPlan memoryPlan,
        ExpertCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(memoryPlan);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.layout = layout;
        this.source = source;

        if (layout.MoeLayerCount <= 0)
        {
            throw new InvalidOperationException("The model has no MoE layers and does not require an expert cache.");
        }

        var afterModel = Math.Max(0, memoryPlan.AvailableBytes - memoryPlan.RequiredBytes);
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

        TotalSlotCount = checked(SlotCapacityPerLayer * layout.MoeLayerCount);
        BudgetedBytes = checked((long)TotalSlotCount * layout.SlotBudgetedBytes);
        layers = new LayerState?[layout.Configuration.LayerCount];
        usage = new long[layout.Configuration.LayerCount][];
        for (var layer = 0; layer < usage.Length; layer++)
        {
            usage[layer] = new long[layout.Configuration.RoutedExpertCount];
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
        var state = GetLayer(layer);
        if (expertIds.IsEmpty)
        {
            throw new ArgumentException("At least one expert ID is required.", nameof(expertIds));
        }

        var unique = GetUniqueExpertIds(expertIds);

        if (unique.Count > SlotCapacityPerLayer)
        {
            throw new InvalidOperationException(
                $"Layer {layer} requested {unique.Count} unique experts, but its cache has {SlotCapacityPerLayer} slots.");
        }

        var leases = new List<ExpertLease>(unique.Count);
        try
        {
            foreach (var expertId in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                leases.Add(await AcquireOneAsync(state, layer, expertId, cancellationToken).ConfigureAwait(false));
            }

            return new ExpertLeaseBatch(this, leases.ToArray());
        }
        catch
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
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
            new ReadOnlyDictionary<ExpertKey, long>(histogram));
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
                var existing = state.Slots.FirstOrDefault(slot =>
                    slot.Key == key && slot.State is SlotState.Loading or SlotState.Loaded);
                if (existing is not null)
                {
                    existing.ReferenceCount++;
                    existing.LastAccess = Interlocked.Increment(ref accessClock);
                    if (existing.State == SlotState.Loaded)
                    {
                        Interlocked.Increment(ref hits);
                    }

                    Interlocked.Increment(ref usage[layer][expertId]);
                    lease = new ExpertLease(this, existing);
                }
                else
                {
                    var candidate = state.Slots.FirstOrDefault(slot => slot.State == SlotState.Empty);
                    candidate ??= state.Slots
                        .Where(slot => slot.ReferenceCount == 0 && slot.State is SlotState.Loaded or SlotState.Failed)
                        .OrderBy(slot => slot.LastAccess)
                        .FirstOrDefault();
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
                        Interlocked.Increment(ref usage[layer][expertId]);
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

    private List<int> GetUniqueExpertIds(ReadOnlyMemory<int> expertIds)
    {
        var unique = new List<int>(expertIds.Length);
        var seen = new HashSet<int>();
        foreach (var expertId in expertIds.Span)
        {
            if ((uint)expertId >= (uint)layout.Configuration.RoutedExpertCount)
            {
                throw new ArgumentOutOfRangeException(nameof(expertIds), $"Expert ID is out of range: {expertId}.");
            }

            if (seen.Add(expertId))
            {
                unique.Add(expertId);
            }
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
