using System.Buffers;

namespace Tomur.Providers.Glm;

internal sealed class TensorWorkspace : IDisposable
{
    private float[]? activations;
    private sbyte[]? quantized;
    private float[]? outputs;

    public TensorWorkspace(int activationCapacity, int quantizationCapacity, int outputCapacity)
    {
        RequirePositive(activationCapacity, nameof(activationCapacity));
        RequirePositive(quantizationCapacity, nameof(quantizationCapacity));
        RequirePositive(outputCapacity, nameof(outputCapacity));

        float[]? rentedActivations = null;
        sbyte[]? rentedQuantized = null;
        float[]? rentedOutputs = null;
        try
        {
            rentedActivations = ArrayPool<float>.Shared.Rent(activationCapacity);
            rentedQuantized = ArrayPool<sbyte>.Shared.Rent(quantizationCapacity);
            rentedOutputs = ArrayPool<float>.Shared.Rent(outputCapacity);
            activations = rentedActivations;
            quantized = rentedQuantized;
            outputs = rentedOutputs;
        }
        catch
        {
            Return(rentedActivations);
            Return(rentedQuantized);
            Return(rentedOutputs);
            throw;
        }

        ActivationCapacity = activationCapacity;
        QuantizationCapacity = quantizationCapacity;
        OutputCapacity = outputCapacity;
    }

    public int ActivationCapacity { get; }

    public int QuantizationCapacity { get; }

    public int OutputCapacity { get; }

    public long BudgetedBytes => checked(
        ((long)ActivationCapacity * sizeof(float)) +
        QuantizationCapacity +
        ((long)OutputCapacity * sizeof(float)));

    public Span<float> GetActivations(int length)
        => Slice(GetActivations(), length, ActivationCapacity, nameof(length));

    public Span<sbyte> GetQuantized(int length)
        => Slice(GetQuantized(), length, QuantizationCapacity, nameof(length));

    public Span<float> GetOutputs(int length)
        => Slice(GetOutputs(), length, OutputCapacity, nameof(length));

    public Memory<float> GetOutputMemory(int length)
    {
        if (length < 0 || length > OutputCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return GetOutputs().AsMemory(0, length);
    }

    public void Dispose()
    {
        var activationBuffer = Interlocked.Exchange(ref activations, null);
        var quantizedBuffer = Interlocked.Exchange(ref quantized, null);
        var outputBuffer = Interlocked.Exchange(ref outputs, null);
        Return(activationBuffer);
        Return(quantizedBuffer);
        Return(outputBuffer);
    }

    private float[] GetActivations()
    {
        ObjectDisposedException.ThrowIf(activations is null, this);
        return activations;
    }

    private sbyte[] GetQuantized()
    {
        ObjectDisposedException.ThrowIf(quantized is null, this);
        return quantized;
    }

    private float[] GetOutputs()
    {
        ObjectDisposedException.ThrowIf(outputs is null, this);
        return outputs;
    }

    private static Span<T> Slice<T>(T[] buffer, int length, int capacity, string parameterName)
    {
        if (length < 0 || length > capacity)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return buffer.AsSpan(0, length);
    }

    private static void RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void Return<T>(T[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<T>.Shared.Return(buffer, clearArray: true);
        }
    }
}
