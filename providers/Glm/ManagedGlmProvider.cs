using System.Text.Json;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Glm;

public sealed class ManagedGlmProvider : ITextGenerationProvider
{
    public const string ProviderId = "managed-glm";

    public string Id => ProviderId;

    public bool CanHandle(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase) &&
            ModelProviderManifestReader.TryRead(model.AbsolutePath, out var manifest, out _) &&
            manifest is not null &&
            string.Equals(manifest.Provider, ProviderId, StringComparison.OrdinalIgnoreCase);
    }

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var probe = ModelDirectoryProbe.Read(model, ProviderId);
            return new ManagedGlmSession(model, options, probe);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            throw new InferenceException(
                "managed_model_invalid",
                $"The managed GLM model could not be prepared: {exception.Message}",
                [
                    $"Verify {ModelProviderManifest.FileName}, config.json, tokenizer.json and every safetensors shard.",
                    "Verify that all required dense, attention, router, shared-expert and routed-expert tensors are present.",
                    "Do not mark a partially downloaded or failed checksum bundle as installed."
                ],
                exception);
        }
    }
}

internal sealed class ManagedGlmSession : ITextGenerationSession
{
    private readonly LocalModelDescriptor model;
    private readonly ModelSessionOptions options;
    private readonly ModelProbe probe;
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private long requestCount;
    private bool disposed;

    public ManagedGlmSession(
        LocalModelDescriptor model,
        ModelSessionOptions options,
        ModelProbe probe)
    {
        this.model = model;
        this.options = options;
        this.probe = probe;
    }

    public string ProviderId => ManagedGlmProvider.ProviderId;

    public CompletionResult Generate(
        string prompt,
        CompletionOptions options,
        CancellationToken cancellationToken,
        Action<string>? onToken = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref requestCount);

        throw new InferenceException(
            "managed_forward_not_ready",
            "The managed GLM provider validated the model metadata and tensor index, but forward execution is not connected yet.",
            [
                $"Provider: {ProviderId}; architecture: {probe.Manifest.Architecture}.",
                $"Indexed {probe.Tensors.Count} tensors from {probe.TensorFileCount} files ({probe.Tensors.TotalPayloadBytes} payload bytes).",
                "Use an existing llama.cpp-compatible model for inference until the managed kernels pass the tiny-model oracle.",
                "Do not treat metadata validation as successful inference."
            ]);
    }

    public SessionSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new SessionSnapshot(
            Loaded: false,
            ModelId: model.Id,
            ModelPath: model.AbsolutePath,
            Mode: "managed-glm-probe",
            LoadedAt: createdAt,
            RequestCount: Interlocked.Read(ref requestCount),
            PromptTokens: 0,
            CompletionTokens: 0,
            Diagnostics:
            [
                $"provider: {ProviderId}",
                $"context limit requested: {this.options.ContextSize}",
                $"layers: {probe.Configuration.LayerCount}",
                $"routed experts: {probe.Configuration.RoutedExpertCount}",
                $"tensor files: {probe.TensorFileCount}",
                $"indexed tensors: {probe.Tensors.Count}",
                "forward execution is not connected"
            ]);
    }

    public void Dispose()
    {
        disposed = true;
    }
}
