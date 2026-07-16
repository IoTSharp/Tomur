namespace Tomur.Providers.Glm;

internal static class MlaAttention
{
    public static void RunToken(
        ManagedGlmModel model,
        int layer,
        ReadOnlySpan<float> input,
        KvCache cache,
        SequenceState sequence,
        AttentionWorkspace workspace,
        Span<float> destination,
        CancellationToken cancellationToken,
        AttentionTrace? trace = null,
        MlaAttentionMode mode = MlaAttentionMode.Absorbed)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(workspace);
        trace?.Clear();
        if (mode is not (MlaAttentionMode.Reference or MlaAttentionMode.Absorbed))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (sequence.Layer != layer)
        {
            throw new ArgumentException(
                $"Sequence state belongs to layer {sequence.Layer}, not layer {layer}.",
                nameof(sequence));
        }

        var configuration = model.Configuration;
        if (input.Length != configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"Attention input must contain {configuration.HiddenSize} elements.",
                nameof(input));
        }

        if (destination.Length != configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"Attention destination must contain {configuration.HiddenSize} elements.",
                nameof(destination));
        }

        if (input.Overlaps(destination))
        {
            throw new ArgumentException("Attention input and destination cannot overlap.", nameof(destination));
        }

        cache.EnsureCompatible(configuration, sequence.ContextLimit);
        workspace.EnsureCompatible(configuration);
        sequence.EnsureCanAppend();
        var originalCacheLength = cache.GetValidTokenCount(layer);
        if (originalCacheLength != sequence.ValidTokenCount)
        {
            throw new InvalidOperationException(
                $"Layer {layer} KV cache contains {originalCacheLength} valid tokens, " +
                $"but the sequence state contains {sequence.ValidTokenCount}.");
        }

        var sequenceCheckpoint = sequence.Capture();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queryHeadSize = checked(
                configuration.QueryKeyNopeHeadSize + configuration.QueryKeyRopeHeadSize);
            var queryProjectionSize = checked(configuration.AttentionHeadCount * queryHeadSize);
            var keyValueInputSize = checked(
                configuration.KeyValueLoraRank + configuration.QueryKeyRopeHeadSize);
            var keyValueHeadSize = checked(
                configuration.QueryKeyNopeHeadSize + configuration.ValueHeadSize);
            var keyValueProjectionSize = checked(
                configuration.AttentionHeadCount * keyValueHeadSize);
            var contextSize = checked(
                configuration.AttentionHeadCount * configuration.ValueHeadSize);
            var firstTemporarySize = Math.Max(configuration.QueryLoraRank, keyValueInputSize);
            var secondTemporarySize = Math.Max(
                configuration.QueryLoraRank,
                configuration.KeyValueLoraRank);
            var activationLength = ModelMemoryPlan.GetAttentionActivationCapacity(configuration);
            var activations = workspace.GetActivations(activationLength);
            var offset = 0;
            var firstTemporary = activations.Slice(offset, firstTemporarySize);
            offset = checked(offset + firstTemporarySize);
            var secondTemporary = activations.Slice(offset, secondTemporarySize);
            offset = checked(offset + secondTemporarySize);
            var queryHeads = activations.Slice(offset, queryProjectionSize);
            offset = checked(offset + queryProjectionSize);
            var keyValueExpansion = activations.Slice(offset, keyValueProjectionSize);
            offset = checked(offset + keyValueProjectionSize);
            var context = activations.Slice(offset, contextSize);
            offset = checked(offset + contextSize);
            var projectedOutput = activations.Slice(offset, configuration.HiddenSize);

            var prefix = $"model.layers.{layer}.self_attn.";
            var queryLatent = firstTemporary[..configuration.QueryLoraRank];
            model.MultiplyResidentWeight($"{prefix}q_a_proj.weight", input, queryLatent);
            var queryLatentTrace = trace is null ? null : queryLatent.ToArray();

            var normalizedQueryLatent = secondTemporary[..configuration.QueryLoraRank];
            ScalarKernels.RmsNorm(
                queryLatent,
                model.GetResidentWeight($"{prefix}q_a_layernorm.weight"),
                configuration.RmsNormEpsilon,
                normalizedQueryLatent);
            var normalizedQueryLatentTrace = trace is null
                ? null
                : normalizedQueryLatent.ToArray();
            model.MultiplyResidentWeight(
                $"{prefix}q_b_proj.weight",
                normalizedQueryLatent,
                queryHeads);
            for (var head = 0; head < configuration.AttentionHeadCount; head++)
            {
                RotaryEmbedding.ApplyInterleaved(
                    queryHeads.Slice(
                        checked((head * queryHeadSize) + configuration.QueryKeyNopeHeadSize),
                        configuration.QueryKeyRopeHeadSize),
                    sequence.Position,
                    configuration.RopeTheta);
            }

            var queryTrace = trace is null ? null : queryHeads.ToArray();
            cancellationToken.ThrowIfCancellationRequested();

            var keyValueInput = firstTemporary[..keyValueInputSize];
            model.MultiplyResidentWeight(
                $"{prefix}kv_a_proj_with_mqa.weight",
                input,
                keyValueInput);
            var keyValueLatent = keyValueInput[..configuration.KeyValueLoraRank];
            var keyValueLatentTrace = trace is null ? null : keyValueLatent.ToArray();
            var normalizedKeyValueLatent = secondTemporary[..configuration.KeyValueLoraRank];
            ScalarKernels.RmsNorm(
                keyValueLatent,
                model.GetResidentWeight($"{prefix}kv_a_layernorm.weight"),
                configuration.RmsNormEpsilon,
                normalizedKeyValueLatent);
            var normalizedKeyValueLatentTrace = trace is null
                ? null
                : normalizedKeyValueLatent.ToArray();
            var ropeKey = keyValueInput.Slice(
                configuration.KeyValueLoraRank,
                configuration.QueryKeyRopeHeadSize);
            RotaryEmbedding.ApplyInterleaved(
                ropeKey,
                sequence.Position,
                configuration.RopeTheta);

            cancellationToken.ThrowIfCancellationRequested();
            cache.Append(layer, normalizedKeyValueLatent, ropeKey);
            cancellationToken.ThrowIfCancellationRequested();

            var validTokenCount = cache.GetValidTokenCount(layer);
            var capturedScores = trace is null
                ? null
                : new float[checked(configuration.AttentionHeadCount * validTokenCount)];
            var capturedProbabilities = trace is null
                ? null
                : new float[checked(configuration.AttentionHeadCount * validTokenCount)];
            context.Clear();
            var inverseScale = 1.0 / Math.Sqrt(queryHeadSize);
            var keyValueProjectionName = $"{prefix}kv_b_proj.weight";
            for (var head = 0; head < configuration.AttentionHeadCount; head++)
            {
                var scores = workspace.GetScores(validTokenCount);
                var queryOffset = checked(head * queryHeadSize);
                var keyValueOffset = checked(head * keyValueHeadSize);
                var compressedQuery = secondTemporary[..configuration.KeyValueLoraRank];
                if (mode == MlaAttentionMode.Absorbed)
                {
                    for (var latent = 0; latent < configuration.KeyValueLoraRank; latent++)
                    {
                        double sum = 0;
                        for (var component = 0; component < configuration.QueryKeyNopeHeadSize; component++)
                        {
                            sum +=
                                (double)queryHeads[queryOffset + component] *
                                model.GetResidentWeightValue(
                                    keyValueProjectionName,
                                    keyValueOffset + component,
                                    latent);
                        }

                        compressedQuery[latent] = (float)sum;
                    }
                }

                for (var tokenIndex = 0; tokenIndex < validTokenCount; tokenIndex++)
                {
                    if ((tokenIndex & 63) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    double score = 0;
                    var cachedCompressed = cache.GetCompressed(layer, tokenIndex);
                    if (mode == MlaAttentionMode.Reference)
                    {
                        model.MultiplyResidentWeight(
                            keyValueProjectionName,
                            cachedCompressed,
                            keyValueExpansion);
                        for (var component = 0; component < configuration.QueryKeyNopeHeadSize; component++)
                        {
                            score +=
                                (double)queryHeads[queryOffset + component] *
                                keyValueExpansion[keyValueOffset + component];
                        }
                    }
                    else
                    {
                        for (var latent = 0; latent < configuration.KeyValueLoraRank; latent++)
                        {
                            score += (double)compressedQuery[latent] * cachedCompressed[latent];
                        }
                    }

                    var cachedRopeKey = cache.GetRopeKey(layer, tokenIndex);
                    for (var component = 0; component < configuration.QueryKeyRopeHeadSize; component++)
                    {
                        score +=
                            (double)queryHeads[
                                queryOffset + configuration.QueryKeyNopeHeadSize + component] *
                            cachedRopeKey[component];
                    }

                    var scaledScore = (float)(score * inverseScale);
                    if (!float.IsFinite(scaledScore))
                    {
                        throw new InvalidDataException(
                            $"Attention score for layer {layer}, head {head}, token {tokenIndex} is not finite.");
                    }

                    scores[tokenIndex] = scaledScore;
                }

                if (capturedScores is not null)
                {
                    scores.CopyTo(capturedScores.AsSpan(
                        head * validTokenCount,
                        validTokenCount));
                }

                if (configuration.HasDsaConfiguration && layer >= configuration.DsaStartLayer)
                {
                    if (configuration.DsaTopK < validTokenCount)
                    {
                        throw new InvalidDataException(
                            $"DSA layer {layer} requires validated indexer scores before selecting " +
                            $"{configuration.DsaTopK} of {validTokenCount} causal keys. " +
                            "Only the dense-equivalent DSA path is enabled before M14 indexer validation.");
                    }

                    DsaCausalSelector.SoftmaxSelectedInPlace(scores, configuration.DsaTopK);
                }
                else
                {
                    ScalarKernels.SoftmaxInPlace(scores);
                }
                if (capturedProbabilities is not null)
                {
                    scores.CopyTo(capturedProbabilities.AsSpan(
                        head * validTokenCount,
                        validTokenCount));
                }

                var headContext = context.Slice(
                    checked(head * configuration.ValueHeadSize),
                    configuration.ValueHeadSize);
                if (mode == MlaAttentionMode.Reference)
                {
                    for (var component = 0; component < configuration.ValueHeadSize; component++)
                    {
                        double sum = 0;
                        for (var tokenIndex = 0; tokenIndex < validTokenCount; tokenIndex++)
                        {
                            if ((tokenIndex & 63) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            model.MultiplyResidentWeight(
                                keyValueProjectionName,
                                cache.GetCompressed(layer, tokenIndex),
                                keyValueExpansion);
                            sum +=
                                (double)scores[tokenIndex] *
                                keyValueExpansion[
                                    keyValueOffset + configuration.QueryKeyNopeHeadSize + component];
                        }

                        headContext[component] = (float)sum;
                    }
                }
                else
                {
                    var compressedContext = secondTemporary[..configuration.KeyValueLoraRank];
                    for (var latent = 0; latent < configuration.KeyValueLoraRank; latent++)
                    {
                        double sum = 0;
                        for (var tokenIndex = 0; tokenIndex < validTokenCount; tokenIndex++)
                        {
                            if ((tokenIndex & 63) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            sum +=
                                (double)scores[tokenIndex] *
                                cache.GetCompressed(layer, tokenIndex)[latent];
                        }

                        compressedContext[latent] = (float)sum;
                    }

                    for (var component = 0; component < configuration.ValueHeadSize; component++)
                    {
                        double sum = 0;
                        for (var latent = 0; latent < configuration.KeyValueLoraRank; latent++)
                        {
                            sum +=
                                (double)model.GetResidentWeightValue(
                                    keyValueProjectionName,
                                    keyValueOffset + configuration.QueryKeyNopeHeadSize + component,
                                    latent) *
                                compressedContext[latent];
                        }

                        headContext[component] = (float)sum;
                    }
                }
            }

            float[]? capturedKey = null;
            float[]? capturedValue = null;
            if (trace is not null)
            {
                model.MultiplyResidentWeight(
                    keyValueProjectionName,
                    cache.GetCompressed(layer, validTokenCount - 1),
                    keyValueExpansion);
                capturedKey = new float[queryProjectionSize];
                capturedValue = new float[contextSize];
                var currentRopeKey = cache.GetRopeKey(layer, validTokenCount - 1);
                for (var head = 0; head < configuration.AttentionHeadCount; head++)
                {
                    var queryOffset = checked(head * queryHeadSize);
                    var keyValueOffset = checked(head * keyValueHeadSize);
                    keyValueExpansion
                        .Slice(keyValueOffset, configuration.QueryKeyNopeHeadSize)
                        .CopyTo(capturedKey.AsSpan(queryOffset, configuration.QueryKeyNopeHeadSize));
                    currentRopeKey.CopyTo(capturedKey.AsSpan(
                        queryOffset + configuration.QueryKeyNopeHeadSize,
                        configuration.QueryKeyRopeHeadSize));
                    keyValueExpansion
                        .Slice(
                            keyValueOffset + configuration.QueryKeyNopeHeadSize,
                            configuration.ValueHeadSize)
                        .CopyTo(capturedValue.AsSpan(
                            head * configuration.ValueHeadSize,
                            configuration.ValueHeadSize));
                }
            }

            model.MultiplyResidentWeight($"{prefix}o_proj.weight", context, projectedOutput);
            EnsureFinite(projectedOutput, layer);
            var capturedOutput = trace is null ? null : projectedOutput.ToArray();
            cancellationToken.ThrowIfCancellationRequested();

            sequence.Advance();
            projectedOutput.CopyTo(destination);
            if (trace is not null)
            {
                trace.Set(
                    queryLatentTrace!,
                    normalizedQueryLatentTrace!,
                    keyValueLatentTrace!,
                    normalizedKeyValueLatentTrace!,
                    queryTrace!,
                    capturedKey!,
                    capturedValue!,
                    capturedScores!,
                    capturedProbabilities!,
                    capturedOutput!);
            }
        }
        catch
        {
            cache.RollbackLayer(layer, originalCacheLength);
            sequence.Restore(sequenceCheckpoint);
            trace?.Clear();
            throw;
        }
    }

    public static void RunPrefill(
        ManagedGlmModel model,
        int layer,
        ReadOnlySpan<float> inputs,
        int tokenCount,
        KvCache cache,
        SequenceState sequence,
        AttentionWorkspace workspace,
        Span<float> destinations,
        CancellationToken cancellationToken,
        AttentionTrace? lastTokenTrace = null,
        MlaAttentionMode mode = MlaAttentionMode.Absorbed)
    {
        ArgumentNullException.ThrowIfNull(model);
        lastTokenTrace?.Clear();
        if (tokenCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount));
        }

        var requiredLength = checked(tokenCount * model.Configuration.HiddenSize);
        if (inputs.Length != requiredLength)
        {
            throw new ArgumentException(
                $"Prefill inputs must contain exactly {requiredLength} elements.",
                nameof(inputs));
        }

        if (destinations.Length != requiredLength)
        {
            throw new ArgumentException(
                $"Prefill destinations must contain exactly {requiredLength} elements.",
                nameof(destinations));
        }

        if (inputs.Overlaps(destinations))
        {
            throw new ArgumentException("Prefill inputs and destinations cannot overlap.", nameof(destinations));
        }

        sequence.EnsureCanAppend(tokenCount);
        var sequenceCheckpoint = sequence.Capture();
        var cacheCheckpoint = cache.GetValidTokenCount(layer);
        try
        {
            for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = checked(tokenIndex * model.Configuration.HiddenSize);
                RunToken(
                    model,
                    layer,
                    inputs.Slice(offset, model.Configuration.HiddenSize),
                    cache,
                    sequence,
                    workspace,
                    destinations.Slice(offset, model.Configuration.HiddenSize),
                    cancellationToken,
                    lastTokenTrace,
                    mode);
            }
        }
        catch
        {
            cache.RollbackLayer(layer, cacheCheckpoint);
            sequence.Restore(sequenceCheckpoint);
            lastTokenTrace?.Clear();
            throw;
        }
    }

    private static void EnsureFinite(ReadOnlySpan<float> values, int layer)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException(
                    $"Attention output for layer {layer} at index {index} is not finite.");
            }
        }
    }

}

internal enum MlaAttentionMode
{
    Reference,
    Absorbed
}
