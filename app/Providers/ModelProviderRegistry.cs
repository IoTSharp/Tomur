using System.Text.Json.Serialization;
using Tomur.Inference;
using Tomur.Providers.Glm;
using Tomur.Providers.Olmoe;
using Tomur.Runtime;

namespace Tomur.Providers;

public sealed record ModelProviderLoadDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path = null);

public sealed record ModelProviderInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("path")] string Path);

public sealed record ModelProviderStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("dynamic_loading_supported")] bool DynamicLoadingSupported,
    [property: JsonPropertyName("search_directories")] IReadOnlyList<string> SearchDirectories,
    [property: JsonPropertyName("loaded")] IReadOnlyList<ModelProviderInfo> Loaded,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ModelProviderLoadDiagnostic> Diagnostics);

public sealed class ModelProviderRegistry : IDisposable
{
    private readonly IReadOnlyList<ITextGenerationProvider> textProviders;

    internal ModelProviderRegistry(
        IReadOnlyList<ITextGenerationProvider> textProviders,
        bool dynamicLoadingSupported,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<ModelProviderInfo> loadedProviders,
        IReadOnlyList<ModelProviderLoadDiagnostic> diagnostics)
    {
        this.textProviders = textProviders;
        Diagnostics = diagnostics;
        Status = new ModelProviderStatus(
            ResolveStatus(loadedProviders, diagnostics),
            dynamicLoadingSupported,
            searchDirectories,
            loadedProviders,
            diagnostics);
    }

    public IReadOnlyList<ModelProviderLoadDiagnostic> Diagnostics { get; }

    public ModelProviderStatus Status { get; }

    public static ModelProviderRegistry CreateDefault()
    {
        ITextGenerationProvider[] providers =
        [
            new ManagedGlmProvider(),
            new ManagedOlmoeProvider()
        ];
        var loadedProviders = providers
            .Select(static provider => CreateProviderInfo(provider))
            .ToArray();
        return new ModelProviderRegistry(
            providers,
            dynamicLoadingSupported: false,
            searchDirectories: [],
            loadedProviders,
            diagnostics: []);
    }

    public ITextGenerationProvider? FindTextProvider(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var provider in textProviders)
        {
            try
            {
                if (provider.CanHandle(model))
                {
                    return provider;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
                {
                    var manifestValid = ModelProviderManifestReader.TryRead(
                        model.AbsolutePath,
                        out _,
                        out var manifestError);
                    throw new InferenceException(
                        manifestValid ? "managed_provider_probe_failed" : "managed_model_invalid",
                        manifestValid
                            ? $"Managed provider '{provider.Id}' failed while probing the model: {exception.Message}"
                            : manifestError ?? $"Managed model manifest is invalid: {exception.Message}",
                        manifestValid
                            ? ["Verify the provider assembly version and the model provider manifest."]
                            : ["Repair or reinstall the managed model manifest before retrying."],
                        exception);
                }
            }
        }

        return null;
    }

