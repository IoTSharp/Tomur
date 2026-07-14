namespace Tomur.Providers.Glm;

internal sealed record ModelMemoryPlan(
    int ContextSize,
    long ResidentBytes,
    long KvBytes,
    long ScratchBytes,
    long RequiredBytes,
    long AvailableBytes,
    int ActivationCapacity,
    int QuantizationCapacity,
    int OutputCapacity,
    int AttentionScoreCapacity)
{
    public static ModelMemoryPlan Create(
        GlmModelConfiguration configuration,
        IReadOnlyList<ResidentWeightSpec> residentWeights,
        int contextSize,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(residentWeights);
        if (contextSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextSize));
        }

        if (availableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableBytes));
        }

        long residentBytes = 0;
        foreach (var weight in residentWeights)
        {
            residentBytes = checked(residentBytes + checked(weight.Descriptor.ElementCount * sizeof(float)));
        }

        var kvElementsPerToken = checked(
            (long)configuration.LayerCount *
            checked(configuration.KeyValueLoraRank + configuration.QueryKeyRopeHeadSize));
        var kvBytes = checked(checked(kvElementsPerToken * contextSize) * sizeof(float));

        var attentionProjectionSize = checked(
            configuration.AttentionHeadCount *
            checked(configuration.QueryKeyNopeHeadSize + configuration.QueryKeyRopeHeadSize));
        var keyValueInputSize = checked(
            configuration.KeyValueLoraRank + configuration.QueryKeyRopeHeadSize);
        var keyValueProjectionSize = checked(
            configuration.AttentionHeadCount *
            checked(configuration.QueryKeyNopeHeadSize + configuration.ValueHeadSize));
        var sharedIntermediateSize = checked(
            configuration.SharedExpertCount * configuration.MoeIntermediateSize);
        var largestMlpIntermediate = Math.Max(
            configuration.FirstMoeLayer > 0 ? configuration.DenseIntermediateSize : 0,
            sharedIntermediateSize);
        var attentionContextSize = checked(
            configuration.AttentionHeadCount * configuration.ValueHeadSize);
        var attentionActivationCapacity = checked(
            configuration.QueryLoraRank +
            attentionProjectionSize +
            keyValueInputSize +
            keyValueProjectionSize +
            attentionContextSize +
            configuration.HiddenSize);
        // Dense SwiGLU and MLA retain several intermediates at the same time.
        var activationCapacity = Math.Max(
            configuration.HiddenSize,
            Math.Max(
                checked(largestMlpIntermediate * 3),
                attentionActivationCapacity));
        var quantizationCapacity = Math.Max(configuration.HiddenSize, largestMlpIntermediate);
        var outputCapacity = Math.Max(
            configuration.VocabularySize,
            Math.Max(
                configuration.HiddenSize,
                Math.Max(
                    configuration.RoutedExpertCount,
                    Math.Max(attentionProjectionSize, keyValueProjectionSize))));
        var attentionScoreCapacity = contextSize;
        var scratchBytes = checked(
            checked((long)activationCapacity * sizeof(float)) +
            quantizationCapacity +
            checked((long)outputCapacity * sizeof(float)) +
            checked((long)attentionScoreCapacity * sizeof(float)));
        var requiredBytes = checked(residentBytes + kvBytes + scratchBytes);

        return new ModelMemoryPlan(
            contextSize,
            residentBytes,
            kvBytes,
            scratchBytes,
            requiredBytes,
            availableBytes,
            activationCapacity,
            quantizationCapacity,
            outputCapacity,
            attentionScoreCapacity);
    }

    public static long GetAvailableMemoryBytes()
    {
        var totalAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalAvailable <= 0)
        {
            throw new InvalidOperationException("The managed runtime did not report an available memory limit.");
        }

        var currentUsage = Math.Max(Environment.WorkingSet, GC.GetTotalMemory(forceFullCollection: false));
        return Math.Max(0, totalAvailable - currentUsage);
    }

    public void EnsureFits()
    {
        if (RequiredBytes > AvailableBytes)
        {
            throw new ModelMemoryBudgetExceededException(this);
        }
    }
}

internal sealed class ModelMemoryBudgetExceededException : InvalidOperationException
{
    public ModelMemoryBudgetExceededException(ModelMemoryPlan plan)
        : base(
            $"Managed model loading requires {plan.RequiredBytes} bytes " +
            $"(resident {plan.ResidentBytes}, KV {plan.KvBytes}, scratch {plan.ScratchBytes}), " +
            $"but only {plan.AvailableBytes} bytes are available.")
    {
        Plan = plan;
    }

    public ModelMemoryPlan Plan { get; }
}
