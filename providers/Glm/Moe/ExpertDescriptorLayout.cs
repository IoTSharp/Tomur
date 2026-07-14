namespace Tomur.Providers.Glm;

internal enum ExpertWeightFormat
{
    Float32,
    Float16,
    BFloat16,
    Int8,
    Int4
}

internal readonly record struct ExpertKey(
    int Layer,
    int ExpertId,
    ExpertWeightFormat Format);

internal sealed record ExpertLayoutConfiguration(
    int HiddenSize,
    int LayerCount,
    int FirstMoeLayer,
    int RoutedExpertCount,
    int ExpertsPerToken,
    int MoeIntermediateSize);

internal sealed class ExpertDescriptor
{
    public ExpertDescriptor(
        ExpertKey key,
        TensorDescriptor gate,
        TensorDescriptor up,
        TensorDescriptor down,
        QuantizedTensorDescriptor? quantizedGate = null,
        QuantizedTensorDescriptor? quantizedUp = null,
        QuantizedTensorDescriptor? quantizedDown = null)
    {
        Key = key;
        Gate = gate;
        Up = up;
        Down = down;
        QuantizedGate = quantizedGate;
        QuantizedUp = quantizedUp;
        QuantizedDown = quantizedDown;

        DiskBytes = checked(gate.PhysicalLength + up.PhysicalLength + down.PhysicalLength);
        if (quantizedGate is not null && quantizedUp is not null && quantizedDown is not null)
        {
            DiskBytes = checked(
                DiskBytes +
                quantizedGate.Scales.PhysicalLength +
                quantizedUp.Scales.PhysicalLength +
                quantizedDown.Scales.PhysicalLength);
        }
    }

    public ExpertKey Key { get; }

    public TensorDescriptor Gate { get; }

    public TensorDescriptor Up { get; }

    public TensorDescriptor Down { get; }

    public QuantizedTensorDescriptor? QuantizedGate { get; }

    public QuantizedTensorDescriptor? QuantizedUp { get; }

    public QuantizedTensorDescriptor? QuantizedDown { get; }

    public long DiskBytes { get; }
}

internal sealed class ExpertDescriptorLayout
{
    private const string WeightSuffix = ".weight";
    private readonly ExpertDescriptor?[][] descriptors;

    private ExpertDescriptorLayout(
        ExpertLayoutConfiguration configuration,
        ExpertWeightFormat format,
        QuantizedValueEncoding valueEncoding,
        ExpertDescriptor?[][] descriptors,
        long slotBudgetedBytes)
    {
        Configuration = configuration;
        Format = format;
        ValueEncoding = valueEncoding;
        this.descriptors = descriptors;
        SlotBudgetedBytes = slotBudgetedBytes;
    }

    public ExpertLayoutConfiguration Configuration { get; }

    public ExpertWeightFormat Format { get; }

    public QuantizedValueEncoding ValueEncoding { get; }

    public int MoeLayerCount => Configuration.LayerCount - Configuration.FirstMoeLayer;

    public long SlotBudgetedBytes { get; }

    public static ExpertDescriptorLayout Create(
        GlmModelConfiguration configuration,
        string quantization,
        string quantizationLayout,
        SafeTensorCatalog tensors)
        => Create(
            new ExpertLayoutConfiguration(
                configuration.HiddenSize,
                configuration.LayerCount,
                configuration.FirstMoeLayer,
                configuration.RoutedExpertCount,
                configuration.ExpertsPerToken,
                configuration.MoeIntermediateSize),
            quantization,
            quantizationLayout,
            tensors);

