using Tomur.Inference;

namespace Tomur.Providers.Glm;

internal sealed record GlmPrompt(
    IReadOnlyList<int> TokenIds,
    IReadOnlyList<int> StopTokenIds);

internal sealed class GlmPromptTemplate
{
    private static readonly string[] MaskTokens = ["[gMASK]", "<|gmask|>"];
    private static readonly string[] StartTokens = ["<sop>", "<|startofprompt|>"];
    private static readonly string[] SystemTokens = ["<|system|>"];
    private static readonly string[] UserTokens = ["<|user|>"];
    private static readonly string[] AssistantTokens = ["<|assistant|>"];
    private static readonly string[] ObservationTokens = ["<|observation|>", "<|tool|>"];
    private const string ThinkStartToken = "<think>";
    private const string ThinkEndToken = "</think>";
    private readonly ManagedTokenizer tokenizer;

    public GlmPromptTemplate(ManagedTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        this.tokenizer = tokenizer;
    }

    public GlmPrompt BuildCompletion(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var tokens = new List<int>();
        AppendPrefix(tokens);
        tokens.AddRange(tokenizer.Encode(prompt));
        return new GlmPrompt(tokens, ResolveStopTokenIds());
    }

    public GlmPrompt BuildChat(IReadOnlyList<ChatTurn> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var normalized = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new ChatTurn(
                NormalizeRole(message.Role),
                NormalizeContent(message.Content)))
            .ToArray();
        if (normalized.Length == 0)
        {
            return new GlmPrompt([], ResolveStopTokenIds());
        }

        var tokens = new List<int>();
        AppendPrefix(tokens);
        foreach (var message in normalized)
        {
            tokens.Add(RequireRoleToken(message.Role));
            tokens.AddRange(tokenizer.Encode(message.Content));
        }

        if (!string.Equals(normalized[^1].Role, "assistant", StringComparison.Ordinal))
        {
            tokens.Add(RequireRoleToken("assistant"));
            if (tokenizer.TryGetTokenId(ThinkStartToken, out var thinkStart) &&
                tokenizer.TryGetTokenId(ThinkEndToken, out var thinkEnd))
            {
                tokens.Add(thinkStart);
                tokens.Add(thinkEnd);
            }
        }

        return new GlmPrompt(tokens, ResolveStopTokenIds());
    }

    public IReadOnlyList<int> ResolveStopTokenIds()
    {
        var stopTokens = new List<int>(tokenizer.EosTokenIds);
        AddExisting(stopTokens, SystemTokens);
        AddExisting(stopTokens, UserTokens);
        AddExisting(stopTokens, AssistantTokens);
        AddExisting(stopTokens, ObservationTokens);
        return stopTokens.Distinct().ToArray();
    }

    private void AppendPrefix(List<int> tokens)
    {
        var maskToken = FindTokenId(MaskTokens);
        var startToken = FindTokenId(StartTokens);
        if (maskToken.HasValue || startToken.HasValue)
        {
            if (!maskToken.HasValue || !startToken.HasValue)
            {
                throw new InvalidDataException(
                    "GLM tokenizer must define both the generation mask and start-of-prompt token.");
            }

            tokens.Add(maskToken.Value);
            tokens.Add(startToken.Value);
            return;
        }

        if (tokenizer.BosTokenId.HasValue)
        {
            tokens.Add(tokenizer.BosTokenId.Value);
        }
    }

    private int RequireRoleToken(string role)
    {
        var candidates = role switch
        {
            "system" => SystemTokens,
            "assistant" => AssistantTokens,
            "observation" => ObservationTokens,
            _ => UserTokens
        };
        return FindTokenId(candidates) ?? throw new InvalidDataException(
            $"GLM tokenizer does not define the required '{role}' role token.");
    }

    private int? FindTokenId(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (tokenizer.TryGetTokenId(candidate, out var tokenId))
            {
                return tokenId;
            }
        }

        return null;
    }

    private void AddExisting(List<int> destination, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (tokenizer.TryGetTokenId(candidate, out var tokenId))
            {
                destination.Add(tokenId);
            }
        }
    }

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "assistant" => "assistant",
            "tool" or "observation" => "observation",
            _ => "user"
        };

    private static string NormalizeContent(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
