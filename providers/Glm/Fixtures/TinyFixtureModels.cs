using System.Text.Json.Serialization;
using Tomur.Providers;

namespace Tomur.Providers.Glm;

internal static class TinyFixtureFiles
{
    public const string FixtureManifest = "fixture.manifest.json";
    public const string ModelManifest = "model.tomur.json";
    public const string Configuration = "config.json";
    public const string Tokenizer = "tokenizer.json";
    public const string Tensors = "model.safetensors";
    public const string TensorManifest = "tensors.manifest.json";
    public const string Oracle = "oracle.json";

    public static readonly string[] PayloadFiles =
    [
        ModelManifest,
        Configuration,
        Tokenizer,
        Tensors,
        TensorManifest,
        Oracle
    ];
}

internal sealed record TinyFixtureManifest(
    int SchemaVersion,
    string FixtureId,
    string Seed,
    string GeneratorVersion,
    string ConfigurationSha256,
    string TensorManifestSha256,
    string OracleSha256,
    IReadOnlyList<TinyFixtureFile> Files);

internal sealed record TinyFixtureFile(
    string Path,
    long Length,
    string Sha256);

internal sealed record TinyTensorManifest(
    int SchemaVersion,
    string FixtureId,
    string Format,
    IReadOnlyList<TinyTensorManifestEntry> Tensors);

internal sealed record TinyTensorManifestEntry(
    string Name,
    string DataType,
    IReadOnlyList<long> Shape,
    long DataOffset,
    long ByteLength,
    string Sha256);

internal sealed record TinyOracle(
    int SchemaVersion,
    string FixtureId,
    TinyOracleGenerator Generator,
    TinyOracleConfiguration ModelConfiguration,
    TinyOracleTolerance Tolerances,
    IReadOnlyList<TinyTokenizationCase> Tokenization,
    IReadOnlyList<TinyOracleCheckpoint> Checkpoints,
    TinyRouterOracle Router,
    IReadOnlyList<TinyTeacherForcingStep> TeacherForcing,
    TinyGreedyOracle GreedyDecode)
{
    public TinyOracleCheckpoint GetCheckpoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Checkpoints.FirstOrDefault(
                checkpoint => string.Equals(checkpoint.Name, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Oracle checkpoint was not found: {name}");
    }
}

internal sealed record TinyOracleGenerator(
    string Name,
    string Version,
    string Arithmetic,
    string Rounding);

internal sealed record TinyOracleConfiguration(
    string Sha256,
    int HiddenSize,
    int LayerCount,
    int AttentionHeadCount,
    int RoutedExpertCount,
    int ExpertsPerToken,
    int VocabularySize,
    int ContextSize);

internal sealed record TinyOracleTolerance(
    float Absolute,
    float Relative);

internal sealed record TinyTokenizationCase(
    string Text,
    bool AddBos,
    IReadOnlyList<int> TokenIds);

internal sealed record TinyOracleCheckpoint(
    string Name,
    string DataType,
    IReadOnlyList<int> Shape,
    IReadOnlyList<float> Values);

internal sealed record TinyRouterOracle(
    IReadOnlyList<int> ExpertIds,
    IReadOnlyList<float> Weights);

internal sealed record TinyTeacherForcingStep(
    int Position,
    int InputTokenId,
    IReadOnlyList<float> Logits);

internal sealed record TinyGreedyOracle(
    IReadOnlyList<int> PromptTokenIds,
    int MaxNewTokens,
    IReadOnlyList<int> TokenIds);

internal sealed record TinyFixtureVerification(
    string FixtureId,
    string Directory,
    int SchemaVersion,
    int FileCount,
    int TensorCount,
    int OracleCheckpointCount)
{
    public ModelFixtureResult ToResult(string providerId)
        => new(
            providerId,
            FixtureId,
            Directory,
            SchemaVersion,
            FileCount,
            TensorCount,
            OracleCheckpointCount);
}

internal sealed record TinyTensor(
    string Name,
    IReadOnlyList<long> Shape,
    float[] Values)
{
    public long ByteLength => checked((long)Values.Length * sizeof(float));
}

[JsonSerializable(typeof(TinyFixtureManifest))]
[JsonSerializable(typeof(TinyTensorManifest))]
[JsonSerializable(typeof(TinyOracle))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true)]
internal sealed partial class TinyFixtureJsonContext : JsonSerializerContext;
