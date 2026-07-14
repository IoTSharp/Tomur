using Tomur.Inference;
using Tomur.Providers.Glm;

namespace Tomur.Providers.Olmoe;

internal sealed record OlmoeGenerationResult(
    string Text,
    int PromptTokenCount,
    IReadOnlyList<int> GeneratedTokenIds,
    string StopReason,
    int Seed);

internal sealed class OlmoeTextGenerator(
    ManagedOlmoeModel model,
    ExpertCache expertCache)
{
    public async ValueTask<OlmoeGenerationResult> GenerateAsync(
        OlmoePrompt prompt,
        CompletionOptions options,
        CancellationToken cancellationToken = default,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        if (prompt.TokenIds.Count == 0)
        {
            throw new ArgumentException("Prompt must contain at least one token.", nameof(prompt));
        }

        if (options.MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max output tokens must be positive.");
        }

        var contextLimit = Math.Min(options.ContextSize, model.MemoryPlan.ContextSize);
        var requiredTokens = checked(prompt.TokenIds.Count + options.MaxOutputTokens);
        if (requiredTokens > contextLimit)
        {
            throw new ContextLengthExceededException(
                prompt.TokenIds.Count,
                options.MaxOutputTokens,
                contextLimit);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var forward = new OlmoeForwardContext(model, expertCache, contextLimit);
        using var sampler = new TokenSampler(model.Configuration.VocabularySize, options);
        var promptTokenIds = prompt.TokenIds.ToArray();
        var history = new List<int>(requiredTokens);
        history.AddRange(promptTokenIds);
        var generated = new List<int>(options.MaxOutputTokens);
        var decoder = new IncrementalTextDecoder(
            model.Tokenizer,
            prompt.StopTokenIds,
            options.StopSequences,
            onToken);

        var logits = await forward.ForwardAsync(promptTokenIds, cancellationToken).ConfigureAwait(false);
        var stopReason = "length";
        var nextToken = new int[1];
        for (var step = 0; step < options.MaxOutputTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenId = sampler.Sample(logits.Span, history);
            decoder.AppendToken(tokenId);
            if (decoder.StopTokenId.HasValue)
            {
                stopReason = "stop_token";
                break;
            }

            generated.Add(tokenId);
            history.Add(tokenId);
            if (decoder.Stopped)
            {
                stopReason = "stop_sequence";
                break;
            }

            if (step + 1 >= options.MaxOutputTokens)
            {
                break;
            }

            nextToken[0] = tokenId;
            logits = await forward.ForwardAsync(nextToken, cancellationToken).ConfigureAwait(false);
        }

        decoder.Complete();
        return new OlmoeGenerationResult(
            decoder.Text,
            promptTokenIds.Length,
            generated.ToArray(),
            stopReason,
            sampler.Seed);
    }
}
