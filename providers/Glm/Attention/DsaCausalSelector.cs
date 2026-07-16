using System.Buffers;

namespace Tomur.Providers.Glm;

internal static class DsaCausalSelector
{
    public static int SelectTopK(
        ReadOnlySpan<float> indexScores,
        int topK,
        Span<int> selectedIndices)
    {
        if (indexScores.IsEmpty)
        {
            throw new ArgumentException("At least one causal key score is required.", nameof(indexScores));
        }

        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        var selectedCount = Math.Min(topK, indexScores.Length);
        if (selectedIndices.Length < selectedCount)
        {
            throw new ArgumentException(
                $"Selected-index storage must contain at least {selectedCount} elements.",
                nameof(selectedIndices));
        }

        for (var index = 0; index < indexScores.Length; index++)
        {
            if (!float.IsFinite(indexScores[index]))
            {
                throw new InvalidDataException($"DSA index score at position {index} is not finite.");
            }
        }

        if (selectedCount == indexScores.Length)
        {
            for (var index = 0; index < selectedCount; index++)
            {
                selectedIndices[index] = index;
            }

            return selectedCount;
        }

        var rentedScores = ArrayPool<float>.Shared.Rent(selectedCount);
        try
        {
            var selectedScores = rentedScores.AsSpan(0, selectedCount);
            for (var keyIndex = 0; keyIndex < selectedCount; keyIndex++)
            {
                selectedIndices[keyIndex] = keyIndex;
                selectedScores[keyIndex] = indexScores[keyIndex];
            }

            for (var parent = (selectedCount / 2) - 1; parent >= 0; parent--)
            {
                SiftWorstDown(selectedScores, selectedIndices, parent, selectedCount);
            }

            for (var keyIndex = selectedCount; keyIndex < indexScores.Length; keyIndex++)
            {
                var score = indexScores[keyIndex];
                if (!IsWorse(selectedScores[0], selectedIndices[0], score, keyIndex))
                {
                    continue;
                }

                selectedScores[0] = score;
                selectedIndices[0] = keyIndex;
                SiftWorstDown(selectedScores, selectedIndices, 0, selectedCount);
            }

            selectedIndices[..selectedCount].Sort();
            return selectedCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedScores, clearArray: true);
        }
    }

    public static void SoftmaxSelectedInPlace(Span<float> attentionScores, int topK)
    {
        if (attentionScores.IsEmpty)
        {
            throw new ArgumentException("At least one attention score is required.", nameof(attentionScores));
        }

        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        if (topK >= attentionScores.Length)
        {
            ScalarKernels.SoftmaxInPlace(attentionScores);
            return;
        }

        var indices = ArrayPool<int>.Shared.Rent(topK);
        try
        {
            var selected = indices.AsSpan(0, topK);
            SelectTopK(attentionScores, topK, selected);
            var maximum = float.NegativeInfinity;
            foreach (var index in selected)
            {
                maximum = Math.Max(maximum, attentionScores[index]);
            }

            double total = 0;
            foreach (var index in selected)
            {
                var probability = Math.Exp(attentionScores[index] - maximum);
                attentionScores[index] = (float)probability;
                total += probability;
            }

            if (!double.IsFinite(total) || total <= 0)
            {
                throw new InvalidDataException("DSA attention probabilities could not be normalized.");
            }

            var selectedOffset = 0;
            for (var index = 0; index < attentionScores.Length; index++)
            {
                if (selectedOffset < selected.Length && selected[selectedOffset] == index)
                {
                    attentionScores[index] = (float)(attentionScores[index] / total);
                    selectedOffset++;
                }
                else
                {
                    attentionScores[index] = 0;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices, clearArray: true);
        }
    }

    private static void SiftWorstDown(
        Span<float> scores,
        Span<int> indices,
        int parent,
        int count)
    {
        while (true)
        {
            var left = checked((parent * 2) + 1);
            if (left >= count)
            {
                return;
            }

            var right = left + 1;
            var worse = right < count &&
                IsWorse(scores[right], indices[right], scores[left], indices[left])
                    ? right
                    : left;
            if (!IsWorse(scores[worse], indices[worse], scores[parent], indices[parent]))
            {
                return;
            }

            var score = scores[parent];
            scores[parent] = scores[worse];
            scores[worse] = score;
            var index = indices[parent];
            indices[parent] = indices[worse];
            indices[worse] = index;
            parent = worse;
        }
    }

    private static bool IsWorse(
        float leftScore,
        int leftIndex,
        float rightScore,
        int rightIndex)
        => leftScore < rightScore ||
            (leftScore == rightScore && leftIndex > rightIndex);
}