    public static ExpertDescriptorLayout Create(
        ExpertLayoutConfiguration configuration,
        string quantization,
        string quantizationLayout,
        SafeTensorCatalog tensors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(quantization);
        ArgumentException.ThrowIfNullOrWhiteSpace(quantizationLayout);
        ArgumentNullException.ThrowIfNull(tensors);

        var format = ParseFormat(quantization);
        var valueEncoding = quantizationLayout.Equals("packed-offset", StringComparison.OrdinalIgnoreCase)
            ? QuantizedValueEncoding.OffsetBinary
            : QuantizedValueEncoding.TwosComplement;
        var descriptors = new ExpertDescriptor?[configuration.LayerCount][];
        for (var layer = configuration.FirstMoeLayer; layer < configuration.LayerCount; layer++)
        {
            descriptors[layer] = new ExpertDescriptor[configuration.RoutedExpertCount];
            for (var expertId = 0; expertId < configuration.RoutedExpertCount; expertId++)
            {
                descriptors[layer][expertId] = CreateDescriptor(
                    configuration,
                    tensors,
                    format,
                    quantizationLayout,
                    layer,
                    expertId);
            }
        }

        return new ExpertDescriptorLayout(
            configuration,
            format,
            valueEncoding,
            descriptors,
            GetSlotBudgetedBytes(configuration, format));
    }

    public ExpertDescriptor Get(int layer, int expertId)
    {
        if (layer < Configuration.FirstMoeLayer || layer >= Configuration.LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} is not a MoE layer.");
        }

        if ((uint)expertId >= (uint)Configuration.RoutedExpertCount)
        {
            throw new ArgumentOutOfRangeException(nameof(expertId));
        }

