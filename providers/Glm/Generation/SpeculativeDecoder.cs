namespace Tomur.Providers.Glm;

internal readonly record struct SpeculativeVerificationResult(
    int TokenId,
    bool DraftAccepted,
    float AcceptanceProbability);

internal static class SpeculativeDecoder
{
    public static SpeculativeVerificationResult VerifySingleDraft(
        int draftTokenId,
        ReadOnlySpan<float> targetProbabilities,
        ReadOnlySpan<float> draftProbabilities,
        double acceptanceSample,
        double rejectionSample)
    {
        ValidateDistribution(targetProbabilities, nameof(targetProbabilities));
        ValidateDistribution(draftProbabilities, nameof(draftProbabilities));
        if (targetProbabilities.Length != draftProbabilities.Length)
        {
            throw new ArgumentException("Target and draft distributions must have the same vocabulary size.");
        }

        if ((uint)draftTokenId >= (uint)targetProbabilities.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(draftTokenId));
        }

        ValidateUnitSample(acceptanceSample, nameof(acceptanceSample));
        ValidateUnitSample(rejectionSample, nameof(rejectionSample));
        var target = targetProbabilities[draftTokenId];
        var draft = draftProbabilities[draftTokenId];
        var acceptance = draft <= 0 ? 1.0f : Math.Min(1.0f, target / draft);
        if (acceptanceSample < acceptance)
        {
            return new SpeculativeVerificationResult(draftTokenId, true, acceptance);
        }

        double residualTotal = 0;
        for (var tokenId = 0; tokenId < targetProbabilities.Length; tokenId++)
        {
            residualTotal += Math.Max(
                0,
                (double)targetProbabilities[tokenId] - draftProbabilities[tokenId]);
        }

        if (residualTotal <= 0 || !double.IsFinite(residualTotal))
        {
            return new SpeculativeVerificationResult(
                Sample(targetProbabilities, rejectionSample),
                false,
                acceptance);
        }

        var threshold = rejectionSample * residualTotal;
        double cumulative = 0;
        for (var tokenId = 0; tokenId < targetProbabilities.Length; tokenId++)
        {
            cumulative += Math.Max(
                0,
                (double)targetProbabilities[tokenId] - draftProbabilities[tokenId]);
            if (threshold < cumulative || tokenId == targetProbabilities.Length - 1)
            {
                return new SpeculativeVerificationResult(tokenId, false, acceptance);
            }
        }

        throw new InvalidOperationException("Speculative rejection sampling did not select a token.");
    }

    public static void Softmax(
        ReadOnlySpan<float> logits,
        float temperature,
        Span<float> probabilities)
    {
        if (logits.IsEmpty || probabilities.Length != logits.Length)
        {
            throw new ArgumentException("Logits and probabilities must have the same non-zero length.");
        }

        if (!float.IsFinite(temperature) || temperature <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature));
        }

        var maximum = float.NegativeInfinity;
        for (var index = 0; index < logits.Length; index++)
        {
            if (!float.IsFinite(logits[index]))
            {
                throw new InvalidDataException($"Speculative logit at index {index} is not finite.");
            }

            maximum = Math.Max(maximum, logits[index]);
        }

        double total = 0;
        for (var index = 0; index < logits.Length; index++)
        {
            var value = Math.Exp((logits[index] - maximum) / temperature);
            probabilities[index] = (float)value;
            total += value;
        }

        if (!double.IsFinite(total) || total <= 0)
        {
            throw new InvalidDataException("Speculative probabilities could not be normalized.");
        }

        for (var index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] = (float)(probabilities[index] / total);
        }
    }

    private static int Sample(ReadOnlySpan<float> probabilities, double sample)
    {
        double cumulative = 0;
        for (var tokenId = 0; tokenId < probabilities.Length; tokenId++)
        {
            cumulative += probabilities[tokenId];
            if (sample < cumulative || tokenId == probabilities.Length - 1)
            {
                return tokenId;
            }
        }

        throw new InvalidOperationException("Probability sampling did not select a token.");
    }

    private static void ValidateDistribution(ReadOnlySpan<float> values, string parameterName)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Probability distribution cannot be empty.", parameterName);
        }

        double total = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]) || values[index] < 0)
            {
                throw new InvalidDataException(
                    $"Probability at index {index} in {parameterName} must be finite and non-negative.");
            }

            total += values[index];
        }

        if (Math.Abs(total - 1.0) > 1e-4)
        {
            throw new InvalidDataException(
                $"Probability distribution {parameterName} must sum to 1; observed {total:R}.");
        }
    }

    private static void ValidateUnitSample(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value >= 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Random sample must be in [0, 1).");
        }
    }
}
