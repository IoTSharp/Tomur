namespace Tomur.Providers.Glm;

internal sealed record ForcedTokenSpan(int StartPosition, IReadOnlyList<int> TokenIds);

internal sealed class ForcedSpanGrammar
{
    private readonly ForcedTokenSpan[] spans;

    public ForcedSpanGrammar(IEnumerable<ForcedTokenSpan> spans, int vocabularySize)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (vocabularySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vocabularySize));
        }

        this.spans = spans.OrderBy(static span => span.StartPosition).ToArray();
        var previousEnd = 0;
        foreach (var span in this.spans)
        {
            ArgumentNullException.ThrowIfNull(span);
            if (span.StartPosition < 0 || span.TokenIds.Count == 0)
            {
                throw new InvalidDataException("A forced grammar span requires a non-negative start and at least one token.");
            }

            if (span.StartPosition < previousEnd)
            {
                throw new InvalidDataException("Forced grammar spans cannot overlap.");
            }

            foreach (var tokenId in span.TokenIds)
            {
                if ((uint)tokenId >= (uint)vocabularySize)
                {
                    throw new InvalidDataException($"Forced grammar token is outside the vocabulary: {tokenId}.");
                }
            }

            previousEnd = checked(span.StartPosition + span.TokenIds.Count);
        }
    }

    public bool TryGetForcedToken(int generatedPosition, out int tokenId)
    {
        if (generatedPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generatedPosition));
        }

        foreach (var span in spans)
        {
            if (generatedPosition < span.StartPosition)
            {
                break;
            }

            var relative = generatedPosition - span.StartPosition;
            if ((uint)relative < (uint)span.TokenIds.Count)
            {
                tokenId = span.TokenIds[relative];
                return true;
            }
        }

        tokenId = default;
        return false;
    }

    public bool Apply(int generatedPosition, Span<float> logits)
    {
        if (!TryGetForcedToken(generatedPosition, out var tokenId))
        {
            return false;
        }

        if ((uint)tokenId >= (uint)logits.Length)
        {
            throw new InvalidDataException(
                $"Forced grammar token {tokenId} exceeds the current logit vocabulary {logits.Length}.");
        }

        var selected = logits[tokenId];
        if (!float.IsFinite(selected))
        {
            throw new InvalidDataException($"Forced grammar logit for token {tokenId} is not finite.");
        }

        logits.Fill(float.MinValue);
        logits[tokenId] = selected;
        return true;
    }
}