        return descriptors[layer][expertId]
            ?? throw new InvalidOperationException($"Expert descriptor is unavailable for layer {layer}, expert {expertId}.");
    }

    public static string GetScaleTensorName(string weightName)
        => GetScaleTensorName(weightName, "separate-scales");

    public static string GetScaleTensorName(string weightName, string quantizationLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weightName);
        ArgumentException.ThrowIfNullOrWhiteSpace(quantizationLayout);
        if (!weightName.EndsWith(WeightSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Quantized expert tensor names must end in '.weight'.", nameof(weightName));
        }

        return quantizationLayout.Equals("packed-offset", StringComparison.OrdinalIgnoreCase) ||
               quantizationLayout.Equals("rowwise-qs", StringComparison.OrdinalIgnoreCase)
            ? $"{weightName}.qs"
            : $"{weightName[..^WeightSuffix.Length]}.scales";
    }

    private static ExpertDescriptor CreateDescriptor(
        ExpertLayoutConfiguration configuration,
        SafeTensorCatalog tensors,
        ExpertWeightFormat format,
        string quantizationLayout,
        int layer,
        int expertId)
    {
        var prefix = $"model.layers.{layer}.mlp.experts.{expertId}.";
        var gate = tensors.GetRequired($"{prefix}gate_proj.weight");
        var up = tensors.GetRequired($"{prefix}up_proj.weight");
        var down = tensors.GetRequired($"{prefix}down_proj.weight");
        var key = new ExpertKey(layer, expertId, format);

        if (format is ExpertWeightFormat.Float32 or ExpertWeightFormat.Float16 or ExpertWeightFormat.BFloat16)
        {
            var expectedType = format switch
            {
                ExpertWeightFormat.Float32 => TensorDataType.Float32,
                ExpertWeightFormat.Float16 => TensorDataType.Float16,
                ExpertWeightFormat.BFloat16 => TensorDataType.BFloat16,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
            ValidateFloating(gate, expectedType, configuration.MoeIntermediateSize, configuration.HiddenSize);
            ValidateFloating(up, expectedType, configuration.MoeIntermediateSize, configuration.HiddenSize);
            ValidateFloating(down, expectedType, configuration.HiddenSize, configuration.MoeIntermediateSize);
            return new ExpertDescriptor(key, gate, up, down);
        }

        var quantizedFormat = format == ExpertWeightFormat.Int8
            ? QuantizedTensorFormat.Int8
            : QuantizedTensorFormat.Int4;
        var valueEncoding = quantizationLayout.Equals("packed-offset", StringComparison.OrdinalIgnoreCase)
            ? QuantizedValueEncoding.OffsetBinary
            : QuantizedValueEncoding.TwosComplement;
        var quantizedGate = CreateQuantized(
            tensors,
            gate,
            new QuantizedTensorShape(
                quantizedFormat,
                configuration.MoeIntermediateSize,
                configuration.HiddenSize,
                valueEncoding),
            quantizationLayout);
        var quantizedUp = CreateQuantized(
            tensors,
            up,
            new QuantizedTensorShape(
                quantizedFormat,
                configuration.MoeIntermediateSize,
                configuration.HiddenSize,
                valueEncoding),
            quantizationLayout);
        var quantizedDown = CreateQuantized(
            tensors,
            down,
            new QuantizedTensorShape(
                quantizedFormat,
                configuration.HiddenSize,
                configuration.MoeIntermediateSize,
                valueEncoding),
            quantizationLayout);
        return new ExpertDescriptor(
            key,
            gate,
            up,
            down,
            quantizedGate,
            quantizedUp,
            quantizedDown);
    }

    private static QuantizedTensorDescriptor CreateQuantized(
        SafeTensorCatalog tensors,
        TensorDescriptor payload,
        QuantizedTensorShape shape,
        string quantizationLayout)
    {
        var scales = tensors.GetRequired(GetScaleTensorName(payload.Name, quantizationLayout));
        if (!scales.LogicalShape.SequenceEqual([shape.Rows]))
        {
            throw new InvalidDataException(
                $"Quantized expert scale tensor '{scales.Name}' has shape " +
                $"[{string.Join(", ", scales.LogicalShape)}]; expected [{shape.Rows}].");
        }

        return new QuantizedTensorDescriptor(payload, scales, shape);
    }

    private static void ValidateFloating(
        TensorDescriptor descriptor,
        TensorDataType expectedType,
        int rows,
        int columns)
    {
        if (descriptor.DataType != expectedType)
        {
            throw new InvalidDataException(
                $"Routed expert tensor '{descriptor.Name}' uses {descriptor.DataTypeName}; " +
                $"the model manifest requires {expectedType.ToSafeTensorName()} expert storage.");
        }

        if (!descriptor.LogicalShape.SequenceEqual([rows, columns]))
        {
            throw new InvalidDataException(
                $"Routed expert tensor '{descriptor.Name}' has shape " +
                $"[{string.Join(", ", descriptor.LogicalShape)}]; expected [{rows}, {columns}].");
        }

        if (descriptor.ElementCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Routed expert tensor '{descriptor.Name}' exceeds the single-buffer element limit.");
        }
    }

    private static ExpertWeightFormat ParseFormat(string value)
        => value.ToLowerInvariant() switch
        {
            "f32" => ExpertWeightFormat.Float32,
            "f16" => ExpertWeightFormat.Float16,
            "bf16" => ExpertWeightFormat.BFloat16,
            "int8" => ExpertWeightFormat.Int8,
            "int4" => ExpertWeightFormat.Int4,
            _ => throw new InvalidDataException($"Managed expert storage format is not supported: {value}")
        };

    private static long GetSlotBudgetedBytes(
        ExpertLayoutConfiguration configuration,
        ExpertWeightFormat format)
    {
        var gateElements = checked((long)configuration.MoeIntermediateSize * configuration.HiddenSize);
        var downElements = checked((long)configuration.HiddenSize * configuration.MoeIntermediateSize);
        if (format is ExpertWeightFormat.Float32 or ExpertWeightFormat.Float16 or ExpertWeightFormat.BFloat16)
        {
            return checked(checked((gateElements * 2) + downElements) * sizeof(float));
        }

        var quantizedFormat = format == ExpertWeightFormat.Int8
            ? QuantizedTensorFormat.Int8
            : QuantizedTensorFormat.Int4;
        var gate = new QuantizedTensorShape(
            quantizedFormat,
            configuration.MoeIntermediateSize,
            configuration.HiddenSize);
        var down = new QuantizedTensorShape(
            quantizedFormat,
            configuration.HiddenSize,
            configuration.MoeIntermediateSize);
        return checked(
            checked((long)gate.PayloadByteLength * 2) +
            down.PayloadByteLength +
            checked((long)((gate.ScaleCount * 2) + down.ScaleCount) * sizeof(float)));
    }
}
