using System.Text;
using System.Text.Json;

namespace Tomur.Providers.Glm;

internal sealed class ManagedTokenizer
{
    private const long MaximumTokenizerBytes = 512L * 1024 * 1024;
    private const int MaximumVocabularyEntries = 1 << 24;
    private const int MaximumInputCharacters = 64 * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, int> vocabulary;
    private readonly IReadOnlyDictionary<int, TokenEntry> tokensById;
    private readonly IReadOnlyDictionary<(string Left, string Right), int> mergeRanks;
    private readonly ITextNormalizer normalizer;
    private readonly IPreTokenizer preTokenizer;
    private readonly AddedTokenMatcher preNormalizationAddedTokenMatcher;
    private readonly AddedTokenMatcher normalizedAddedTokenMatcher;
    private readonly TokenDecoder decoder;
    private readonly string modelType;
    private readonly int? unknownTokenId;
    private readonly bool byteFallback;
    private readonly bool fuseUnknown;
    private readonly bool ignoreMerges;

    private ManagedTokenizer(
        IReadOnlyDictionary<string, int> vocabulary,
        IReadOnlyDictionary<int, TokenEntry> tokensById,
        IReadOnlyDictionary<(string Left, string Right), int> mergeRanks,
        ITextNormalizer normalizer,
        IPreTokenizer preTokenizer,
        TokenDecoder decoder,
        string modelType,
        int? unknownTokenId,
        bool byteFallback,
        bool fuseUnknown,
        bool ignoreMerges)
    {
        this.vocabulary = vocabulary;
        this.tokensById = tokensById;
        this.mergeRanks = mergeRanks;
        this.normalizer = normalizer;
        this.preTokenizer = preTokenizer;
        this.decoder = decoder;
        this.modelType = modelType;
        this.unknownTokenId = unknownTokenId;
        this.byteFallback = byteFallback;
        this.fuseUnknown = fuseUnknown;
        this.ignoreMerges = ignoreMerges;
        var addedTokens = tokensById.Values.Where(static token => token.IsAdded).ToArray();
        preNormalizationAddedTokenMatcher = new AddedTokenMatcher(
            addedTokens.Where(static token => !token.Normalized).ToArray());
        normalizedAddedTokenMatcher = new AddedTokenMatcher(
            addedTokens.Where(static token => token.Normalized).ToArray());

        MaximumTokenId = tokensById.Keys.Max();
        VocabularySize = checked(MaximumTokenId + 1);
        BosTokenId = FindFirstTokenId("<bos>", "<s>", "[BOS]");
        EosTokenIds = ResolveTokenIds(
            "<eos>",
            "</s>",
            "<|endoftext|>",
            "<|end_of_text|>",
            "<|end_of_sentence|>",
            "<|eot_id|>");
    }

    public int VocabularySize { get; }

    public int MaximumTokenId { get; }

    public int? BosTokenId { get; }

    public IReadOnlyList<int> EosTokenIds { get; }

