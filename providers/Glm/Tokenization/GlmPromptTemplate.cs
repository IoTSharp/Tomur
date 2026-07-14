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
    private const string ToolResponseStart = "<tool_response>";
    private const string ToolResponseEnd = "</tool_response>";
    private readonly ManagedTokenizer tokenizer;
    private readonly bool usesMoeLiteTemplate;

    public GlmPromptTemplate(
        ManagedTokenizer tokenizer,
        string modelType = GlmModelConfiguration.DsaModelType)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        if (!GlmModelConfiguration.IsSupportedModelType(modelType))
        {
            throw new ArgumentOutOfRangeException(nameof(modelType), modelType, "Unsupported GLM prompt model type.");
        }

        this.tokenizer = tokenizer;
        usesMoeLiteTemplate = modelType.Equals(
            GlmModelConfiguration.MoeLiteModelType,
            StringComparison.OrdinalIgnoreCase);
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
        if (usesMoeLiteTemplate)
        {
            AppendEncoded(tokens, "\n", "GLM4 MoE Lite prompt prefix newline");
        }

        foreach (var message in normalized)
        {
            tokens.Add(RequireRoleToken(message.Role));
            if (usesMoeLiteTemplate && message.Role.Equals("assistant", StringComparison.Ordinal))
            {
                tokens.Add(RequireTokenId(ThinkEndToken));
                tokens.AddRange(tokenizer.Encode(StripThinking(message.Content)));
            }
            else if (usesMoeLiteTemplate && message.Role.Equals("observation", StringComparison.Ordinal))
            {
                AppendEncoded(tokens, ToolResponseStart, "GLM4 MoE Lite tool response start");
                tokens.AddRange(tokenizer.Encode(message.Content));
                AppendEncoded(tokens, ToolResponseEnd, "GLM4 MoE Lite tool response end");
            }
            else
            {
                tokens.AddRange(tokenizer.Encode(message.Content));
            }
        }

        if (!string.Equals(normalized[^1].Role, "assistant", StringComparison.Ordinal))
        {
            tokens.Add(RequireRoleToken("assistant"));
            if (usesMoeLiteTemplate)
            {
                tokens.Add(RequireTokenId(ThinkEndToken));
            }
            else if (tokenizer.TryGetTokenId(ThinkStartToken, out var thinkStart) &&
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

    private int RequireTokenId(string content)
        => tokenizer.TryGetTokenId(content, out var tokenId)
            ? tokenId
            : throw new InvalidDataException($"GLM tokenizer does not define required token '{content}'.");

    private void AppendEncoded(List<int> destination, string content, string description)
    {
        var encoded = tokenizer.Encode(content);
        if (encoded.Count == 0)
        {
            throw new InvalidDataException($"Tokenizer produced no tokens for {description}.");
        }

        destination.AddRange(encoded);
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

    private static string StripThinking(string content)
    {
        var marker = content.LastIndexOf(ThinkEndToken, StringComparison.Ordinal);
        return marker < 0
            ? content
            : content[(marker + ThinkEndToken.Length)..].TrimStart('\r', '\n');
    }
}
