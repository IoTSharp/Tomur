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