    public ModelReadinessStatus InspectModel(
        LocalModelDescriptor model,
        SessionSnapshot? session = null,
        int contextSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sessionLoaded = session is { Loaded: true } &&
            string.Equals(session.ModelId, model.Id, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(session.ModelPath) ||
             string.Equals(session.ModelPath, model.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        var forwardVerified = sessionLoaded && session!.RequestCount > 0;

        if (!string.Equals(model.Format, "managed-model", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelReadinessStatus(
                model.Id,
                "llama.cpp",
                model.Family,
                model.QuantizationLevel,
                null,
                sessionLoaded ? "loaded" : "ready",
                true,
                true,
                true,
                forwardVerified,
                sessionLoaded,
                sessionLoaded ? session!.ContextSize : null,
                null,
                null,
                sessionLoaded ? session!.ResidentBytes : null,
                sessionLoaded ? session!.KvBytes : null,
                sessionLoaded ? session!.ScratchBytes : null,
                sessionLoaded ? session!.ExpertCacheBytes : null,
                null,
                null,
                []);
        }

        if (!ModelProviderManifestReader.TryRead(model.AbsolutePath, out var manifest, out var manifestError) ||
            manifest is null)
        {
            return CreateFailedModelStatus(
                model,
                null,
                providerDiscovered: false,
                metadataValid: false,
                sessionLoaded,
                forwardVerified,
                "managed_model_invalid",
                manifestError ?? "Managed model provider manifest is invalid.",
                ["Repair or reinstall the managed model manifest before retrying."]);
        }

        var provider = textProviders.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, manifest.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return CreateFailedModelStatus(
                model,
                manifest,
                providerDiscovered: false,
                metadataValid: true,
                sessionLoaded,
                forwardVerified,
                "managed_provider_unavailable",
                $"Managed provider '{manifest.Provider}' is not loaded.",
                [
                    "Verify that the model manifest selects a provider compiled into this Tomur build.",
                    "Install a Tomur build that includes the required managed model provider."
                ]);
        }

        try
        {
            var preparation = provider is IModelReadinessProvider readinessProvider
                ? readinessProvider.InspectModel(
                    model,
                    new ModelSessionOptions(Math.Clamp(contextSize, 1, 131072)))
                : InspectGenericModel(model, manifest, provider.Id, contextSize);
            var fitsMemory = preparation.RequiredBytes <= preparation.AvailableBytes;
            var diagnostics = preparation.Diagnostics
                .Select(static message => new ModelReadinessDiagnostic(
                    "managed_model_probe_detail",
                    message,
                    []))
                .ToList();
            if (!fitsMemory)
            {
                diagnostics.Add(new ModelReadinessDiagnostic(
                    "managed_model_memory_budget_exceeded",
                    $"The model requires {preparation.RequiredBytes} bytes, but {preparation.AvailableBytes} bytes are currently available.",
                    [
                        "Reduce the requested context size.",
                        "Unload the active model and other memory-heavy applications before retrying."
                    ]));
            }

            var status = !fitsMemory
                ? "memory_limited"
                : !sessionLoaded
                    ? "ready_unverified"
                    : forwardVerified
                        ? "loaded"
                        : session!.Busy
                            ? "warming"
                            : "loaded_unverified";
            if (!forwardVerified)
            {
                diagnostics.Add(new ModelReadinessDiagnostic(
                    "managed_forward_unverified",
                    sessionLoaded
                        ? "The managed model session is loaded, but no generation request has completed."
                        : "Model metadata and assets are valid, but a generation request has not completed yet.",
                    ["Complete a bounded generation smoke before treating the model as conversation-ready."]));
            }

            return new ModelReadinessStatus(
                model.Id,
                preparation.ProviderId,
                preparation.Architecture,
                preparation.Quantization,
                preparation.QuantizationLayout,
                status,
                true,
                true,
                true,
                forwardVerified,
                sessionLoaded,
                preparation.ContextSize,
                preparation.TensorFileCount,
                preparation.TensorCount,
                preparation.ResidentBytes,
                preparation.KvBytes,
                preparation.ScratchBytes,
                preparation.ExpertCacheBytes,
                preparation.RequiredBytes,
                preparation.AvailableBytes,
                diagnostics);
        }
        catch (InferenceException exception)
        {
            return CreateFailedModelStatus(
                model,
                manifest,
                providerDiscovered: true,
                metadataValid: exception.Code is not "managed_model_invalid",
                sessionLoaded,
                forwardVerified,
                exception.Code,
                exception.Message,
                exception.Actions);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CreateFailedModelStatus(
                model,
                manifest,
                providerDiscovered: true,
                metadataValid: false,
                sessionLoaded,
                forwardVerified,
                "managed_provider_probe_failed",
                $"Managed provider '{provider.Id}' failed while inspecting the model: {exception.Message}");
        }
    }

    private static ModelPreparationResult InspectGenericModel(
        LocalModelDescriptor model,
        ModelProviderManifest manifest,
        string providerId,
        int contextSize)
    {
        var modelDirectory = Path.GetDirectoryName(model.AbsolutePath)
            ?? throw new InferenceException(
                "managed_model_invalid",
                "Managed model manifest path does not have a parent directory.");
        var configPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile);
        var tokenizerPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile);
        if (!File.Exists(configPath) || !File.Exists(tokenizerPath))
        {
            throw new InferenceException(
                "managed_model_assets_incomplete",
                "Managed model configuration or tokenizer asset is missing.",
                ["Complete the model download and checksum verification before retrying."]);
        }

        var tensors = Directory.EnumerateFiles(
                modelDirectory,
                manifest.TensorPattern,
                SearchOption.TopDirectoryOnly)
            .Take(4097)
            .ToArray();
        if (tensors.Length == 0 || tensors.Length > 4096)
        {
            throw new InferenceException(
                "managed_model_assets_incomplete",
                tensors.Length == 0
                    ? $"No tensor files matched '{manifest.TensorPattern}'."
                    : "Managed model contains more than 4096 tensor files.",
                ["Verify the model manifest and downloaded tensor shards."]);
        }

        return new ModelPreparationResult(
            providerId,
            manifest.Architecture,
            manifest.Quantization,
            manifest.QuantizationLayout,
            Math.Clamp(contextSize, 1, 131072),
            tensors.Length,
            0,
            0,
            0,
            0,
            0,
            0,
            long.MaxValue,
            ["The provider does not expose detailed model readiness diagnostics."]);
    }

    private static ModelReadinessStatus CreateFailedModelStatus(
        LocalModelDescriptor model,
        ModelProviderManifest? manifest,
        bool providerDiscovered,
        bool metadataValid,
        bool sessionLoaded,
        bool forwardVerified,
        string code,
        string message,
        IReadOnlyList<string>? actions = null)
    {
        return new ModelReadinessStatus(
            model.Id,
            manifest?.Provider,
            manifest?.Architecture ?? model.Family,
            manifest?.Quantization ?? model.QuantizationLevel,
            manifest?.QuantizationLayout,
            code == "managed_provider_unavailable" ? "provider_unavailable" : "invalid",
            providerDiscovered,
            metadataValid,
            false,
            forwardVerified,
            sessionLoaded,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [new ModelReadinessDiagnostic(code, message, actions ?? [])]);
    }

    public IModelFixtureProvider? FindFixtureProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return textProviders
            .OfType<IModelFixtureProvider>()
            .FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public IModelConversionProvider? FindConversionProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return textProviders
            .OfType<IModelConversionProvider>()
            .FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        foreach (var disposable in textProviders.OfType<IDisposable>())
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // An optional provider must not make host shutdown fail.
            }
        }
    }

    private static string ResolveStatus(
        IReadOnlyCollection<ModelProviderInfo> loadedProviders,
        IReadOnlyCollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            return "warning";
        }

        return loadedProviders.Count > 0 ? "ready" : "not_configured";
    }

    private static ModelProviderInfo CreateProviderInfo(ITextGenerationProvider provider)
    {
        var assembly = provider.GetType().Assembly;
        var assemblyName = assembly.GetName();
        return new ModelProviderInfo(
            provider.Id,
            assemblyName.Name ?? provider.GetType().Namespace ?? provider.Id,
            assemblyName.Version?.ToString(),
            AppContext.BaseDirectory);
    }
}
