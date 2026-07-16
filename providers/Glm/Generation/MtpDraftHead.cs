namespace Tomur.Providers.Glm;

internal static class MtpDraftHead
{
    public static int DraftSingleToken(
        ManagedGlmModel model,
        ReadOnlySpan<float> hiddenState,
        Span<float> draftLogits)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (hiddenState.Length != model.Configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"MTP hidden state must contain {model.Configuration.HiddenSize} elements.",
                nameof(hiddenState));
        }

        if (draftLogits.Length != model.Configuration.VocabularySize)
        {
            throw new ArgumentException(
                $"MTP draft logits must contain {model.Configuration.VocabularySize} elements.",
                nameof(draftLogits));
        }

        model.ProjectMtpLogits(hiddenState, draftLogits);
        var selected = 0;
        for (var tokenId = 0; tokenId < draftLogits.Length; tokenId++)
        {
            if (!float.IsFinite(draftLogits[tokenId]))
            {
                throw new InvalidDataException($"MTP draft logit at token {tokenId} is not finite.");
            }

            if (draftLogits[tokenId] > draftLogits[selected])
            {
                selected = tokenId;
            }
        }

        return selected;
    }
}
