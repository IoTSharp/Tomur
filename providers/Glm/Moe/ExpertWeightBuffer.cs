using System.Buffers;

namespace Tomur.Providers.Glm;

internal abstract class ExpertWeightBuffer : IDisposable
{
    public abstract long BudgetedBytes { get; }

    public abstract void Load(
        TensorDataSource source,
        ExpertDescriptor descriptor,
        CancellationToken cancellationToken);

    public abstract void Run(
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        Span<float> destination);

    public abstract void Dispose();

    public static ExpertWeightBuffer Create(ExpertDescriptorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Format switch
        {
            ExpertWeightFormat.Float32 or
            ExpertWeightFormat.Float16 or
            ExpertWeightFormat.BFloat16 => new FloatingExpertWeightBuffer(layout.Configuration),
            ExpertWeightFormat.Int8 or
            ExpertWeightFormat.Int4 => new QuantizedExpertWeightBuffer(layout),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.Format, null)
        };
    }
}

internal sealed class FloatingExpertWeightBuffer : ExpertWeightBuffer
{
    private readonly int hiddenSize;
    private readonly int intermediateSize;
    private float[]? gate;
    private float[]? up;
    private float[]? down;
    private bool loaded;

    public FloatingExpertWeightBuffer(GlmModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        hiddenSize = configuration.HiddenSize;
        intermediateSize = configuration.MoeIntermediateSize;
        var inputProjectionLength = checked(hiddenSize * intermediateSize);
        var outputProjectionLength = checked(intermediateSize * hiddenSize);

        float[]? rentedGate = null;
        float[]? rentedUp = null;
        float[]? rentedDown = null;
        try
        {
            rentedGate = ArrayPool<float>.Shared.Rent(inputProjectionLength);
            rentedUp = ArrayPool<float>.Shared.Rent(inputProjectionLength);
            rentedDown = ArrayPool<float>.Shared.Rent(outputProjectionLength);
            gate = rentedGate;
            up = rentedUp;
            down = rentedDown;
        }
        catch
        {
            Return(rentedGate);
            Return(rentedUp);
            Return(rentedDown);
            throw;
        }
    }

    public override long BudgetedBytes => checked(
        checked((long)hiddenSize * intermediateSize * 3) * sizeof(float));

    public override void Load(
        TensorDataSource source,
        ExpertDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(descriptor);
        var gateBuffer = GetGate();
        var upBuffer = GetUp();
        var downBuffer = GetDown();
        loaded = false;

        var inputProjectionLength = checked(hiddenSize * intermediateSize);
        var outputProjectionLength = checked(hiddenSize * intermediateSize);
        source.ReadFloat32(
            descriptor.Gate,
            gateBuffer.AsSpan(0, inputProjectionLength),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.ReadFloat32(
            descriptor.Up,
            upBuffer.AsSpan(0, inputProjectionLength),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.ReadFloat32(
            descriptor.Down,
            downBuffer.AsSpan(0, outputProjectionLength),
            cancellationToken);
        loaded = true;
    }

    public override void Run(
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        Span<float> destination)
    {
        if (!loaded)
        {
            throw new InvalidOperationException("Floating expert buffer does not contain a complete expert.");
        }

        var gateWeight = GetGate().AsSpan(0, checked(hiddenSize * intermediateSize));
        var upWeight = GetUp().AsSpan(0, checked(hiddenSize * intermediateSize));
        var downWeight = GetDown().AsSpan(0, checked(hiddenSize * intermediateSize));
        var activations = workspace.GetExpertActivations(intermediateSize);
        var gateOutput = activations[..intermediateSize];
        var upOutput = activations.Slice(intermediateSize, intermediateSize);
        var activated = activations.Slice(checked(intermediateSize * 2), intermediateSize);
        ScalarKernels.MatVec(
            gateWeight,
            intermediateSize,
            hiddenSize,
            hiddenSize,
            input,
            gateOutput);
        ScalarKernels.MatVec(
            upWeight,
            intermediateSize,
            hiddenSize,
            hiddenSize,
            input,
            upOutput);
        ScalarKernels.SiLU(gateOutput, activated);
        ScalarKernels.Multiply(activated, upOutput, gateOutput);
        ScalarKernels.MatVec(
            downWeight,
            hiddenSize,
            intermediateSize,
            intermediateSize,
            gateOutput,
            destination);
    }

    public override void Dispose()
    {
        loaded = false;
        Return(Interlocked.Exchange(ref gate, null));
        Return(Interlocked.Exchange(ref up, null));
        Return(Interlocked.Exchange(ref down, null));
    }

    private float[] GetGate()
    {
        ObjectDisposedException.ThrowIf(gate is null, this);
        return gate;
    }

    private float[] GetUp()
    {
        ObjectDisposedException.ThrowIf(up is null, this);
        return up;
    }

    private float[] GetDown()
    {
        ObjectDisposedException.ThrowIf(down is null, this);
        return down;
    }

    private static void Return(float[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<float>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal sealed class QuantizedExpertWeightBuffer : ExpertWeightBuffer
{
    private readonly ExpertSlab slab;
    private readonly ExpertWeightFormat format;

    public QuantizedExpertWeightBuffer(ExpertDescriptorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        format = layout.Format;
        var quantizedFormat = format == ExpertWeightFormat.Int8
            ? QuantizedTensorFormat.Int8
            : QuantizedTensorFormat.Int4;
        slab = new ExpertSlab(
            new QuantizedTensorShape(
                quantizedFormat,
                layout.Configuration.MoeIntermediateSize,
                layout.Configuration.HiddenSize),
            new QuantizedTensorShape(
                quantizedFormat,
                layout.Configuration.MoeIntermediateSize,
                layout.Configuration.HiddenSize),
            new QuantizedTensorShape(
                quantizedFormat,
                layout.Configuration.HiddenSize,
                layout.Configuration.MoeIntermediateSize));
    }

    public override long BudgetedBytes => slab.BudgetedBytes;

    public override void Load(
        TensorDataSource source,
        ExpertDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.QuantizedGate is null ||
            descriptor.QuantizedUp is null ||
            descriptor.QuantizedDown is null)
        {
            throw new InvalidDataException(
                $"Expert {descriptor.Key.Layer}:{descriptor.Key.ExpertId} is missing its quantized descriptor.");
        }

        slab.Load(
            source,
            descriptor.QuantizedGate,
            descriptor.QuantizedUp,
            descriptor.QuantizedDown,
            cancellationToken);
    }

    public override void Run(
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        Span<float> destination)
    {
        var intermediateSize = workspace.MoeIntermediateSize;
        var activations = workspace.GetExpertActivations(intermediateSize);
        var gateOutput = activations[..intermediateSize];
        var upOutput = activations.Slice(intermediateSize, intermediateSize);
        var activated = activations.Slice(checked(intermediateSize * 2), intermediateSize);
        RunProjection(slab.Gate, input, gateOutput);
        RunProjection(slab.Up, input, upOutput);
        ScalarKernels.SiLU(gateOutput, activated);
        ScalarKernels.Multiply(activated, upOutput, gateOutput);
        RunProjection(slab.Down, gateOutput, destination);
    }

    public override void Dispose() => slab.Dispose();

    private void RunProjection(
        QuantizedTensorView weight,
        ReadOnlySpan<float> input,
        Span<float> destination)
    {
        if (format == ExpertWeightFormat.Int8)
        {
            ScalarKernels.Int8DequantMatVec(weight, input, destination);
        }
        else
        {
            ScalarKernels.Int4DequantMatVec(weight, input, destination);
        }
    }
}
