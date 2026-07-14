using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tomur.Providers.Glm;

internal interface ITextNormalizer
{
    string Normalize(string text);
}

internal interface IPreTokenizer
{
    bool ProducesByteLevelText { get; }

    IReadOnlyList<string> Split(string text);
}

internal sealed class IdentityNormalizer : ITextNormalizer
{
    public static IdentityNormalizer Instance { get; } = new();

    public string Normalize(string text) => text;
}

internal sealed class SequenceNormalizer(IReadOnlyList<ITextNormalizer> normalizers) : ITextNormalizer
{
    public string Normalize(string text)
    {
        foreach (var normalizer in normalizers)
        {
            text = normalizer.Normalize(text);
        }

        return text;
    }
}

internal sealed class UnicodeNormalizer(NormalizationForm form) : ITextNormalizer
{
    public string Normalize(string text) => text.Normalize(form);
}

internal sealed class LowercaseNormalizer : ITextNormalizer
{
    public string Normalize(string text) => text.ToLowerInvariant();
}

internal sealed class StripNormalizer(bool left, bool right) : ITextNormalizer
{
    public string Normalize(string text)
    {
        var start = 0;
        var end = text.Length;
        if (left)
        {
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
        }

        if (right)
        {
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }
        }

        return start == 0 && end == text.Length ? text : text[start..end];
    }
}

internal sealed class StripAccentsNormalizer : ITextNormalizer
{
    public string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not UnicodeCategory.NonSpacingMark and
                not UnicodeCategory.SpacingCombiningMark and
                not UnicodeCategory.EnclosingMark)
            {
                builder.Append(rune);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

internal sealed class PrependNormalizer(string prefix) : ITextNormalizer
{
    public string Normalize(string text) => string.Concat(prefix, text);
}

internal sealed class ReplaceNormalizer : ITextNormalizer
{
    private readonly string? literal;
    private readonly Regex? regex;
    private readonly string replacement;

    public ReplaceNormalizer(string pattern, string replacement, bool isRegex)
    {
        this.replacement = replacement;
        if (isRegex)
        {
            try
            {
                regex = new Regex(
                    pattern,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Tokenizer normalizer contains an invalid regular expression.", exception);
            }
        }
        else
        {
            literal = pattern;
        }
    }

    public string Normalize(string text)
        => regex is null
            ? text.Replace(literal!, replacement, StringComparison.Ordinal)
            : regex.Replace(text, replacement);
}

internal sealed class BertNormalizer(
    bool cleanText,
    bool handleChineseCharacters,
    bool lowercase,
    bool stripAccents) : ITextNormalizer
{
    public string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (cleanText && (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control || rune.Value is 0 or 0xfffd))
            {
                if (rune.Value is '\t' or '\n' or '\r')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (handleChineseCharacters && IsCjk(rune.Value))
            {
                builder.Append(' ').Append(rune).Append(' ');
            }
            else
            {
                builder.Append(rune);
            }
        }

        var normalized = builder.ToString();
        if (lowercase)
        {
            normalized = normalized.ToLowerInvariant();
        }

        return stripAccents ? new StripAccentsNormalizer().Normalize(normalized) : normalized;
    }

    private static bool IsCjk(int value)
        => value is >= 0x3400 and <= 0x4dbf or
            >= 0x4e00 and <= 0x9fff or
            >= 0xf900 and <= 0xfaff or
            >= 0x20000 and <= 0x2fa1f;
}

internal sealed class IdentityPreTokenizer : IPreTokenizer
{
    public static IdentityPreTokenizer Instance { get; } = new();

    public bool ProducesByteLevelText => false;

    public IReadOnlyList<string> Split(string text) => text.Length == 0 ? [] : [text];
}

internal sealed class SequencePreTokenizer(IReadOnlyList<IPreTokenizer> preTokenizers) : IPreTokenizer
{
    public bool ProducesByteLevelText => preTokenizers.Any(static item => item.ProducesByteLevelText);

    public IReadOnlyList<string> Split(string text)
    {
        IReadOnlyList<string> pieces = text.Length == 0 ? [] : [text];
        foreach (var preTokenizer in preTokenizers)
        {
            var next = new List<string>();
            foreach (var piece in pieces)
            {
                next.AddRange(preTokenizer.Split(piece));
            }

            pieces = next;
        }

        return pieces;
    }
}

internal sealed class WhitespaceSplitPreTokenizer(bool splitPunctuation) : IPreTokenizer
{
    public bool ProducesByteLevelText => false;

    public IReadOnlyList<string> Split(string text)
    {
        var pieces = new List<string>();
        var builder = new StringBuilder();
        bool? currentIsWord = null;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                Flush(builder, pieces);
                currentIsWord = null;
                continue;
            }

            if (!splitPunctuation)
            {
                builder.Append(rune);
                continue;
            }

            var isWord = Rune.IsLetterOrDigit(rune) || rune.Value == '_';
            if (currentIsWord.HasValue && currentIsWord.Value != isWord)
            {
                Flush(builder, pieces);
            }

            builder.Append(rune);
            currentIsWord = isWord;
        }

        Flush(builder, pieces);
        return pieces;
    }

    private static void Flush(StringBuilder builder, List<string> pieces)
    {
        if (builder.Length == 0)
        {
            return;
        }

        pieces.Add(builder.ToString());
        builder.Clear();
    }
}

