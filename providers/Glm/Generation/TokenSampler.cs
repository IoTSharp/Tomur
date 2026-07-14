using System.Buffers;
using Tomur.Inference;

namespace Tomur.Providers.Glm;

internal sealed class TokenSampler : IDisposable
{
    private readonly int vocabularySize;
    private readonly float temperature;
    private readonly float topP;
    private readonly int topK;
    private readonly int penaltyLastTokens;
    private readonly float repeatPenalty;
    private readonly float frequencyPenalty;
    private readonly float presencePenalty;
    private readonly int seed;
    private readonly ScoreComparer comparer;
    private float[]? scores;
    private int[]? indices;
    private ulong randomState;

    public TokenSampler(int vocabularySize, CompletionOptions options)
    {
        if (vocabularySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vocabularySize));
        }

        ArgumentNullException.ThrowIfNull(options);
        ValidateFinite(options.Temperature, nameof(options.Temperature));
        ValidateFinite(options.TopP, nameof(options.TopP));
        ValidateFinite(options.RepeatPenalty, nameof(options.RepeatPenalty));
        ValidateFinite(options.FrequencyPenalty, nameof(options.FrequencyPenalty));
        ValidateFinite(options.PresencePenalty, nameof(options.PresencePenalty));
        if (options.RepeatPenalty <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Repeat penalty must be finite and greater than zero.");
        }

        this.vocabularySize = vocabularySize;
        temperature = options.Temperature <= 0
            ? options.Temperature
            : Math.Max(0.01f, options.Temperature);
        topP = Math.Clamp(options.TopP, 0.01f, 1.0f);
        topK = Math.Clamp(options.TopK, 1, vocabularySize);
        penaltyLastTokens = options.PenaltyLastTokens < -1 ? -1 : options.PenaltyLastTokens;
        repeatPenalty = options.RepeatPenalty;
        frequencyPenalty = options.FrequencyPenalty;
        presencePenalty = options.PresencePenalty;
        seed = options.Seed < 0 ? Random.Shared.Next(1, int.MaxValue) : options.Seed;
        randomState = unchecked((uint)seed) + 0x9e3779b97f4a7c15UL;

        float[]? rentedScores = null;
        int[]? rentedIndices = null;
        try
        {
            rentedScores = ArrayPool<float>.Shared.Rent(vocabularySize);
            rentedIndices = ArrayPool<int>.Shared.Rent(vocabularySize);
            scores = rentedScores;
            indices = rentedIndices;
            comparer = new ScoreComparer(this);
        }
        catch
        {
            Return(rentedScores);
            Return(rentedIndices);
            throw;
        }
    }

    public int Seed => seed;

    public int Sample(ReadOnlySpan<float> logits, IReadOnlyList<int> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (logits.Length != vocabularySize)
        {
            throw new ArgumentException(
                $"Sampler logits must contain {vocabularySize} values.",
                nameof(logits));
        }

        var scoreBuffer = GetScores().AsSpan(0, vocabularySize);
        var indexBuffer = GetIndices();
        logits.CopyTo(scoreBuffer);
        EnsureFinite(scoreBuffer);
        ApplyPenalties(scoreBuffer, indexBuffer, history);
        EnsureFinite(scoreBuffer);

        if (temperature <= 0)
        {
            return ArgMax(scoreBuffer);
        }

        for (var index = 0; index < vocabularySize; index++)
        {
            indexBuffer[index] = index;
        }

        Array.Sort(indexBuffer, 0, vocabularySize, comparer);
        var candidateCount = topK;
        var maximum = scoreBuffer[indexBuffer[0]] / temperature;
        double total = 0;
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var tokenId = indexBuffer[candidate];
            var weight = Math.Exp((scoreBuffer[tokenId] / temperature) - maximum);
            scoreBuffer[tokenId] = (float)weight;
            total += weight;
        }

        if (!double.IsFinite(total) || total <= 0)
        {
            throw new InvalidDataException("Sampling probabilities could not be normalized.");
        }

        var selectedCount = candidateCount;
        var selectedMass = total;
        if (topP < 1.0f)
        {
            double cumulative = 0;
            for (var candidate = 0; candidate < candidateCount; candidate++)
            {
                cumulative += scoreBuffer[indexBuffer[candidate]];
                if ((cumulative / total) >= topP)
                {
                    selectedCount = candidate + 1;
                    selectedMass = cumulative;
                    break;
                }
            }
        }

        var target = NextUnitDouble() * selectedMass;
        double observed = 0;
        for (var candidate = 0; candidate < selectedCount; candidate++)
        {
            var tokenId = indexBuffer[candidate];
            observed += scoreBuffer[tokenId];
            if (target < observed || candidate == selectedCount - 1)
            {
                return tokenId;
            }
        }

        throw new InvalidOperationException("Sampler did not select a token.");
    }

    public void Dispose()
    {
        Return(Interlocked.Exchange(ref scores, null));
        Return(Interlocked.Exchange(ref indices, null));
    }

    private void ApplyPenalties(
        Span<float> values,
        int[] frequencies,
        IReadOnlyList<int> history)
    {
        if (penaltyLastTokens == 0 || history.Count == 0 ||
            (Math.Abs(repeatPenalty - 1.0f) <= 0.0001f &&
             Math.Abs(frequencyPenalty) <= 0.0001f &&
             Math.Abs(presencePenalty) <= 0.0001f))
        {
            return;
        }

        var count = penaltyLastTokens < 0
            ? history.Count
            : Math.Min(penaltyLastTokens, history.Count);
        var start = history.Count - count;
        Array.Clear(frequencies, 0, vocabularySize);
        for (var index = start; index < history.Count; index++)
        {
            var tokenId = history[index];
            if ((uint)tokenId >= (uint)vocabularySize)
            {
                throw new InvalidDataException($"Sampler history token is outside the vocabulary: {tokenId}.");
            }

            frequencies[tokenId] = checked(frequencies[tokenId] + 1);
        }

        for (var tokenId = 0; tokenId < vocabularySize; tokenId++)
        {
            var frequency = frequencies[tokenId];
            if (frequency == 0)
            {
                continue;
            }

            var value = values[tokenId];
            if (Math.Abs(repeatPenalty - 1.0f) > 0.0001f)
            {
                value = value <= 0 ? value * repeatPenalty : value / repeatPenalty;
            }

            value -= frequency * frequencyPenalty;
            value -= presencePenalty;
            values[tokenId] = value;
        }
    }

    private double NextUnitDouble()
    {
        randomState += 0x9e3779b97f4a7c15UL;
        var value = randomState;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        value ^= value >> 31;
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var selected = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] > values[selected])
            {
                selected = index;
            }
        }

        return selected;
    }

    private static void EnsureFinite(ReadOnlySpan<float> values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidDataException($"Logit at index {index} is not finite.");
            }
        }
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private float[] GetScores()
    {
        ObjectDisposedException.ThrowIf(scores is null, this);
        return scores;
    }

    private int[] GetIndices()
    {
        ObjectDisposedException.ThrowIf(indices is null, this);
        return indices;
    }

    private static void Return<T>(T[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<T>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class ScoreComparer(TokenSampler owner) : IComparer<int>
    {
        public int Compare(int left, int right)
        {
            var scores = owner.GetScores();
            var order = scores[right].CompareTo(scores[left]);
            return order != 0 ? order : left.CompareTo(right);
        }
    }
}
