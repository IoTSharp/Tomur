using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Providers;

public sealed class ModelSessionOptions
{
    public ModelSessionOptions(int contextSize)
    {
        ContextSize = contextSize;
    }

    public int ContextSize { get; }
}

public interface ITextGenerationProvider
{
    string Id { get; }

    bool CanHandle(LocalModelDescriptor model);

    ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options);
}

public interface IModelFixtureProvider : ITextGenerationProvider
{
    ModelFixtureResult GenerateFixture(string outputDirectory);

    ModelFixtureResult VerifyFixture(string fixtureDirectory);
}

public interface IModelReadinessProvider : ITextGenerationProvider
{
    ModelPreparationResult InspectModel(LocalModelDescriptor model, ModelSessionOptions options);
}

public sealed record ModelPreparationResult(
    string ProviderId,
    string Architecture,
    string Quantization,
    string QuantizationLayout,
    int ContextSize,
    int TensorFileCount,
    int TensorCount,
    long ResidentBytes,
    long KvBytes,
    long ScratchBytes,
    long ExpertCacheBytes,
    long RequiredBytes,
    long AvailableBytes,
    IReadOnlyList<string> Diagnostics);

public sealed record ModelReadinessDiagnostic(
    string Code,
    string Message,
    IReadOnlyList<string> Actions);

public sealed record ModelReadinessStatus(
    string ModelId,
    string? ProviderId,
    string Architecture,
    string Quantization,
    string? QuantizationLayout,
    string Status,
    bool ProviderDiscovered,
    bool MetadataValid,
    bool AssetsComplete,
    bool ForwardVerified,
    bool SessionLoaded,
    int? ContextSize,
    int? TensorFileCount,
    int? TensorCount,
    long? ResidentBytes,
    long? KvBytes,
    long? ScratchBytes,
    long? ExpertCacheBytes,
    long? RequiredBytes,
    long? AvailableBytes,
    IReadOnlyList<ModelReadinessDiagnostic> Diagnostics);

public sealed record ModelFixtureResult(
    string ProviderId,
    string FixtureId,
    string Directory,
    int SchemaVersion,
    int FileCount,
    int TensorCount,
    int OracleCheckpointCount);

public interface ITextGenerationSession : IDisposable
{
    string ProviderId { get; }

    CompletionResult Generate(
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null);

    SessionSnapshot GetSnapshot();
}

public interface IChatGenerationSession : ITextGenerationSession
{
    CompletionResult GenerateChat(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null);
}