    public static ManagedTokenizer Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumTokenizerBytes)
        {
            throw new InvalidDataException(
                $"Tokenizer must exist and be between 1 and {MaximumTokenizerBytes} bytes: {path}");
        }

        using var stream = File.OpenRead(info.FullName);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer JSON must contain a model object.");
        }

        var modelType = GetRequiredString(model, "type");
        if (!modelType.Equals("WordLevel", StringComparison.Ordinal) &&
            !modelType.Equals("BPE", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Tokenizer model type '{modelType}' is not supported.");
        }

        var vocabulary = ReadVocabulary(model);
        var tokensById = vocabulary.ToDictionary(
            static item => item.Value,
            static item => new TokenEntry(item.Value, item.Key, false, false, false, false, false, true));
        ReadAddedTokens(root, vocabulary, tokensById);

        var unknownToken = GetOptionalString(model, "unk_token");
        int? unknownTokenId = null;
        if (!string.IsNullOrEmpty(unknownToken))
        {
            if (!vocabulary.TryGetValue(unknownToken, out var id))
            {
                throw new InvalidDataException($"Tokenizer unknown token is not present in the vocabulary: {unknownToken}");
            }

            unknownTokenId = id;
        }

        var byteFallback = GetOptionalBoolean(model, "byte_fallback", false);
        var fuseUnknown = GetOptionalBoolean(model, "fuse_unk", false);
        var ignoreMerges = GetOptionalBoolean(model, "ignore_merges", false);
        ValidateBpeOptions(model, modelType);
        var mergeRanks = modelType.Equals("BPE", StringComparison.Ordinal)
            ? ReadMerges(model, vocabulary)
            : new Dictionary<(string Left, string Right), int>();
        var normalizer = root.TryGetProperty("normalizer", out var normalizerElement) &&
                         normalizerElement.ValueKind != JsonValueKind.Null
            ? ReadNormalizer(normalizerElement)
            : IdentityNormalizer.Instance;
        var preTokenizer = root.TryGetProperty("pre_tokenizer", out var preTokenizerElement) &&
                           preTokenizerElement.ValueKind != JsonValueKind.Null
            ? ReadPreTokenizer(preTokenizerElement)
            : IdentityPreTokenizer.Instance;
        var decoder = root.TryGetProperty("decoder", out var decoderElement) &&
                      decoderElement.ValueKind != JsonValueKind.Null
            ? TokenDecoder.Read(decoderElement)
            : modelType.Equals("WordLevel", StringComparison.Ordinal)
                ? TokenDecoder.WordLevel
                : TokenDecoder.Raw;

        return new ManagedTokenizer(
            vocabulary,
            tokensById,
            mergeRanks,
            normalizer,
            preTokenizer,
            decoder,
            modelType,
            unknownTokenId,
            byteFallback,
            fuseUnknown,
            ignoreMerges);
    }

    public IReadOnlyList<int> Encode(string text, bool addBos = false, bool parseSpecialTokens = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumInputCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                $"Tokenizer input exceeds {MaximumInputCharacters} UTF-16 code units.");
        }

        var output = new List<int>();
        if (addBos)
        {
            if (!BosTokenId.HasValue)
            {
                throw new InvalidDataException("Tokenizer does not define a recognized BOS token.");
            }

            output.Add(BosTokenId.Value);
        }

        foreach (var rawSegment in preNormalizationAddedTokenMatcher.Split(text, parseSpecialTokens))
        {
            if (rawSegment.TokenId.HasValue)
            {
                output.Add(rawSegment.TokenId.Value);
                continue;
            }

            var normalized = normalizer.Normalize(rawSegment.Text);
            if (normalized.Length > MaximumInputCharacters)
            {
                throw new InvalidDataException(
                    $"Tokenizer normalization exceeds {MaximumInputCharacters} UTF-16 code units.");
            }

            foreach (var segment in normalizedAddedTokenMatcher.Split(normalized, parseSpecialTokens))
            {
                if (segment.TokenId.HasValue)
                {
                    output.Add(segment.TokenId.Value);
                    continue;
                }

                foreach (var piece in preTokenizer.Split(segment.Text))
                {
                    if (modelType.Equals("WordLevel", StringComparison.Ordinal))
                    {
                        EncodeWord(piece, output);
                    }
                    else
                    {
                        EncodeBpe(piece, output);
                    }
                }
            }
        }

        return output;
    }

    public string Decode(IEnumerable<int> tokenIds, bool skipSpecialTokens = true)
    {
        ArgumentNullException.ThrowIfNull(tokenIds);
        var builder = new StringBuilder();
        var incremental = new IncrementalTextDecoder(
            this,
            [],
            [],
            value => builder.Append(value),
            skipSpecialTokens);
        foreach (var tokenId in tokenIds)
        {
            incremental.AppendToken(tokenId);
        }

        incremental.Complete();
        return builder.ToString();
    }

    public bool TryGetTokenId(string content, out int tokenId)
    {
        ArgumentNullException.ThrowIfNull(content);
        return vocabulary.TryGetValue(content, out tokenId);
    }

    public IReadOnlyList<int> ResolveTokenIds(params string[] contents)
        => contents
            .Where(static content => !string.IsNullOrEmpty(content))
            .Select(content => vocabulary.TryGetValue(content, out var tokenId) ? tokenId : -1)
            .Where(static tokenId => tokenId >= 0)
            .Distinct()
            .ToArray();

    public bool IsSpecialToken(int tokenId)
        => tokensById.TryGetValue(tokenId, out var token) && token.Special;

    public bool ContainsTokenId(int tokenId) => tokensById.ContainsKey(tokenId);

    internal byte[] DecodeTokenBytes(int tokenId, bool skipSpecialToken, bool firstToken)
    {
        if (!tokensById.TryGetValue(tokenId, out var token))
        {
            throw new InvalidDataException($"Token ID is not present in the tokenizer vocabulary: {tokenId}");
        }

        if (skipSpecialToken && token.Special)
        {
            return [];
        }

        if (token.IsAdded)
        {
            return Encoding.UTF8.GetBytes(token.Content);
        }

        return decoder.Decode(token.Content, firstToken);
    }

    private int? FindFirstTokenId(params string[] contents)
    {
        foreach (var content in contents)
        {
            if (vocabulary.TryGetValue(content, out var tokenId))
            {
                return tokenId;
            }
        }

        return null;
    }

    private void EncodeWord(string piece, List<int> output)
    {
        if (vocabulary.TryGetValue(piece, out var tokenId) && !IsSpecialToken(tokenId))
        {
            output.Add(tokenId);
            return;
        }

        output.Add(RequireUnknownToken(piece));
    }

    private void EncodeBpe(string piece, List<int> output)
    {
        if (piece.Length == 0)
        {
            return;
        }

        if (ignoreMerges &&
            vocabulary.TryGetValue(piece, out var wholePieceTokenId) &&
            !IsSpecialToken(wholePieceTokenId))
        {
            output.Add(wholePieceTokenId);
            return;
        }

        var symbols = piece.EnumerateRunes().Select(static rune => rune.ToString()).ToArray();
        if (symbols.Length > 1 && mergeRanks.Count > 0)
        {
            symbols = ApplyMerges(symbols);
        }

        var previousWasUnknown = false;
        foreach (var symbol in symbols)
        {
            if (vocabulary.TryGetValue(symbol, out var tokenId) && !IsSpecialToken(tokenId))
            {
                output.Add(tokenId);
                previousWasUnknown = false;
                continue;
            }

            if (byteFallback)
            {
                var bytes = preTokenizer.ProducesByteLevelText
                    ? ByteLevelEncoding.Decode(symbol)
                    : Encoding.UTF8.GetBytes(symbol);
                foreach (var value in bytes)
                {
                    var fallback = $"<0x{value:X2}>";
                    if (!vocabulary.TryGetValue(fallback, out var fallbackId))
                    {
                        throw new InvalidDataException(
                            $"Tokenizer byte fallback token is missing from the vocabulary: {fallback}");
                    }
                    if (IsSpecialToken(fallbackId))
                    {
                        throw new InvalidDataException(
                            $"Tokenizer byte fallback token must not be marked special: {fallback}");
                    }

                    output.Add(fallbackId);
                }

                previousWasUnknown = false;
                continue;
            }

            if (!fuseUnknown || !previousWasUnknown)
            {
                output.Add(RequireUnknownToken(symbol));
            }

            previousWasUnknown = true;
        }
    }

    private string[] ApplyMerges(IReadOnlyList<string> symbols)
    {
        var nodes = symbols.Select(static symbol => new BpeNode(symbol)).ToArray();
        for (var index = 0; index < nodes.Length; index++)
        {
            nodes[index].Previous = index - 1;
            nodes[index].Next = index + 1 < nodes.Length ? index + 1 : -1;
        }

        var queue = new PriorityQueue<MergeCandidate, (int Rank, int Left)>();
        for (var index = 0; index + 1 < nodes.Length; index++)
        {
            EnqueuePair(queue, nodes, index);
        }

        while (queue.TryDequeue(out var candidate, out _))
        {
            var left = nodes[candidate.Left];
            if (!left.Alive || left.Version != candidate.LeftVersion || left.Next != candidate.Right)
            {
                continue;
            }

            var right = nodes[candidate.Right];
            if (!right.Alive || right.Version != candidate.RightVersion)
            {
                continue;
            }

            if (!mergeRanks.TryGetValue((left.Value, right.Value), out var currentRank) ||
                currentRank != candidate.Rank)
            {
                continue;
            }

            left.Value = string.Concat(left.Value, right.Value);
            left.Next = right.Next;
            left.Version++;
            right.Alive = false;
            right.Version++;
            if (right.Next >= 0)
            {
                nodes[right.Next].Previous = candidate.Left;
            }

            if (left.Previous >= 0)
            {
                EnqueuePair(queue, nodes, left.Previous);
            }

            EnqueuePair(queue, nodes, candidate.Left);
        }

        var output = new List<string>();
        var current = 0;
        while (current >= 0)
        {
            if (nodes[current].Alive)
            {
                output.Add(nodes[current].Value);
            }

            current = nodes[current].Next;
        }

        return output.ToArray();
    }

    private void EnqueuePair(
        PriorityQueue<MergeCandidate, (int Rank, int Left)> queue,
        IReadOnlyList<BpeNode> nodes,
        int leftIndex)
    {
        if (leftIndex < 0 || !nodes[leftIndex].Alive)
        {
            return;
        }

        var rightIndex = nodes[leftIndex].Next;
        if (rightIndex < 0 || !nodes[rightIndex].Alive ||
            !mergeRanks.TryGetValue((nodes[leftIndex].Value, nodes[rightIndex].Value), out var rank))
        {
            return;
        }

        queue.Enqueue(
            new MergeCandidate(
                leftIndex,
                rightIndex,
                nodes[leftIndex].Version,
                nodes[rightIndex].Version,
                rank),
            (rank, leftIndex));
    }

    private int RequireUnknownToken(string value)
        => unknownTokenId ?? throw new InvalidDataException(
            $"Tokenizer cannot encode '{value}' because no unknown token or byte fallback is configured.");

    private static Dictionary<string, int> ReadVocabulary(JsonElement model)
    {
        if (!model.TryGetProperty("vocab", out var vocab) || vocab.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer model must contain a vocab object.");
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<int>();
        foreach (var property in vocab.EnumerateObject())
        {
            if (result.Count >= MaximumVocabularyEntries)
            {
                throw new InvalidDataException($"Tokenizer vocabulary exceeds {MaximumVocabularyEntries} entries.");
            }

            if (property.Name.Length == 0 ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var tokenId) ||
                tokenId < 0 ||
                tokenId >= MaximumVocabularyEntries)
            {
                throw new InvalidDataException($"Tokenizer vocabulary entry is invalid: {property.Name}");
            }

            if (!result.TryAdd(property.Name, tokenId) || !ids.Add(tokenId))
            {
                throw new InvalidDataException($"Tokenizer vocabulary contains a duplicate token or ID: {property.Name}");
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("Tokenizer vocabulary must not be empty.");
        }

        return result;
    }

    private static void ReadAddedTokens(
        JsonElement root,
        IDictionary<string, int> vocabulary,
        IDictionary<int, TokenEntry> tokensById)
    {
        if (!root.TryGetProperty("added_tokens", out var addedTokens) || addedTokens.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (addedTokens.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Tokenizer added_tokens must be an array.");
        }

        foreach (var item in addedTokens.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt32(out var tokenId) ||
                tokenId < 0 ||
                tokenId >= MaximumVocabularyEntries)
            {
                throw new InvalidDataException("Tokenizer contains an invalid added token ID.");
            }

            var content = GetRequiredString(item, "content");
            var entry = new TokenEntry(
                tokenId,
                content,
                GetOptionalBoolean(item, "special", false),
                true,
                GetOptionalBoolean(item, "single_word", false),
                GetOptionalBoolean(item, "lstrip", false),
                GetOptionalBoolean(item, "rstrip", false),
                GetOptionalBoolean(item, "normalized", true));

            if (vocabulary.TryGetValue(content, out var vocabularyId) && vocabularyId != tokenId)
            {
                throw new InvalidDataException($"Added token '{content}' conflicts with its vocabulary ID.");
            }

            if (tokensById.TryGetValue(tokenId, out var existing) &&
                !string.Equals(existing.Content, content, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Added token ID {tokenId} conflicts with '{existing.Content}'.");
            }

            vocabulary[content] = tokenId;
            tokensById[tokenId] = entry;
        }
    }

    private static Dictionary<(string Left, string Right), int> ReadMerges(
        JsonElement model,
        IReadOnlyDictionary<string, int> vocabulary)
    {
        var result = new Dictionary<(string Left, string Right), int>();
        if (!model.TryGetProperty("merges", out var merges) || merges.ValueKind == JsonValueKind.Null)
        {
            return result;
        }

        if (merges.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Tokenizer BPE merges must be an array.");
        }

        var rank = 0;
        foreach (var merge in merges.EnumerateArray())
        {
            string left;
            string right;
            if (merge.ValueKind == JsonValueKind.String)
            {
                var value = merge.GetString() ?? string.Empty;
                var separator = value.IndexOf(' ');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    throw new InvalidDataException($"Tokenizer contains an invalid BPE merge: {value}");
                }

                left = value[..separator];
                right = value[(separator + 1)..];
            }
            else if (merge.ValueKind == JsonValueKind.Array)
            {
                var pair = merge.EnumerateArray().ToArray();
                if (pair.Length != 2 ||
                    pair[0].ValueKind != JsonValueKind.String ||
                    pair[1].ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("Tokenizer contains an invalid BPE merge pair.");
                }

                left = pair[0].GetString() ?? string.Empty;
                right = pair[1].GetString() ?? string.Empty;
            }
            else
            {
                throw new InvalidDataException("Tokenizer contains an invalid BPE merge entry.");
            }

            if (left.Length == 0 || right.Length == 0 || !result.TryAdd((left, right), rank))
            {
                throw new InvalidDataException($"Tokenizer contains an empty or duplicate BPE merge: {left} {right}");
            }

            var merged = string.Concat(left, right);
            if (!vocabulary.ContainsKey(merged))
            {
                throw new InvalidDataException(
                    $"Tokenizer BPE merge output is missing from the vocabulary: {left} {right}");
            }

            rank = checked(rank + 1);
        }

        return result;
    }

    private static void ValidateBpeOptions(JsonElement model, string modelType)
    {
        if (!modelType.Equals("BPE", StringComparison.Ordinal))
        {
            return;
        }

        if (model.TryGetProperty("dropout", out var dropout) &&
            dropout.ValueKind != JsonValueKind.Null &&
            (dropout.ValueKind != JsonValueKind.Number || dropout.GetDouble() != 0.0))
        {
            throw new InvalidDataException("Tokenizer BPE dropout is not supported by deterministic inference.");
        }

        foreach (var option in new[] { "continuing_subword_prefix", "end_of_word_suffix" })
        {
            if (model.TryGetProperty(option, out var value) &&
                value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Tokenizer BPE option '{option}' must be a string or null.");
                }

                if (!string.IsNullOrEmpty(value.GetString()))
                {
                    throw new InvalidDataException($"Tokenizer BPE option '{option}' is not supported.");
                }
            }
        }

        _ = GetOptionalBoolean(model, "ignore_merges", false);
    }

    private static ITextNormalizer ReadNormalizer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer normalizer must be an object.");
        }

        var type = GetRequiredString(element, "type");
        return type switch
        {
            "Sequence" => new SequenceNormalizer(ReadObjectArray(element, "normalizers", ReadNormalizer)),
            "NFC" => new UnicodeNormalizer(NormalizationForm.FormC),
            "NFD" => new UnicodeNormalizer(NormalizationForm.FormD),
            "NFKC" => new UnicodeNormalizer(NormalizationForm.FormKC),
            "NFKD" => new UnicodeNormalizer(NormalizationForm.FormKD),
            "Lowercase" => new LowercaseNormalizer(),
            "Strip" => new StripNormalizer(
                GetOptionalBoolean(element, "strip_left", true),
                GetOptionalBoolean(element, "strip_right", true)),
            "StripAccents" => new StripAccentsNormalizer(),
            "Prepend" => new PrependNormalizer(GetRequiredString(element, "prepend")),
            "Replace" => CreateReplaceNormalizer(element),
            "BertNormalizer" => new BertNormalizer(
                GetOptionalBoolean(element, "clean_text", true),
                GetOptionalBoolean(element, "handle_chinese_chars", true),
                GetOptionalBoolean(element, "lowercase", true),
                GetOptionalBoolean(element, "strip_accents", false)),
            _ => throw new InvalidDataException($"Tokenizer normalizer type '{type}' is not supported.")
        };
    }

    private static IPreTokenizer ReadPreTokenizer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer pre-tokenizer must be an object.");
        }

        var type = GetRequiredString(element, "type");
        return type switch
        {
            "Sequence" => new SequencePreTokenizer(ReadObjectArray(element, "pretokenizers", ReadPreTokenizer)),
            "WhitespaceSplit" => new WhitespaceSplitPreTokenizer(false),
            "Whitespace" or "BertPreTokenizer" => new WhitespaceSplitPreTokenizer(true),
            "ByteLevel" => new ByteLevelPreTokenizer(
                GetOptionalBoolean(element, "add_prefix_space", false),
                GetOptionalBoolean(element, "use_regex", true)),
            "Metaspace" => new MetaspacePreTokenizer(
                GetOptionalString(element, "replacement") ?? "▁",
                ResolveMetaspacePrefix(element)),
            "Split" => CreateSplitPreTokenizer(element),
            _ => throw new InvalidDataException($"Tokenizer pre-tokenizer type '{type}' is not supported.")
        };
    }

    private static ReplaceNormalizer CreateReplaceNormalizer(JsonElement element)
    {
        var (pattern, isRegex) = ReadPattern(element, "pattern");
        return new ReplaceNormalizer(pattern, GetRequiredString(element, "content"), isRegex);
    }

    private static RegexSplitPreTokenizer CreateSplitPreTokenizer(JsonElement element)
    {
        var (pattern, isRegex) = ReadPattern(element, "pattern");
        if (!isRegex)
        {
            pattern = RegexEscape(pattern);
        }

        var behaviorName = GetOptionalString(element, "behavior") ?? "Isolated";
        if (!Enum.TryParse<SplitBehavior>(behaviorName, ignoreCase: true, out var behavior))
        {
            throw new InvalidDataException($"Tokenizer split behavior '{behaviorName}' is not supported.");
        }

        if (behavior is SplitBehavior.MergedWithNext or SplitBehavior.Contiguous ||
            GetOptionalBoolean(element, "invert", false))
        {
            throw new InvalidDataException(
                $"Tokenizer split behavior '{behaviorName}' with the requested invert setting is not supported.");
        }

        return new RegexSplitPreTokenizer(
            pattern,
            behavior,
            invert: false);
    }

    private static string RegexEscape(string value)
        => System.Text.RegularExpressions.Regex.Escape(value);

    private static bool ResolveMetaspacePrefix(JsonElement element)
    {
        if (element.TryGetProperty("prepend_scheme", out var scheme) && scheme.ValueKind == JsonValueKind.String)
        {
            return !string.Equals(scheme.GetString(), "never", StringComparison.OrdinalIgnoreCase);
        }

        return GetOptionalBoolean(element, "add_prefix_space", true);
    }

    private static (string Pattern, bool IsRegex) ReadPattern(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var pattern))
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' is required.");
        }

        if (pattern.ValueKind == JsonValueKind.String)
        {
            return (pattern.GetString() ?? string.Empty, false);
        }

        if (pattern.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' must be a string or pattern object.");
        }

        if (pattern.TryGetProperty("String", out var literal) && literal.ValueKind == JsonValueKind.String)
        {
            return (literal.GetString() ?? string.Empty, false);
        }

        if (pattern.TryGetProperty("Regex", out var regex) && regex.ValueKind == JsonValueKind.String)
        {
            return (regex.GetString() ?? string.Empty, true);
        }

        throw new InvalidDataException($"Tokenizer property '{propertyName}' contains an unknown pattern kind.");
    }

    private static IReadOnlyList<T> ReadObjectArray<T>(
        JsonElement element,
        string propertyName,
        Func<JsonElement, T> reader)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' must be an array.");
        }

        var result = new List<T>();
        foreach (var item in array.EnumerateArray())
        {
            result.Add(reader(item));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' must not be empty.");
        }

        return result;
    }

    internal static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(property.GetString()))
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    internal static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Tokenizer property '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    internal static bool GetOptionalBoolean(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Tokenizer property '{propertyName}' must be a boolean.")
        };
    }

    private sealed class BpeNode(string value)
    {
        public string Value { get; set; } = value;

        public int Previous { get; set; }

        public int Next { get; set; }

        public int Version { get; set; }

        public bool Alive { get; set; } = true;
    }

    private readonly record struct MergeCandidate(
        int Left,
        int Right,
        int LeftVersion,
        int RightVersion,
        int Rank);
}

