using Tomur.Inference;
using Tomur.Providers.Glm;

namespace Tomur.Providers.Olmoe;

internal sealed record OlmoePrompt(
    IReadOnlyList<int> TokenIds,
    IReadOnlyList<int> StopTokenIds);

internal sealed class OlmoePromptTemplate(
    OlmoeModelConfiguration configuration,
    ManagedTokenizer tokenizer)
{
    public OlmoePrompt BuildCompletion(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var tokens = new List<int> { configuration.EosTokenId };
        tokens.AddRange(tokenizer.Encode(prompt));
        return new OlmoePrompt(tokens, [configuration.EosTokenId]);
    }

    public OlmoePrompt BuildChat(IReadOnlyList<ChatTurn> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var normalized = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new ChatTurn(
                NormalizeRole(message.Role),
                message.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()))
            .ToArray();
        if (normalized.Length == 0)
        {
            return new OlmoePrompt([], [configuration.EosTokenId]);
        }

        var tokens = new List<int> { configuration.EosTokenId };
        foreach (var message in normalized)
        {
            var role = message.Role switch
            {
                "system" => "system",
                "assistant" => "assistant",
                _ => "user"
            };
            tokens.AddRange(tokenizer.Encode($"<|{role}|>\n{message.Content}"));
            if (role == "assistant")
            {
                tokens.Add(configuration.EosTokenId);
            }

            tokens.AddRange(tokenizer.Encode("\n"));
        }

        tokens.AddRange(tokenizer.Encode("<|assistant|>\n"));
        return new OlmoePrompt(tokens, [configuration.EosTokenId]);
    }

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "assistant" => "assistant",
            _ => "user"
        };
}
