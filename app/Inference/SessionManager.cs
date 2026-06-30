using Tomur.Runtime;

namespace Tomur.Inference;

public sealed class SessionManager : IDisposable
{
    private static readonly object BackendGate = new();
    private static bool backendInitialized;

    private readonly LlamaImportResolver importResolver;
    private readonly Tomur.Native.INativeLibraryResolver libraryResolver;
    private readonly object gate = new();
    private LlamaNativeSession? currentSession;
    private string? currentModelId;
    private string? currentModelPath;
    private int currentContextSize;
    private bool currentEmbeddings;
    private bool disposed;

    public SessionManager(LlamaImportResolver importResolver, Tomur.Native.INativeLibraryResolver libraryResolver)
    {
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
    }

    internal CompletionResult Generate(
        LocalModelDescriptor model,
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (gate)
        {
            var session = GetOrLoadCore(model, embeddings: false, options.ContextSize);
            return session.Generate(prompt, options, cancellationToken, onToken);
        }
    }

    internal EmbeddingResult Embed(
        LocalModelDescriptor model,
        string input,
        CompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (gate)
        {
            var session = GetOrLoadCore(model, embeddings: true, options.ContextSize);
            return session.Embed(input, options, cancellationToken);
        }
    }

    private LlamaNativeSession GetOrLoadCore(LocalModelDescriptor model, bool embeddings, int contextSize)
    {
        ArgumentNullException.ThrowIfNull(model);

        ObjectDisposedException.ThrowIf(disposed, this);
        var effectiveContextSize = Math.Clamp(contextSize, 512, 131072);

        if (currentSession is not null &&
            string.Equals(currentModelId, model.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentModelPath, model.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
            currentContextSize == effectiveContextSize &&
            currentEmbeddings == embeddings)
        {
            return currentSession;
        }

        currentSession?.Dispose();
        currentSession = null;
        currentModelId = null;
        currentModelPath = null;
        currentContextSize = 0;
        currentEmbeddings = false;

        EnsureBackendInitialized();
        try
        {
            currentSession = new LlamaNativeSession(
                model.Id,
                model.AbsolutePath,
                contextSize: effectiveContextSize,
                gpuLayers: 0,
                embeddings: embeddings);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InferenceException(
                "native_runtime_unavailable",
                $"The llama.cpp native runtime could not be loaded: {exception.Message}",
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Run tomur doctor to inspect native runtime status."
                ],
                exception);
        }

        currentModelId = model.Id;
        currentModelPath = model.AbsolutePath;
        currentContextSize = effectiveContextSize;
        currentEmbeddings = embeddings;
        return currentSession;
    }

    public void Unload()
    {
        lock (gate)
        {
            currentSession?.Dispose();
            currentSession = null;
            currentModelId = null;
            currentModelPath = null;
            currentContextSize = 0;
            currentEmbeddings = false;
        }
    }

    public SessionSnapshot GetSnapshot()
    {
        lock (gate)
        {
            if (currentSession is null)
            {
                return new SessionSnapshot(
                    Loaded: false,
                    ModelId: null,
                    ModelPath: null,
                    Mode: null,
                    LoadedAt: null,
                    RequestCount: 0,
                    PromptTokens: 0,
                    CompletionTokens: 0,
                    Diagnostics: ["no active llama session"]);
            }

            return currentSession.GetSnapshot();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            currentSession?.Dispose();
            currentSession = null;
            currentContextSize = 0;
            currentEmbeddings = false;
            disposed = true;
        }
    }

    private void EnsureBackendInitialized()
    {
        importResolver.Register();

        lock (BackendGate)
        {
            if (backendInitialized)
            {
                return;
            }

            try
            {
                TryLoadDynamicBackends();
                LlamaNativeMethods.BackendInit();
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new InferenceException(
                    "native_runtime_unavailable",
                    $"The llama.cpp native runtime could not be initialized: {exception.Message}",
                    [
                        "Run tomur native prepare to extract or repair the managed runtime bundle.",
                        "Run tomur doctor to inspect native runtime status."
                    ],
                    exception);
            }

            backendInitialized = true;
        }
    }

    private void TryLoadDynamicBackends()
    {
        var resolution = libraryResolver.Resolve("llama", "llama");
        if (!resolution.Exists || string.IsNullOrWhiteSpace(resolution.RuntimeRoot))
        {
            return;
        }

        try
        {
            LlamaNativeMethods.GgmlBackendLoadAllFromPath(resolution.RuntimeRoot);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }
}
