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
