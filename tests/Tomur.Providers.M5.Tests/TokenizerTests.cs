using System.Text.Json;
using Tomur.Inference;
using Tomur.Providers.Glm;

namespace Tomur.Providers.M5.Tests;

public sealed class TokenizerTests
{
    [Fact]
    public void WordLevelEncodingMatchesTinyFixtureOracle()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var tokenizer = ManagedTokenizer.Read(Path.Combine(fixturePath, TinyFixtureFiles.Tokenizer));
        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);

        foreach (var item in oracle.Tokenization)
        {
            Assert.Equal(item.TokenIds, tokenizer.Encode(item.Text, item.AddBos));
        }

        Assert.Equal("hello Tomur", tokenizer.Decode([1, 4, 6]));
        Assert.Equal(new[] { 2 }, tokenizer.EosTokenIds);
    }

    [Fact]
    public void BpeUsesStableMergeRanksAndRejectsImplicitSpecialTokens()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteTokenizer(tokenizerPath, new
        {
            added_tokens = new object[]
            {
                new { id = 0, content = "<unk>", special = true },
                new { id = 1, content = "<bos>", special = true },
                new { id = 2, content = "<eos>", special = true },
                new { id = 11, content = "<|user|>", special = true }
            },
            normalizer = new
            {
                type = "Sequence",
                normalizers = new object[] { new { type = "NFKC" }, new { type = "Lowercase" } }
            },
            pre_tokenizer = new { type = "WhitespaceSplit" },
            model = new
            {
                type = "BPE",
                unk_token = "<unk>",
                vocab = new Dictionary<string, int>
                {
                    ["<unk>"] = 0,
                    ["<bos>"] = 1,
                    ["<eos>"] = 2,
                    ["h"] = 3,
                    ["e"] = 4,
                    ["l"] = 5,
                    ["o"] = 6,
                    ["he"] = 7,
                    ["ll"] = 8,
                    ["hell"] = 9,
                    ["hello"] = 10,
                    ["<|user|>"] = 11
                },
                merges = new[] { "h e", "l l", "he ll", "hell o" }
            }
        });
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);

        Assert.Equal(new[] { 1, 10 }, tokenizer.Encode("ＨＥＬＬＯ", addBos: true));
        Assert.Equal(new[] { 11 }, tokenizer.Encode("<|user|>", parseSpecialTokens: true));
        Assert.DoesNotContain(11, tokenizer.Encode("<|user|>"));
        Assert.Equal("hello", tokenizer.Decode([10]));
    }

    [Fact]
    public void ByteLevelRoundTripsUnicodeAndStreamsOnlyCompleteUtf8Characters()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteByteLevelTokenizer(tokenizerPath);
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);
        const string text = "A🙂中\nC#\0\t";

        var encoded = tokenizer.Encode(text);

        Assert.Equal(text, tokenizer.Decode(encoded));
        var emojiTokens = tokenizer.Encode("🙂");
        Assert.Equal(4, emojiTokens.Count);
        var callbacks = new List<string>();
        var incremental = new IncrementalTextDecoder(tokenizer, [], [], callbacks.Add);
        for (var index = 0; index < emojiTokens.Count - 1; index++)
        {
            incremental.AppendToken(emojiTokens[index]);
            Assert.Empty(callbacks);
        }

        incremental.AppendToken(emojiTokens[^1]);
        incremental.Complete();
        Assert.Equal(new[] { "🙂" }, callbacks);
    }

    [Fact]
    public void IncrementalDecoderFiltersTextStopsAcrossTokensAndMultipleStopTokens()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteByteLevelTokenizer(tokenizerPath);
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);
        Assert.True(tokenizer.TryGetTokenId("<eos>", out var eosTokenId));
        var callbacks = new List<string>();
        var decoder = new IncrementalTextDecoder(
            tokenizer,
            [eosTokenId],
            ["END", "HALT"],
            callbacks.Add);

        foreach (var tokenId in tokenizer.Encode("prefixENDtail"))
        {
            decoder.AppendToken(tokenId);
        }

        decoder.AppendToken(eosTokenId);
        decoder.Complete();
        Assert.True(decoder.Stopped);
        Assert.Equal("END", decoder.StopSequence);
        Assert.Null(decoder.StopTokenId);
        Assert.Equal("prefix", decoder.Text);
        Assert.Equal("prefix", string.Concat(callbacks));

        var tokenStopDecoder = new IncrementalTextDecoder(tokenizer, [eosTokenId], []);
        tokenStopDecoder.AppendToken(tokenizer.Encode("A").Single());
        tokenStopDecoder.AppendToken(eosTokenId);
        Assert.True(tokenStopDecoder.Stopped);
        Assert.Equal(eosTokenId, tokenStopDecoder.StopTokenId);
        Assert.Equal("A", tokenStopDecoder.Text);
    }

    [Fact]
    public void GlmPromptUsesModelRoleTokensAndExposesAllRoleStops()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteGlmTokenizer(tokenizerPath);
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);
        var template = new GlmPromptTemplate(tokenizer);

        var prompt = template.BuildChat(
        [
            new ChatTurn("system", "hello"),
            new ChatTurn("user", "hello"),
            new ChatTurn("tool", "result")
        ]);

        Assert.Equal(new[] { 4, 5, 6, 10, 7, 10, 9, 12, 8 }, prompt.TokenIds);
        Assert.Equal(new[] { 2, 6, 7, 8, 9 }, prompt.StopTokenIds.Order().ToArray());
        Assert.Equal(new[] { 4, 5, 10 }, template.BuildCompletion("hello").TokenIds);
        Assert.Equal(new[] { 7 }, tokenizer.Encode("<|user|>", parseSpecialTokens: true));
        Assert.Equal(new[] { 3 }, tokenizer.Encode("<|user|>"));
    }

    [Fact]
    public void Glm4MoeLitePromptMatchesNonThinkingChatTemplate()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteGlm4MoeLiteTokenizer(tokenizerPath);
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);
        var template = new GlmPromptTemplate(tokenizer, GlmModelConfiguration.MoeLiteModelType);

        var prompt = template.BuildChat(
        [
            new ChatTurn("system", "hello"),
            new ChatTurn("user", "hello"),
            new ChatTurn("assistant", "<think>hidden</think>answer"),
            new ChatTurn("tool", "result")
        ]);

        Assert.Equal(
            new[] { 4, 5, 13, 6, 10, 7, 10, 8, 14, 11, 9, 15, 12, 16, 8, 14 },
            prompt.TokenIds);
        Assert.Equal(new[] { 4, 5, 10 }, template.BuildCompletion("hello").TokenIds);
    }

    [Fact]
    public void UnknownTokenizerComponentsFailDuringRead()
    {
        using var root = new TemporaryDirectory();
        var tokenizerPath = Path.Combine(root.Path, "tokenizer.json");
        WriteTokenizer(tokenizerPath, new
        {
            pre_tokenizer = new { type = "UnboundedCustomTokenizer" },
            model = new
            {
                type = "WordLevel",
                unk_token = "<unk>",
                vocab = new Dictionary<string, int> { ["<unk>"] = 0 }
            }
        });

        var exception = Assert.Throws<InvalidDataException>(() => ManagedTokenizer.Read(tokenizerPath));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteByteLevelTokenizer(string path)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["<unk>"] = 0
        };
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            vocabulary.Add(ByteLevelEncoding.EncodeBytes([(byte)value]), value + 1);
        }

        const int eosTokenId = byte.MaxValue + 2;
        vocabulary.Add("<eos>", eosTokenId);
        WriteTokenizer(path, new
        {
            added_tokens = new object[] { new { id = eosTokenId, content = "<eos>", special = true } },
            pre_tokenizer = new { type = "ByteLevel", add_prefix_space = false, use_regex = false },
            decoder = new { type = "ByteLevel" },
            model = new
            {
                type = "BPE",
                unk_token = "<unk>",
                byte_fallback = false,
                vocab = vocabulary,
                merges = Array.Empty<string>()
            }
        });
    }

    private static void WriteGlmTokenizer(string path)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["<pad>"] = 0,
            ["<bos>"] = 1,
            ["<eos>"] = 2,
            ["<unk>"] = 3,
            ["[gMASK]"] = 4,
            ["<sop>"] = 5,
            ["<|system|>"] = 6,
            ["<|user|>"] = 7,
            ["<|assistant|>"] = 8,
            ["<|observation|>"] = 9,
            ["hello"] = 10,
            ["answer"] = 11,
            ["result"] = 12
        };
        var addedTokens = vocabulary
            .Where(static item => item.Value <= 9)
            .Select(static item => new { id = item.Value, content = item.Key, special = true })
            .ToArray();
        WriteTokenizer(path, new
        {
            added_tokens = addedTokens,
            pre_tokenizer = new { type = "WhitespaceSplit" },
            model = new { type = "WordLevel", unk_token = "<unk>", vocab = vocabulary }
        });
    }

    private static void WriteGlm4MoeLiteTokenizer(string path)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["<pad>"] = 0,
            ["<bos>"] = 1,
            ["<eos>"] = 2,
            ["<unk>"] = 3,
            ["[gMASK]"] = 4,
            ["<sop>"] = 5,
            ["<|system|>"] = 6,
            ["<|user|>"] = 7,
            ["<|assistant|>"] = 8,
            ["<|observation|>"] = 9,
            ["hello"] = 10,
            ["answer"] = 11,
            ["result"] = 12,
            ["\n"] = 13,
            ["</think>"] = 14,
            ["<tool_response>"] = 15,
            ["</tool_response>"] = 16
        };
        var addedTokens = vocabulary
            .Where(static item => item.Value <= 9)
            .Select(static item => new { id = item.Value, content = item.Key, special = true })
            .ToArray();
        WriteTokenizer(path, new
        {
            added_tokens = addedTokens,
            model = new { type = "WordLevel", unk_token = "<unk>", vocab = vocabulary }
        });
    }

    private static void WriteTokenizer<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value));
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
