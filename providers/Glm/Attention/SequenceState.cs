namespace Tomur.Providers.Glm;

internal sealed class SequenceState
{
    public SequenceState(int layer, int layerCount, int contextLimit)
    {
        if (layerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layerCount));
        }

        if ((uint)layer >= (uint)layerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }

        if (contextLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLimit));
        }

        Layer = layer;
        ContextLimit = contextLimit;
    }

    public int Layer { get; }

    public int Position { get; private set; }

    public int ValidTokenCount { get; private set; }

    public int ContextLimit { get; }

    public int CacheStart { get; private set; }

    public int RemainingTokenCount => ContextLimit - Position;

    internal SequenceCheckpoint Capture()
        => new(Position, ValidTokenCount, CacheStart);

    internal void EnsureCanAppend(int tokenCount = 1)
    {
        if (tokenCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount));
        }

        if (Position != checked(CacheStart + ValidTokenCount))
        {
            throw new InvalidOperationException(
                "Sequence position must equal cache start plus the valid token count.");
        }

        if (tokenCount > RemainingTokenCount)
        {
            throw new ContextLengthExceededException(Position, tokenCount, ContextLimit);
        }
    }

    internal void Advance()
    {
        EnsureCanAppend();
        Position = checked(Position + 1);
        ValidTokenCount = checked(ValidTokenCount + 1);
    }

    internal void Restore(SequenceCheckpoint checkpoint)
    {
        Position = checkpoint.Position;
        ValidTokenCount = checkpoint.ValidTokenCount;
        CacheStart = checkpoint.CacheStart;
    }

    internal void RestorePersistent(int position, int validTokenCount, int cacheStart)
    {
        if (position < 0 || validTokenCount < 0 || cacheStart < 0 ||
            position > ContextLimit || validTokenCount > ContextLimit ||
            position != checked(cacheStart + validTokenCount))
        {
            throw new InvalidDataException(
                "Persisted sequence state is outside the context limit or internally inconsistent.");
        }

        Restore(new SequenceCheckpoint(position, validTokenCount, cacheStart));
    }
}

internal readonly record struct SequenceCheckpoint(
    int Position,
    int ValidTokenCount,
    int CacheStart);

internal sealed class ContextLengthExceededException : InvalidOperationException
{
    public ContextLengthExceededException(int position, int requestedTokenCount, int contextLimit)
        : base(
            $"Sequence position {position} cannot append {requestedTokenCount} token(s) " +
            $"within the context limit of {contextLimit}.")
    {
        Position = position;
        RequestedTokenCount = requestedTokenCount;
        ContextLimit = contextLimit;
    }

    public int Position { get; }

    public int RequestedTokenCount { get; }

    public int ContextLimit { get; }
}
