using System.Buffers;

namespace Tomur.Providers.Glm;

internal sealed class AttentionWorkspace : IDisposable
{
    private float[]? activations;
    private float[]? scores;

    public AttentionWorkspace(
        GlmModelConfiguration configuration,
        int activationCapacity,
        int scoreCapacity)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (activationCapacity < ModelMemoryPlan.GetAttentionActivationCapacity(configuration))
        {
            throw new ArgumentOutOfRangeException(nameof(activationCapacity));
        }

        if (scoreCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scoreCapacity));
        }

        float[]? rentedActivations = null;
        float[]? rentedScores = null;
        try
        {
            rentedActivations = ArrayPool<float>.Shared.Rent(activationCapacity);
            rentedScores = ArrayPool<float>.Shared.Rent(scoreCapacity);
            activations = rentedActivations;
            scores = rentedScores;
        }
        catch
        {
            Return(rentedActivations);
            Return(rentedScores);
            throw;
        }

        ActivationCapacity = activationCapacity;
        ScoreCapacity = scoreCapacity;
        LayerCount = configuration.LayerCount;
        AttentionHeadCount = configuration.AttentionHeadCount;
        QueryLoraRank = configuration.QueryLoraRank;
        KeyValueLoraRank = configuration.KeyValueLoraRank;
        QueryKeyNopeHeadSize = configuration.QueryKeyNopeHeadSize;
        QueryKeyRopeHeadSize = configuration.QueryKeyRopeHeadSize;
        ValueHeadSize = configuration.ValueHeadSize;
        HiddenSize = configuration.HiddenSize;
    }

    public int ActivationCapacity { get; }

    public int ScoreCapacity { get; }

    public long BudgetedBytes => checked(
        checked((long)ActivationCapacity * sizeof(float)) +
        checked((long)ScoreCapacity * sizeof(float)));

    private int LayerCount { get; }

    private int AttentionHeadCount { get; }

    private int QueryLoraRank { get; }

    private int KeyValueLoraRank { get; }

    private int QueryKeyNopeHeadSize { get; }

    private int QueryKeyRopeHeadSize { get; }

    private int ValueHeadSize { get; }

    private int HiddenSize { get; }

    internal Span<float> GetActivations(int length)
        => Slice(GetActivations(), length, ActivationCapacity, nameof(length));

    internal Span<float> GetScores(int length)
        => Slice(GetScores(), length, ScoreCapacity, nameof(length));

    internal void EnsureCompatible(GlmModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = GetActivations();
        _ = GetScores();
        if (LayerCount != configuration.LayerCount ||
            AttentionHeadCount != configuration.AttentionHeadCount ||
            QueryLoraRank != configuration.QueryLoraRank ||
            KeyValueLoraRank != configuration.KeyValueLoraRank ||
            QueryKeyNopeHeadSize != configuration.QueryKeyNopeHeadSize ||
            QueryKeyRopeHeadSize != configuration.QueryKeyRopeHeadSize ||
            ValueHeadSize != configuration.ValueHeadSize ||
            HiddenSize != configuration.HiddenSize)
        {
            throw new ArgumentException("Attention workspace dimensions do not match the model configuration.");
        }
    }

    public void Dispose()
    {
        Return(Interlocked.Exchange(ref activations, null));
        Return(Interlocked.Exchange(ref scores, null));
    }

    private float[] GetActivations()
    {
        ObjectDisposedException.ThrowIf(activations is null, this);
        return activations;
    }

    private float[] GetScores()
    {
        ObjectDisposedException.ThrowIf(scores is null, this);
        return scores;
    }

    private static Span<T> Slice<T>(T[] buffer, int length, int capacity, string parameterName)
    {
        if (length < 0 || length > capacity)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return buffer.AsSpan(0, length);
    }

    private static void Return(float[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }
}
