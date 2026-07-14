using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Glm;

internal static class TinyFixtureBundle
{
    public const int SchemaVersion = 1;
    public const string FixtureId = "managed-glm-tiny-f32-v1";
    public const string GeneratorVersion = "1.0.0";
    public const string Seed = "0x6a09e667f3bcc909";

    private const ulong SeedValue = 0x6a09e667f3bcc909UL;
    private const int HiddenSize = 4;
    private const int LayerCount = 1;
    private const int AttentionHeadCount = 1;
    private const int RoutedExpertCount = 3;
    private const int ExpertsPerToken = 2;
    private const int MoeIntermediateSize = 3;
    private const int VocabularySize = 12;
    private const int ContextSize = 16;

    private static readonly string[] RequiredCheckpoints =
    [
        "embedding.lookup",
        "rms_norm.input",
        "rms_norm.output",
        "attention.q_latent",
        "attention.q_normalized",
        "attention.kv_latent",
        "attention.kv_normalized",
        "attention.query",
        "attention.key",
        "attention.value",
        "attention.scores",
        "attention.probabilities",
        "attention.output",
        "router.input",
        "router.scores",
        "router.adjusted_scores",
        "router.weights",
        "router.output",
        "moe.output",
        "layer.output"
    ];

    public static TinyFixtureVerification Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"Fixture output path must not already exist: {target}");
        }

        var parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("Fixture output directory must have a parent directory.", nameof(outputDirectory));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var tensors = CreateTensors();
            var configurationBytes = CreateConfiguration();
            var configurationSha256 = ComputeSha256(configurationBytes);
            var (tensorBytes, tensorManifest) = CreateSafeTensors(tensors);
            var tensorManifestBytes = Serialize(
                tensorManifest,
                TinyFixtureJsonContext.Default.TinyTensorManifest);
            var oracle = TinyFixtureReference.Create(tensors, configurationSha256);
            var oracleBytes = Serialize(oracle, TinyFixtureJsonContext.Default.TinyOracle);

            Write(staging, TinyFixtureFiles.ModelManifest, CreateModelManifest());
            Write(staging, TinyFixtureFiles.Configuration, configurationBytes);
            Write(staging, TinyFixtureFiles.Tokenizer, CreateTokenizer());
            Write(staging, TinyFixtureFiles.Tensors, tensorBytes);
            Write(staging, TinyFixtureFiles.TensorManifest, tensorManifestBytes);
            Write(staging, TinyFixtureFiles.Oracle, oracleBytes);

            var files = TinyFixtureFiles.PayloadFiles
                .Select(fileName => CreateFileEntry(staging, fileName))
                .ToArray();
            var fixtureManifest = new TinyFixtureManifest(
                SchemaVersion,
                FixtureId,
                Seed,
                GeneratorVersion,
                configurationSha256,
                ComputeSha256(tensorManifestBytes),
                ComputeSha256(oracleBytes),
                files);
            Write(
                staging,
                TinyFixtureFiles.FixtureManifest,
                Serialize(fixtureManifest, TinyFixtureJsonContext.Default.TinyFixtureManifest));

            Directory.Move(staging, target);
            return Verify(target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public static TinyFixtureVerification Verify(string fixtureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        var directory = Path.GetFullPath(fixtureDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Fixture directory was not found: {directory}");
        }

        if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Fixture directory must not be a symbolic link.");
        }

        var fixtureManifestPath = Path.Combine(directory, TinyFixtureFiles.FixtureManifest);
        var fixtureManifestInfo = new FileInfo(fixtureManifestPath);
        if (!fixtureManifestInfo.Exists || fixtureManifestInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Fixture manifest is missing or is a symbolic link.");
        }

        var fixtureManifest = ReadJson(
            fixtureManifestPath,
            TinyFixtureJsonContext.Default.TinyFixtureManifest);
        ValidateFixtureManifest(fixtureManifest);
        ValidateDirectoryFileSet(directory);

        foreach (var file in fixtureManifest.Files)
        {
            var path = ResolveFile(directory, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new InvalidDataException($"Fixture file is missing: {file.Path}");
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"Fixture files must not be symbolic links: {file.Path}");
            }

            if (info.Length != file.Length)
            {
                throw new InvalidDataException(
                    $"Fixture file length mismatch for '{file.Path}': expected {file.Length}, found {info.Length}.");
            }

            var actualSha256 = ComputeFileSha256(path);
            if (!string.Equals(file.Sha256, actualSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Fixture checksum mismatch for '{file.Path}'.");
            }
        }

        var configurationPath = Path.Combine(directory, TinyFixtureFiles.Configuration);
        var configurationSha256 = ComputeFileSha256(configurationPath);
        RequireDigest("configuration", fixtureManifest.ConfigurationSha256, configurationSha256);
        RequireDigest(
            "deterministic configuration",
            ComputeSha256(CreateConfiguration()),
            configurationSha256);
        RequireDigest(
            "deterministic model manifest",
            ComputeSha256(CreateModelManifest()),
            ComputeFileSha256(Path.Combine(directory, TinyFixtureFiles.ModelManifest)));
        RequireDigest(
            "deterministic tokenizer",
            ComputeSha256(CreateTokenizer()),
            ComputeFileSha256(Path.Combine(directory, TinyFixtureFiles.Tokenizer)));

        var tensorManifestPath = Path.Combine(directory, TinyFixtureFiles.TensorManifest);
        RequireDigest(
            "tensor manifest",
            fixtureManifest.TensorManifestSha256,
            ComputeFileSha256(tensorManifestPath));
        var tensorManifest = ReadJson(
            tensorManifestPath,
            TinyFixtureJsonContext.Default.TinyTensorManifest);
        ValidateTensorManifest(
            tensorManifest,
            Path.Combine(directory, TinyFixtureFiles.Tensors));

        var oraclePath = Path.Combine(directory, TinyFixtureFiles.Oracle);
        RequireDigest("oracle", fixtureManifest.OracleSha256, ComputeFileSha256(oraclePath));
        var oracle = ReadJson(oraclePath, TinyFixtureJsonContext.Default.TinyOracle);
        ValidateOracle(oracle, configurationSha256);
        var probe = ProbeModelDirectory(directory);
        ValidateProbe(probe, tensorManifest.Tensors.Count);

        return new TinyFixtureVerification(
            FixtureId,
            directory,
            SchemaVersion,
            fixtureManifest.Files.Count + 1,
            tensorManifest.Tensors.Count,
            oracle.Checkpoints.Count);
    }

    public static TinyOracle ReadOracle(string fixtureDirectory)
    {
        Verify(fixtureDirectory);
        return ReadJson(
            Path.Combine(Path.GetFullPath(fixtureDirectory), TinyFixtureFiles.Oracle),
            TinyFixtureJsonContext.Default.TinyOracle);
    }

    private static IReadOnlyList<TinyTensor> CreateTensors()
    {
        var random = new StableRandom(SeedValue);
        var tensors = new List<TinyTensor>();

        AddRandom(tensors, random, "model.embed_tokens.weight", [VocabularySize, HiddenSize], 0.0, 0.35);
        AddRandom(tensors, random, "model.norm.weight", [HiddenSize], 1.0, 0.08);
        AddRandom(tensors, random, "lm_head.weight", [VocabularySize, HiddenSize], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.input_layernorm.weight", [HiddenSize], 1.0, 0.08);
        AddRandom(tensors, random, "model.layers.0.post_attention_layernorm.weight", [HiddenSize], 1.0, 0.08);
        AddRandom(tensors, random, "model.layers.0.self_attn.q_a_proj.weight", [2, HiddenSize], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.self_attn.q_a_layernorm.weight", [2], 1.0, 0.08);
        AddRandom(tensors, random, "model.layers.0.self_attn.q_b_proj.weight", [4, 2], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.self_attn.kv_a_proj_with_mqa.weight", [4, HiddenSize], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.self_attn.kv_a_layernorm.weight", [2], 1.0, 0.08);
        AddRandom(tensors, random, "model.layers.0.self_attn.kv_b_proj.weight", [4, 2], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.self_attn.o_proj.weight", [HiddenSize, 2], 0.0, 0.30);
        AddRandom(tensors, random, "model.layers.0.mlp.gate.weight", [RoutedExpertCount, HiddenSize], 0.0, 0.35);
        tensors.Add(new TinyTensor(
            "model.layers.0.mlp.gate.e_score_correction_bias",
            [RoutedExpertCount],
            [-0.025f, 0.015f, 0.035f]));
        AddRandom(tensors, random, "model.layers.0.mlp.shared_experts.gate_proj.weight", [MoeIntermediateSize, HiddenSize], 0.0, 0.28);
        AddRandom(tensors, random, "model.layers.0.mlp.shared_experts.up_proj.weight", [MoeIntermediateSize, HiddenSize], 0.0, 0.28);
        AddRandom(tensors, random, "model.layers.0.mlp.shared_experts.down_proj.weight", [HiddenSize, MoeIntermediateSize], 0.0, 0.28);

        for (var expert = 0; expert < RoutedExpertCount; expert++)
        {
            var prefix = $"model.layers.0.mlp.experts.{expert}.";
            AddRandom(tensors, random, $"{prefix}gate_proj.weight", [MoeIntermediateSize, HiddenSize], 0.0, 0.32);
            AddRandom(tensors, random, $"{prefix}up_proj.weight", [MoeIntermediateSize, HiddenSize], 0.0, 0.32);
            AddRandom(tensors, random, $"{prefix}down_proj.weight", [HiddenSize, MoeIntermediateSize], 0.0, 0.32);
        }

        return tensors;
    }

    private static void AddRandom(
        ICollection<TinyTensor> tensors,
        StableRandom random,
        string name,
        long[] shape,
        double center,
        double scale)
    {
        var count = checked((int)shape.Aggregate(1L, static (product, value) => checked(product * value)));
        var values = new float[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(center + scale * random.NextSignedUnit());
        }

        tensors.Add(new TinyTensor(name, shape, values));
    }

    private static (byte[] Bytes, TinyTensorManifest Manifest) CreateSafeTensors(
        IReadOnlyList<TinyTensor> tensors)
    {
        var payloads = tensors.Select(CreateTensorPayload).ToArray();
        var entries = new List<TinyTensorManifestEntry>(tensors.Count);
        using var headerStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(headerStream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("__metadata__");
            writer.WriteStartObject();
            writer.WriteString("format", "pt");
            writer.WriteString("fixture_id", FixtureId);
            writer.WriteEndObject();

            long offset = 0;
            for (var index = 0; index < tensors.Count; index++)
            {
                var tensor = tensors[index];
                var payload = payloads[index];
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", "F32");
                writer.WriteStartArray("shape");
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("data_offsets");
                writer.WriteNumberValue(offset);
                writer.WriteNumberValue(checked(offset + payload.Length));
                writer.WriteEndArray();
                writer.WriteEndObject();

                entries.Add(new TinyTensorManifestEntry(
                    tensor.Name,
                    "F32",
                    tensor.Shape,
                    offset,
                    payload.Length,
                    ComputeSha256(payload)));
                offset = checked(offset + payload.Length);
            }

            writer.WriteEndObject();
        }

        var unpaddedHeader = headerStream.ToArray();
        var paddedHeaderLength = checked((unpaddedHeader.Length + 7) & ~7);
        var header = GC.AllocateUninitializedArray<byte>(paddedHeaderLength);
        unpaddedHeader.CopyTo(header, 0);
        header.AsSpan(unpaddedHeader.Length).Fill((byte)' ');
        using var file = new MemoryStream(checked(sizeof(ulong) + header.Length + payloads.Sum(static item => item.Length)));
        Span<byte> headerLength = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(headerLength, (ulong)header.Length);
        file.Write(headerLength);
        file.Write(header);
        foreach (var payload in payloads)
        {
            file.Write(payload);
        }

        return (
            file.ToArray(),
            new TinyTensorManifest(SchemaVersion, FixtureId, "safetensors-f32", entries));
    }

    private static byte[] CreateTensorPayload(TinyTensor tensor)
    {
        var payload = GC.AllocateUninitializedArray<byte>(checked(tensor.Values.Length * sizeof(float)));
        for (var index = 0; index < tensor.Values.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                payload.AsSpan(index * sizeof(float), sizeof(float)),
                tensor.Values[index]);
        }

        return payload;
    }

    private static byte[] CreateModelManifest()
        => Encoding.UTF8.GetBytes("""
            {
              "schema_version": 1,
              "provider": "managed-glm",
              "architecture": "glm_moe_dsa",
              "display_name": "Managed GLM tiny F32 oracle fixture",
              "config": "config.json",
              "tokenizer": "tokenizer.json",
              "tensor_pattern": "*.safetensors",
              "quantization": "f32",
              "capabilities": ["completion", "chat"]
            }
            """ + "\n");

    private static byte[] CreateConfiguration()
        => Encoding.UTF8.GetBytes("""
            {
              "architectures": ["GlmMoeDsaForCausalLM"],
              "model_type": "glm_moe_dsa",
              "hidden_size": 4,
              "num_hidden_layers": 1,
              "num_attention_heads": 1,
              "n_routed_experts": 3,
              "num_experts_per_tok": 2,
              "moe_intermediate_size": 3,
              "intermediate_size": 6,
              "first_k_dense_replace": 0,
              "q_lora_rank": 2,
              "kv_lora_rank": 2,
              "qk_nope_head_dim": 2,
              "qk_rope_head_dim": 2,
              "v_head_dim": 2,
              "n_shared_experts": 1,
              "vocab_size": 12,
              "bos_token_id": 1,
              "eos_token_id": 2,
              "pad_token_id": 0,
              "max_position_embeddings": 16,
              "n_group": 1,
              "topk_group": 1,
              "norm_topk_prob": true,
              "rms_norm_eps": 0.00001,
              "routed_scaling_factor": 1.0,
              "rope_parameters": { "rope_theta": 10000.0 },
              "tie_word_embeddings": false
            }
            """ + "\n");

    private static byte[] CreateTokenizer()
        => Encoding.UTF8.GetBytes("""
            {
              "version": "1.0",
              "truncation": null,
              "padding": null,
              "added_tokens": [
                { "id": 0, "content": "<pad>", "special": true },
                { "id": 1, "content": "<bos>", "special": true },
                { "id": 2, "content": "<eos>", "special": true },
                { "id": 3, "content": "<unk>", "special": true }
              ],
              "pre_tokenizer": { "type": "WhitespaceSplit" },
              "model": {
                "type": "WordLevel",
                "unk_token": "<unk>",
                "vocab": {
                  "<pad>": 0,
                  "<bos>": 1,
                  "<eos>": 2,
                  "<unk>": 3,
                  "hello": 4,
                  "world": 5,
                  "Tomur": 6,
                  "本地": 7,
                  "AI": 8,
                  "!": 9,
                  "C#": 10,
                  "🙂": 11
                }
              }
            }
            """ + "\n");

    private static void ValidateFixtureManifest(TinyFixtureManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion ||
            !string.Equals(manifest.FixtureId, FixtureId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Seed, Seed, StringComparison.Ordinal) ||
            !string.Equals(manifest.GeneratorVersion, GeneratorVersion, StringComparison.Ordinal) ||
            manifest.Files is null)
        {
            throw new InvalidDataException("Fixture manifest identity or schema is not supported.");
        }

        var expected = new HashSet<string>(TinyFixtureFiles.PayloadFiles, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) ||
                !actual.Add(file.Path) || file.Length <= 0 || !IsSha256(file.Sha256))
            {
                throw new InvalidDataException($"Fixture manifest file entry is invalid: {file?.Path}");
            }
        }

        if (!actual.SetEquals(expected) ||
            !IsSha256(manifest.ConfigurationSha256) ||
            !IsSha256(manifest.TensorManifestSha256) ||
            !IsSha256(manifest.OracleSha256))
        {
            throw new InvalidDataException("Fixture manifest file set or digest is invalid.");
        }
    }

    private static void ValidateTensorManifest(TinyTensorManifest manifest, string tensorPath)
    {
        if (manifest.SchemaVersion != SchemaVersion ||
            !string.Equals(manifest.FixtureId, FixtureId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Format, "safetensors-f32", StringComparison.Ordinal) ||
            manifest.Tensors is null ||
            manifest.Tensors.Count == 0)
        {
            throw new InvalidDataException("Tensor manifest identity, schema or format is invalid.");
        }

        var catalog = SafeTensorCatalog.Read([tensorPath]);
        var expectedTensors = CreateTensors().ToDictionary(static tensor => tensor.Name, StringComparer.Ordinal);
        if (catalog.Count != manifest.Tensors.Count || manifest.Tensors.Count != expectedTensors.Count)
        {
            throw new InvalidDataException("Tensor manifest count does not match the safetensors header.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var dataStart = catalog.Items.Min(static tensor => tensor.Offset);
        using var stream = File.OpenRead(tensorPath);
        foreach (var entry in manifest.Tensors)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
                entry.Shape is null || entry.Shape.Count == 0 ||
                entry.DataOffset < 0 || entry.ByteLength <= 0 ||
                !names.Add(entry.Name) ||
                !string.Equals(entry.DataType, "F32", StringComparison.Ordinal) ||
                !IsSha256(entry.Sha256) ||
                !expectedTensors.TryGetValue(entry.Name, out var expectedTensor) ||
                !catalog.TryGet(entry.Name, out var tensor))
            {
                throw new InvalidDataException($"Tensor manifest entry is invalid: {entry.Name}");
            }

            if (!tensor.LogicalShape.SequenceEqual(entry.Shape) ||
                !expectedTensor.Shape.SequenceEqual(entry.Shape) ||
                tensor.PhysicalLength != entry.ByteLength ||
                expectedTensor.ByteLength != entry.ByteLength ||
                tensor.Offset - dataStart != entry.DataOffset)
            {
                throw new InvalidDataException($"Tensor manifest metadata mismatch for '{entry.Name}'.");
            }

            if (tensor.PhysicalLength > int.MaxValue)
            {
                throw new InvalidDataException($"Tiny fixture tensor is too large: {entry.Name}");
            }

            var payload = GC.AllocateUninitializedArray<byte>((int)tensor.PhysicalLength);
            stream.Position = tensor.Offset;
            stream.ReadExactly(payload);
            RequireDigest($"tensor '{entry.Name}'", entry.Sha256, ComputeSha256(payload));
            RequireDigest(
                $"tensor '{entry.Name}' deterministic payload",
                ComputeSha256(CreateTensorPayload(expectedTensor)),
                entry.Sha256);
        }
    }

    private static void ValidateOracle(TinyOracle oracle, string configurationSha256)
    {
        if (oracle.SchemaVersion != SchemaVersion ||
            !string.Equals(oracle.FixtureId, FixtureId, StringComparison.Ordinal) ||
            oracle.Generator is null ||
            oracle.ModelConfiguration is null ||
            !string.Equals(oracle.Generator.Name, "managed-glm-scalar-reference", StringComparison.Ordinal) ||
            !string.Equals(oracle.Generator.Version, GeneratorVersion, StringComparison.Ordinal) ||
            !string.Equals(oracle.Generator.Arithmetic, "F32 checkpoints with F64 accumulators", StringComparison.Ordinal) ||
            !string.Equals(oracle.Generator.Rounding, "IEEE 754 round-to-nearest, ties-to-even", StringComparison.Ordinal) ||
            !string.Equals(oracle.ModelConfiguration.Sha256, configurationSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Oracle identity, schema, generator or configuration digest is invalid.");
        }

        if (oracle.Tolerances is null ||
            oracle.Tokenization is null ||
            oracle.Checkpoints is null ||
            oracle.Router is null ||
            oracle.TeacherForcing is null ||
            oracle.GreedyDecode is null ||
            oracle.ModelConfiguration.HiddenSize != HiddenSize ||
            oracle.ModelConfiguration.LayerCount != LayerCount ||
            oracle.ModelConfiguration.AttentionHeadCount != AttentionHeadCount ||
            oracle.ModelConfiguration.RoutedExpertCount != RoutedExpertCount ||
            oracle.ModelConfiguration.ExpertsPerToken != ExpertsPerToken ||
            oracle.ModelConfiguration.VocabularySize != VocabularySize ||
            oracle.ModelConfiguration.ContextSize != ContextSize ||
            oracle.Tolerances.Absolute <= 0 || oracle.Tolerances.Relative <= 0)
        {
            throw new InvalidDataException("Oracle model summary or tolerances are invalid.");
        }

        var checkpointNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var checkpoint in oracle.Checkpoints)
        {
            if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Name) ||
                checkpoint.Shape is null || checkpoint.Values is null || checkpoint.Shape.Count == 0)
            {
                throw new InvalidDataException("Oracle contains an incomplete checkpoint.");
            }

            long elementCount = 1;
            foreach (var dimension in checkpoint.Shape)
            {
                if (dimension <= 0)
                {
                    throw new InvalidDataException($"Oracle checkpoint shape is invalid: {checkpoint.Name}");
                }

                elementCount = checked(elementCount * dimension);
            }

            if (!checkpointNames.Add(checkpoint.Name) ||
                !string.Equals(checkpoint.DataType, "F32", StringComparison.Ordinal) ||
                elementCount != checkpoint.Values.Count ||
                checkpoint.Values.Any(static value => !float.IsFinite(value)))
            {
                throw new InvalidDataException($"Oracle checkpoint is invalid: {checkpoint.Name}");
            }
        }

        if (!float.IsFinite(oracle.Tolerances.Absolute) ||
            !float.IsFinite(oracle.Tolerances.Relative) ||
            RequiredCheckpoints.Any(name => !checkpointNames.Contains(name)) ||
            oracle.Tokenization.Count == 0 ||
            oracle.Tokenization.Any(static item =>
                item is null || string.IsNullOrEmpty(item.Text) || item.TokenIds is null || !item.AddBos) ||
            oracle.Tokenization.SelectMany(static item => item.TokenIds).Any(static id => id < 0 || id >= VocabularySize) ||
            oracle.Router.ExpertIds is null || oracle.Router.Weights is null ||
            oracle.Router.ExpertIds.Count != ExpertsPerToken ||
            oracle.Router.Weights.Count != ExpertsPerToken ||
            oracle.Router.ExpertIds.Any(static id => id < 0 || id >= RoutedExpertCount) ||
            oracle.Router.ExpertIds.Distinct().Count() != ExpertsPerToken ||
            oracle.Router.Weights.Any(static value => !float.IsFinite(value) || value <= 0) ||
            Math.Abs(oracle.Router.Weights.Sum(static value => (double)value) - 1.0) > 1e-5 ||
            oracle.TeacherForcing.Count == 0 ||
            oracle.TeacherForcing.Any(step =>
                step is null || step.Logits is null || step.Logits.Count != VocabularySize ||
                step.Logits.Any(static value => !float.IsFinite(value))) ||
            oracle.GreedyDecode.PromptTokenIds is null || oracle.GreedyDecode.TokenIds is null ||
            oracle.GreedyDecode.MaxNewTokens <= 0 ||
            oracle.GreedyDecode.TokenIds.Count != oracle.GreedyDecode.MaxNewTokens ||
            oracle.GreedyDecode.PromptTokenIds.Any(static id => id < 0 || id >= VocabularySize) ||
            oracle.GreedyDecode.TokenIds.Any(static id => id < 0 || id >= VocabularySize))
        {
            throw new InvalidDataException("Oracle tokenization, routing, teacher-forcing or greedy data is invalid.");
        }

        if (oracle.TeacherForcing.Count != oracle.GreedyDecode.PromptTokenIds.Count)
        {
            throw new InvalidDataException("Oracle teacher-forcing sequence does not match the greedy prompt.");
        }

        for (var position = 0; position < oracle.TeacherForcing.Count; position++)
        {
            var teacherStep = oracle.TeacherForcing[position];
            if (teacherStep.Position != position ||
                teacherStep.InputTokenId != oracle.GreedyDecode.PromptTokenIds[position])
            {
                throw new InvalidDataException("Oracle teacher-forcing positions or token IDs are inconsistent.");
            }
        }
    }

    private static void RequireDigest(string label, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fixture {label} checksum mismatch.");
        }
    }

    private static void ValidateDirectoryFileSet(string directory)
    {
        var expected = new HashSet<string>(TinyFixtureFiles.PayloadFiles, StringComparer.Ordinal)
        {
            TinyFixtureFiles.FixtureManifest
        };
        var actual = Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path) ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("Fixture directory contains missing or untracked entries.");
        }
    }

    private static ModelProbe ProbeModelDirectory(string directory)
    {
        var manifestPath = Path.Combine(directory, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            FixtureId,
            "Managed GLM tiny F32 oracle fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);
        return ModelDirectoryProbe.Read(descriptor, ManagedGlmProvider.ProviderId);
    }

    private static void ValidateProbe(ModelProbe probe, int expectedTensorCount)
    {
        var configuration = probe.Configuration;
        if (probe.Tensors.Count != expectedTensorCount ||
            configuration.HiddenSize != HiddenSize ||
            configuration.LayerCount != LayerCount ||
            configuration.AttentionHeadCount != AttentionHeadCount ||
            configuration.RoutedExpertCount != RoutedExpertCount ||
            configuration.ExpertsPerToken != ExpertsPerToken ||
            configuration.MoeIntermediateSize != MoeIntermediateSize ||
            configuration.FirstMoeLayer != 0 ||
            configuration.QueryLoraRank != 2 ||
            configuration.KeyValueLoraRank != 2 ||
            configuration.QueryKeyNopeHeadSize != 2 ||
            configuration.QueryKeyRopeHeadSize != 2 ||
            configuration.ValueHeadSize != 2 ||
            configuration.SharedExpertCount != 1 ||
            configuration.VocabularySize != VocabularySize ||
            configuration.ExpertGroupCount != 1 ||
            configuration.ExpertGroupsPerToken != 1)
        {
            throw new InvalidDataException("Fixture model probe does not match the M2 tiny configuration.");
        }
    }

    private static string ResolveFile(string directory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            !string.Equals(Path.GetFileName(relativePath), relativePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fixture manifest path must be a file name: {relativePath}");
        }

        return Path.Combine(directory, relativePath);
    }

    private static TinyFixtureFile CreateFileEntry(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return new TinyFixtureFile(fileName, new FileInfo(path).Length, ComputeFileSha256(path));
    }

    private static T ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new InvalidDataException($"JSON file is empty: {path}");
    }

    private static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static void Write(string directory, string fileName, byte[] bytes)
        => File.WriteAllBytes(Path.Combine(directory, fileName), bytes);

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class StableRandom
    {
        private ulong state;

        public StableRandom(ulong seed)
        {
            state = seed;
        }

        public double NextSignedUnit()
        {
            state = unchecked(state + 0x9e3779b97f4a7c15UL);
            var value = state;
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
            value ^= value >> 31;
            var fraction = (value >> 11) * (1.0 / (1UL << 53));
            return fraction * 2.0 - 1.0;
        }
    }
}
