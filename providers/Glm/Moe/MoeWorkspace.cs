using System.Buffers;

namespace Tomur.Providers.Glm;

internal sealed class MoeWorkspace : IDisposable
{
    private float[]? routerValues;
    private float[]? selectedWeights;
    private int[]? selectedExpertIds;
    private float[]? activations;
    private float[]? outputs;

    public MoeWorkspace(GlmModelConfiguration configuration)
        : this(
            configuration.HiddenSize,
            configuration.RoutedExpertCount,
            configuration.ExpertsPerToken,
            configuration.MoeIntermediateSize,
            checked(configuration.SharedExpertCount * configuration.MoeIntermediateSize))
    {
    }

    public MoeWorkspace(
        int hiddenSize,
        int routedExpertCount,
        int expertsPerToken,
        int moeIntermediateSize,
        int sharedIntermediateSize = 0)
    {
        if (hiddenSize <= 0 || routedExpertCount <= 0 ||
            expertsPerToken <= 0 || expertsPerToken > routedExpertCount ||
            moeIntermediateSize <= 0 || sharedIntermediateSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hiddenSize), "MoE workspace dimensions are invalid.");
        }

        HiddenSize = hiddenSize;
        RoutedExpertCount = routedExpertCount;
        ExpertsPerToken = expertsPerToken;
        MoeIntermediateSize = moeIntermediateSize;
        SharedIntermediateSize = sharedIntermediateSize;
        ActivationIntermediateCapacity = Math.Max(MoeIntermediateSize, SharedIntermediateSize);

        float[]? rentedRouter = null;
        float[]? rentedWeights = null;
        int[]? rentedIds = null;
        float[]? rentedActivations = null;
        float[]? rentedOutputs = null;
        try
        {
            rentedRouter = ArrayPool<float>.Shared.Rent(checked(RoutedExpertCount * 2));
            rentedWeights = ArrayPool<float>.Shared.Rent(ExpertsPerToken);
            rentedIds = ArrayPool<int>.Shared.Rent(ExpertsPerToken);
            rentedActivations = ArrayPool<float>.Shared.Rent(checked(ActivationIntermediateCapacity * 3));
            rentedOutputs = ArrayPool<float>.Shared.Rent(checked(HiddenSize * 3));
            routerValues = rentedRouter;
            selectedWeights = rentedWeights;
            selectedExpertIds = rentedIds;
            activations = rentedActivations;
            outputs = rentedOutputs;
        }
        catch
        {
            Return(rentedRouter);
            Return(rentedWeights);
            Return(rentedIds);
            Return(rentedActivations);
            Return(rentedOutputs);
            throw;
        }
    }

    public int HiddenSize { get; }

    public int RoutedExpertCount { get; }

    public int ExpertsPerToken { get; }

    public int MoeIntermediateSize { get; }

    public int SharedIntermediateSize { get; }

    public int ActivationIntermediateCapacity { get; }

    public long BudgetedBytes => GetBudgetedBytes(
        HiddenSize,
        RoutedExpertCount,
        ExpertsPerToken,
        ActivationIntermediateCapacity);

    internal Span<float> Scores => GetRouterValues().AsSpan(0, RoutedExpertCount);

    internal Span<float> AdjustedScores => GetRouterValues().AsSpan(RoutedExpertCount, RoutedExpertCount);

    internal Span<float> SelectedWeights => GetSelectedWeights().AsSpan(0, ExpertsPerToken);

    internal Span<int> SelectedExpertIds => GetSelectedExpertIds().AsSpan(0, ExpertsPerToken);

    internal ReadOnlyMemory<int> SelectedExpertIdMemory
        => GetSelectedExpertIds().AsMemory(0, ExpertsPerToken);

    internal Span<float> GetExpertActivations(int intermediateSize)
    {
        if (intermediateSize <= 0 || intermediateSize > ActivationIntermediateCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(intermediateSize));
        }

        return GetActivations().AsSpan(0, checked(intermediateSize * 3));
    }

    internal Span<float> RoutedOutput => GetOutputs().AsSpan(0, HiddenSize);

    internal Span<float> SharedOutput => GetOutputs().AsSpan(HiddenSize, HiddenSize);

    internal Span<float> ExpertOutput => GetOutputs().AsSpan(checked(HiddenSize * 2), HiddenSize);

    internal void EnsureCompatible(GlmModelConfiguration configuration)
        => EnsureCompatible(
            configuration.HiddenSize,
            configuration.RoutedExpertCount,
            configuration.ExpertsPerToken,
            configuration.MoeIntermediateSize,
            checked(configuration.SharedExpertCount * configuration.MoeIntermediateSize));

    internal void EnsureCompatible(
        int hiddenSize,
        int routedExpertCount,
        int expertsPerToken,
        int moeIntermediateSize,
        int sharedIntermediateSize = 0)
    {
        _ = GetRouterValues();
        _ = GetSelectedWeights();
        _ = GetSelectedExpertIds();
        _ = GetActivations();
        _ = GetOutputs();
        if (HiddenSize != hiddenSize ||
            RoutedExpertCount != routedExpertCount ||
            ExpertsPerToken != expertsPerToken ||
            MoeIntermediateSize != moeIntermediateSize ||
            SharedIntermediateSize != sharedIntermediateSize)
        {
            throw new ArgumentException("MoE workspace dimensions do not match the model configuration.");
        }
    }

    public void Dispose()
    {
        Return(Interlocked.Exchange(ref routerValues, null));
        Return(Interlocked.Exchange(ref selectedWeights, null));
        Return(Interlocked.Exchange(ref selectedExpertIds, null));
        Return(Interlocked.Exchange(ref activations, null));
        Return(Interlocked.Exchange(ref outputs, null));
    }

    public static long GetBudgetedBytes(GlmModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return GetBudgetedBytes(
            configuration.HiddenSize,
            configuration.RoutedExpertCount,
            configuration.ExpertsPerToken,
            Math.Max(
                configuration.MoeIntermediateSize,
                checked(configuration.SharedExpertCount * configuration.MoeIntermediateSize)));
    }

    public static long GetBudgetedBytes(
        int hiddenSize,
        int routedExpertCount,
        int expertsPerToken,
        int intermediateSize)
        => CalculateBudgetedBytes(hiddenSize, routedExpertCount, expertsPerToken, intermediateSize);

    private static long CalculateBudgetedBytes(
        int hiddenSize,
        int routedExpertCount,
        int expertsPerToken,
        int intermediateSize)
        => checked(
            checked((long)(checked(routedExpertCount * 2) + expertsPerToken) * sizeof(float)) +
            checked((long)expertsPerToken * sizeof(int)) +
            checked((long)checked(intermediateSize * 3) * sizeof(float)) +
            checked((long)checked(hiddenSize * 3) * sizeof(float)));

    private float[] GetRouterValues()
    {
        ObjectDisposedException.ThrowIf(routerValues is null, this);
        return routerValues;
    }

    private float[] GetSelectedWeights()
    {
        ObjectDisposedException.ThrowIf(selectedWeights is null, this);
        return selectedWeights;
    }

    private int[] GetSelectedExpertIds()
    {
        ObjectDisposedException.ThrowIf(selectedExpertIds is null, this);
        return selectedExpertIds;
    }

    private float[] GetActivations()
    {
        ObjectDisposedException.ThrowIf(activations is null, this);
        return activations;
    }

    private float[] GetOutputs()
    {
        ObjectDisposedException.ThrowIf(outputs is null, this);
        return outputs;
    }

    private static void Return<T>(T[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<T>.Shared.Return(buffer, clearArray: true);
        }
    }
}