internal sealed class RegexSplitPreTokenizer : IPreTokenizer
{
    private readonly Regex regex;
    private readonly SplitBehavior behavior;
    private readonly bool invert;

    public RegexSplitPreTokenizer(string pattern, SplitBehavior behavior, bool invert)
    {
        this.behavior = behavior;
        this.invert = invert;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Tokenizer pre-tokenizer contains an invalid regular expression.", exception);
        }
    }

    public bool ProducesByteLevelText => false;

    public IReadOnlyList<string> Split(string text)
    {
        var matches = regex.Matches(text);
        if (matches.Count == 0)
        {
            return text.Length == 0 ? [] : [text];
        }

        var pieces = new List<string>();
        var position = 0;
        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            AddSegment(pieces, text[position..match.Index], isMatch: false);
            AddSegment(pieces, match.Value, isMatch: true);
            position = checked(match.Index + match.Length);
        }

        AddSegment(pieces, text[position..], isMatch: false);
        return pieces;
    }

    private void AddSegment(List<string> pieces, string value, bool isMatch)
    {
        if (value.Length == 0)
        {
            return;
        }

        var selected = invert ? !isMatch : isMatch;
        if (!selected)
        {
            pieces.Add(value);
            return;
        }

        switch (behavior)
        {
            case SplitBehavior.Removed:
                return;
            case SplitBehavior.MergedWithPrevious when pieces.Count > 0:
                pieces[^1] = string.Concat(pieces[^1], value);
                return;
            case SplitBehavior.MergedWithNext:
                pieces.Add(value);
                return;
            default:
                pieces.Add(value);
                return;
        }
    }
}

internal enum SplitBehavior
{
    Removed,
    Isolated,
    MergedWithPrevious,
    MergedWithNext,
    Contiguous
}

internal sealed class MetaspacePreTokenizer(string replacement, bool addPrefixSpace) : IPreTokenizer
{
    public bool ProducesByteLevelText => false;

    public IReadOnlyList<string> Split(string text)
    {
        if (addPrefixSpace && (text.Length == 0 || !char.IsWhiteSpace(text[0])))
        {
            text = string.Concat(" ", text);
        }

        var transformed = text.Replace(" ", replacement, StringComparison.Ordinal);
        return transformed.Length == 0 ? [] : [transformed];
    }
}

internal sealed partial class ByteLevelPreTokenizer(bool addPrefixSpace, bool useRegex) : IPreTokenizer
{
    public bool ProducesByteLevelText => true;

    public IReadOnlyList<string> Split(string text)
    {
        if (addPrefixSpace && (text.Length == 0 || text[0] != ' '))
        {
            text = string.Concat(" ", text);
        }

        if (!useRegex)
        {
            return text.Length == 0 ? [] : [ByteLevelEncoding.Encode(text)];
        }

        var pieces = new List<string>();
        foreach (Match match in ByteLevelPattern().Matches(text))
        {
            if (match.Success && match.Length > 0)
            {
                pieces.Add(ByteLevelEncoding.Encode(match.Value));
            }
        }

        return pieces;
    }

    [GeneratedRegex(
        "'(?:s|t|re|ve|m|ll|d)| ?\\p{L}+| ?\\p{N}+| ?[^\\s\\p{L}\\p{N}]+|\\s+(?!\\S)|\\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex ByteLevelPattern();
}

internal static class ByteLevelEncoding
{
    private static readonly char[] ByteToCharacter = CreateByteToCharacter();
    private static readonly IReadOnlyDictionary<char, byte> CharacterToByte = ByteToCharacter
        .Select(static (character, index) => KeyValuePair.Create(character, checked((byte)index)))
        .ToDictionary();

    public static string Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return EncodeBytes(bytes);
    }

    public static string EncodeBytes(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index] = ByteToCharacter[bytes[index]];
        }

        return new string(characters);
    }

    public static byte[] Decode(string text)
    {
        var bytes = new List<byte>(text.Length);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsBmp && CharacterToByte.TryGetValue(checked((char)rune.Value), out var value))
            {
                bytes.Add(value);
                continue;
            }

            rune.TryEncodeToUtf8(buffer, out var written);
            for (var index = 0; index < written; index++)
            {
                bytes.Add(buffer[index]);
            }
        }

        return bytes.ToArray();
    }

    private static char[] CreateByteToCharacter()
    {
        var visible = new List<int>();
        AddRange(visible, 33, 126);
        AddRange(visible, 161, 172);
        AddRange(visible, 174, 255);

        var characters = new char[256];
        var assigned = new bool[256];
        foreach (var value in visible)
        {
            characters[value] = checked((char)value);
            assigned[value] = true;
        }

        var offset = 0;
        for (var value = 0; value < 256; value++)
        {
            if (!assigned[value])
            {
                characters[value] = checked((char)(256 + offset));
                offset++;
            }
        }

        return characters;
    }

    private static void AddRange(List<int> destination, int start, int end)
    {
        for (var value = start; value <= end; value++)
        {
            destination.Add(value);
        }
    }
}
