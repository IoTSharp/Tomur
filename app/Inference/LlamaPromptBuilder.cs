using System.Text;

namespace Tomur.Inference;

public sealed record ChatTurn(string Role, string Content);

internal sealed class LlamaPromptBuilder
{
    private const string DefaultSystemPrompt = "You are a helpful local assistant.";

    public string BuildChatPrompt(IReadOnlyList<ChatTurn> messages, string modelName)
    {
        var normalized = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new ChatTurn(NormalizeRole(message.Role), NormalizeContent(message.Content)))
            .ToArray();

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return LooksLikeQwen(modelName)
            ? BuildChatMlPrompt(normalized)
            : BuildTaggedPrompt(normalized);
    }

    public IReadOnlyList<string> ResolveChatStops(string modelName, IReadOnlyList<string> requestStops)
    {
        var stops = requestStops
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (LooksLikeQwen(modelName))
        {
            AddStop(stops, "<|im_end|>");
            AddStop(stops, "<|endoftext|>");
            AddStop(stops, "<|im_start|>");
            return stops;
        }

        AddStop(stops, "\n[USER]");
        AddStop(stops, "\n[SYSTEM]");
        AddStop(stops, "\n[TOOL]");
        return stops;
    }

    public string PrepareCompletionPrompt(string prompt, int contextSize)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        var normalized = prompt.Trim();
        var approximateMaxChars = Math.Max(256, contextSize * 2);
        return normalized.Length <= approximateMaxChars
            ? normalized
            : normalized[^approximateMaxChars..];
    }

    private static string BuildChatMlPrompt(IReadOnlyList<ChatTurn> messages)
    {
        var builder = new StringBuilder();
        if (!messages.Any(static message => string.Equals(message.Role, "system", StringComparison.Ordinal)))
        {
            AppendChatMlTurn(builder, "system", DefaultSystemPrompt);
        }

        foreach (var message in messages)
        {
            AppendChatMlTurn(builder, message.Role, message.Content);
        }

        builder.Append("<|im_start|>assistant\n");
        return builder.ToString();
    }

    private static string BuildTaggedPrompt(IReadOnlyList<ChatTurn> messages)
    {
        var builder = new StringBuilder();
        if (!messages.Any(static message => string.Equals(message.Role, "system", StringComparison.Ordinal)))
        {
            AppendTaggedTurn(builder, "SYSTEM", DefaultSystemPrompt);
        }

        foreach (var message in messages)
        {
            var tag = message.Role switch
            {
                "system" => "SYSTEM",
                "assistant" => "ASSISTANT",
                "tool" => "TOOL",
                _ => "USER"
            };

            AppendTaggedTurn(builder, tag, message.Content);
        }

        builder.AppendLine("[ASSISTANT]");
        return builder.ToString();
    }

    private static void AppendChatMlTurn(StringBuilder builder, string role, string content)
    {
        builder.Append("<|im_start|>")
            .Append(role)
            .Append('\n')
            .Append(content)
            .Append("<|im_end|>\n");
    }

    private static void AppendTaggedTurn(StringBuilder builder, string role, string content)
    {
        builder.Append('[')
            .Append(role)
            .AppendLine("]")
            .AppendLine(content)
            .AppendLine();
    }

    private static void AddStop(List<string> stops, string stop)
    {
        if (!stops.Contains(stop, StringComparer.Ordinal))
        {
            stops.Add(stop);
        }
    }

    private static string NormalizeRole(string? role)
    {
        return role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "tool" => "tool",
            _ => "user"
        };
    }

    private static string NormalizeContent(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static bool LooksLikeQwen(string value)
        => value.Contains("qwen", StringComparison.OrdinalIgnoreCase);
}