internal sealed record TokenEntry(
    int Id,
    string Content,
    bool Special,
    bool IsAdded,
    bool SingleWord,
    bool LeftStrip,
    bool RightStrip,
    bool Normalized);

internal readonly record struct TokenSegment(string Text, int? TokenId);

internal sealed class AddedTokenMatcher
{
    private readonly IReadOnlyDictionary<char, IReadOnlyList<TokenEntry>> tokensByFirstCharacter;

    public AddedTokenMatcher(IReadOnlyList<TokenEntry> tokens)
    {
        tokensByFirstCharacter = tokens
            .Where(static token => token.Content.Length > 0)
            .GroupBy(static token => token.Content[0])
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<TokenEntry>)group
                    .OrderByDescending(static token => token.Content.Length)
                    .ThenBy(static token => token.Id)
                    .ToArray());
    }

    public IReadOnlyList<TokenSegment> Split(string text, bool includeSpecialTokens)
    {
        if (tokensByFirstCharacter.Count == 0 || text.Length == 0)
        {
            return text.Length == 0 ? [] : [new TokenSegment(text, null)];
        }

        var result = new List<TokenSegment>();
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            var match = FindMatch(text, index, includeSpecialTokens);
            if (match is null)
            {
                index++;
                continue;
            }

            var plainEnd = index;
            if (match.LeftStrip)
            {
                while (plainEnd > plainStart && char.IsWhiteSpace(text[plainEnd - 1]))
                {
                    plainEnd--;
                }
            }

            if (plainEnd > plainStart)
            {
                result.Add(new TokenSegment(text[plainStart..plainEnd], null));
            }

            result.Add(new TokenSegment(string.Empty, match.Id));
            index = checked(index + match.Content.Length);
            if (match.RightStrip)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }
            }

            plainStart = index;
        }

        if (plainStart < text.Length)
        {
            result.Add(new TokenSegment(text[plainStart..], null));
        }

        return result;
    }

    private TokenEntry? FindMatch(string text, int index, bool includeSpecialTokens)
    {
        if (!tokensByFirstCharacter.TryGetValue(text[index], out var candidates))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if ((candidate.Special && !includeSpecialTokens) ||
                index + candidate.Content.Length > text.Length ||
                !text.AsSpan(index, candidate.Content.Length).SequenceEqual(candidate.Content.AsSpan()) ||
                (candidate.SingleWord && !IsSingleWord(text, index, candidate.Content.Length)))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsSingleWord(string text, int index, int length)
    {
        var leftIsWord = index > 0 && IsWordCharacter(text[index - 1]);
        var end = checked(index + length);
        var rightIsWord = end < text.Length && IsWordCharacter(text[end]);
        return !leftIsWord && !rightIsWord;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

internal enum TokenDecoderKind
{
    Raw,
    WordLevel,
    ByteLevel,
    ByteFallback,
    Metaspace,
    BpeSuffix,
    WordPiece
}

internal sealed class TokenDecoder
{
    private readonly TokenDecoderKind kind;
    private readonly string marker;
    private readonly IReadOnlyList<(string Pattern, string Replacement)> replacements;
    private readonly string? stripContent;
    private readonly int stripStart;

    private TokenDecoder(
        TokenDecoderKind kind,
        string marker = "",
        IReadOnlyList<(string Pattern, string Replacement)>? replacements = null,
        string? stripContent = null,
        int stripStart = 0)
    {
        this.kind = kind;
        this.marker = marker;
        this.replacements = replacements ?? [];
        this.stripContent = stripContent;
        this.stripStart = stripStart;
    }

    public static TokenDecoder Raw { get; } = new(TokenDecoderKind.Raw);

    public static TokenDecoder WordLevel { get; } = new(TokenDecoderKind.WordLevel);

    public static TokenDecoder Read(JsonElement element)
    {
        var specs = new List<DecoderSpec>();
        ReadSpecs(element, specs);
        var baseSpecs = specs.Where(static spec => spec.Kind.HasValue).ToArray();
        if (baseSpecs.Length > 1)
        {
            throw new InvalidDataException("Tokenizer decoder sequence contains multiple incompatible base decoders.");
        }

        var selected = baseSpecs.SingleOrDefault();
        var replacements = specs
            .Where(static spec => spec.Replacement.HasValue)
            .Select(static spec => spec.Replacement!.Value)
            .ToArray();
        var strip = specs.FirstOrDefault(static spec => spec.StripContent is not null);
        return new TokenDecoder(
            selected?.Kind ?? TokenDecoderKind.Raw,
            selected?.Marker ?? string.Empty,
            replacements,
            strip?.StripContent,
            strip?.StripStart ?? 0);
    }

    public byte[] Decode(string token, bool firstToken)
    {
        foreach (var replacement in replacements)
        {
            token = token.Replace(replacement.Pattern, replacement.Replacement, StringComparison.Ordinal);
        }

        var decoded = kind switch
        {
            TokenDecoderKind.WordLevel => Encoding.UTF8.GetBytes(firstToken ? token : string.Concat(" ", token)),
            TokenDecoderKind.ByteLevel => ByteLevelEncoding.Decode(token),
            TokenDecoderKind.ByteFallback => DecodeByteFallback(token),
            TokenDecoderKind.Metaspace => Encoding.UTF8.GetBytes(DecodeMetaspace(token, firstToken)),
            TokenDecoderKind.BpeSuffix => Encoding.UTF8.GetBytes(token.Replace(marker, " ", StringComparison.Ordinal)),
            TokenDecoderKind.WordPiece => Encoding.UTF8.GetBytes(DecodeWordPiece(token, firstToken)),
            _ => Encoding.UTF8.GetBytes(token)
        };
        if (!firstToken || stripContent is null || stripStart == 0)
        {
            return decoded;
        }

        var prefix = Encoding.UTF8.GetBytes(stripContent);
        var offset = 0;
        for (var count = 0;
             count < stripStart && decoded.AsSpan(offset).StartsWith(prefix);
             count++)
        {
            offset = checked(offset + prefix.Length);
        }

        return offset == 0 ? decoded : decoded[offset..];
    }

    private static void ReadSpecs(JsonElement element, List<DecoderSpec> specs)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer decoder must be an object.");
        }

        var type = ManagedTokenizer.GetRequiredString(element, "type");
        switch (type)
        {
            case "Sequence":
                if (!element.TryGetProperty("decoders", out var decoders) || decoders.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Tokenizer decoder sequence must contain a decoders array.");
                }

                foreach (var decoder in decoders.EnumerateArray())
                {
                    ReadSpecs(decoder, specs);
                }

                return;
            case "ByteLevel":
                specs.Add(new DecoderSpec(TokenDecoderKind.ByteLevel));
                return;
            case "ByteFallback":
                specs.Add(new DecoderSpec(TokenDecoderKind.ByteFallback));
                return;
            case "Fuse":
                return;
            case "Metaspace":
                specs.Add(new DecoderSpec(
                    TokenDecoderKind.Metaspace,
                    ManagedTokenizer.GetOptionalString(element, "replacement") ?? "▁"));
                return;
            case "BPEDecoder":
                specs.Add(new DecoderSpec(
                    TokenDecoderKind.BpeSuffix,
                    ManagedTokenizer.GetOptionalString(element, "suffix") ?? "</w>"));
                return;
            case "WordPiece":
                specs.Add(new DecoderSpec(
                    TokenDecoderKind.WordPiece,
                    ManagedTokenizer.GetOptionalString(element, "prefix") ?? "##"));
                return;
            case "Replace":
                var (pattern, _) = ReadLiteralDecoderPattern(element);
                specs.Add(new DecoderSpec(
                    Replacement: (pattern, ManagedTokenizer.GetRequiredString(element, "content"))));
                return;
            case "Strip":
                var stripStop = GetOptionalNonNegativeInt(element, "stop");
                if (stripStop != 0)
                {
                    throw new InvalidDataException("Tokenizer Strip decoder only supports stop=0 for incremental output.");
                }

                specs.Add(new DecoderSpec(
                    StripContent: ManagedTokenizer.GetRequiredString(element, "content"),
                    StripStart: GetOptionalNonNegativeInt(element, "start")));
                return;
            default:
                throw new InvalidDataException($"Tokenizer decoder type '{type}' is not supported.");
        }
    }

    private static (string Pattern, bool IsRegex) ReadLiteralDecoderPattern(JsonElement element)
    {
        if (!element.TryGetProperty("pattern", out var pattern))
        {
            throw new InvalidDataException("Tokenizer Replace decoder requires a pattern.");
        }

        if (pattern.ValueKind == JsonValueKind.String)
        {
            return (pattern.GetString() ?? string.Empty, false);
        }

        if (pattern.ValueKind == JsonValueKind.Object &&
            pattern.TryGetProperty("String", out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return (value.GetString() ?? string.Empty, false);
        }

        throw new InvalidDataException("Tokenizer Replace decoder only supports literal patterns.");
    }

    private static int GetOptionalNonNegativeInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result < 0 ||
            result > 1024)
        {
            throw new InvalidDataException($"Tokenizer decoder property '{propertyName}' is invalid.");
        }

        return result;
    }

    private static byte[] DecodeByteFallback(string token)
    {
        if (token.Length == 6 &&
            token.StartsWith("<0x", StringComparison.Ordinal) &&
            token[^1] == '>' &&
            byte.TryParse(token.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return [value];
        }

        return Encoding.UTF8.GetBytes(token);
    }

    private string DecodeMetaspace(string token, bool firstToken)
    {
        var value = token.Replace(marker, " ", StringComparison.Ordinal);
        return firstToken && value.StartsWith(' ') ? value[1..] : value;
    }

    private string DecodeWordPiece(string token, bool firstToken)
    {
        if (token.StartsWith(marker, StringComparison.Ordinal))
        {
            return token[marker.Length..];
        }

        return firstToken ? token : string.Concat(" ", token);
    }

    private sealed record DecoderSpec(
        TokenDecoderKind? Kind = null,
        string Marker = "",
        (string Pattern, string Replacement)? Replacement = null,
        string? StripContent = null,
        int StripStart = 0);
}
