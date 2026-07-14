namespace Tomur.Providers.Glm;

internal sealed class ResidentTensor<T> : IDisposable
    where T : unmanaged
{
    private T[]? values;

    public ResidentTensor(TensorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ElementCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Tensor '{descriptor.Name}' has {descriptor.ElementCount} elements and cannot be represented by one resident managed buffer.");
        }

        Descriptor = descriptor;
        Length = checked((int)descriptor.ElementCount);
        values = GC.AllocateUninitializedArray<T>(Length);
    }

    public TensorDescriptor Descriptor { get; }

    public int Length { get; }

    public Memory<T> Memory => GetValues().AsMemory(0, Length);

    public ReadOnlyMemory<T> ReadOnlyMemory => GetValues().AsMemory(0, Length);

    public Span<T> Span => GetValues().AsSpan(0, Length);

    public ReadOnlySpan<T> ReadOnlySpan => GetValues().AsSpan(0, Length);

    public void Dispose()
    {
        values = null;
    }

    private T[] GetValues()
    {
        ObjectDisposedException.ThrowIf(values is null, this);
        return values;
    }
}
