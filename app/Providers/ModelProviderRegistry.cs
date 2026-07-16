#if !TOMUR_NATIVE_AOT
using System.Reflection;
using System.Runtime.Loader;
#endif
using System.Text.Json.Serialization;
using Tomur.Inference;
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
    public const string ProviderPathEnvironmentVariable = "TOMUR_PROVIDER_PATH";

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
            ResolveStatus(dynamicLoadingSupported, loadedProviders, diagnostics),
            dynamicLoadingSupported,
            searchDirectories,
            loadedProviders,
            diagnostics);
    }

    public IReadOnlyList<ModelProviderLoadDiagnostic> Diagnostics { get; }

    public ModelProviderStatus Status { get; }

    public static ModelProviderRegistry CreateDefault()
    {
#if TOMUR_NATIVE_AOT
        var defaultDirectory = Path.Combine(AppContext.BaseDirectory, "providers");
        var diagnostics = new ModelProviderLoadDiagnostic[]
        {
            new(
                "dynamic_managed_providers_unavailable",
                "Dynamic managed provider assemblies are unavailable in the Native AOT release profile.",
                defaultDirectory)
        };
        return new ModelProviderRegistry([], false, [defaultDirectory], [], diagnostics);
#else
        var resolution = ResolveProviderDirectories();
        return Load(resolution.Directories, resolution.Diagnostics);
#endif
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
                    $"Place the matching provider DLL in '{Path.Combine(AppContext.BaseDirectory, "providers")}'.",
                    $"Alternatively set {ProviderPathEnvironmentVariable} to the provider output directory."
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

            return new ModelReadinessStatus(
                model.Id,
                preparation.ProviderId,
                preparation.Architecture,
                preparation.Quantization,
                preparation.QuantizationLayout,
                sessionLoaded ? "loaded" : fitsMemory ? "ready" : "memory_limited",
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
        bool dynamicLoadingSupported,
        IReadOnlyCollection<ModelProviderInfo> loadedProviders,
        IReadOnlyCollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        if (!dynamicLoadingSupported)
        {
            return "unavailable";
        }

        if (diagnostics.Count > 0)
        {
            return "warning";
        }

        return loadedProviders.Count > 0 ? "ready" : "not_configured";
    }

#if !TOMUR_NATIVE_AOT
    private static ModelProviderRegistry Load(
        IReadOnlyList<string> directories,
        IReadOnlyList<ModelProviderLoadDiagnostic> resolutionDiagnostics)
    {
        var providers = new List<ITextGenerationProvider>();
        var loadedProviders = new List<ModelProviderInfo>();
        var diagnostics = new List<ModelProviderLoadDiagnostic>(resolutionDiagnostics);
        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var assemblyPaths = Directory
                    .EnumerateFiles(directory, "Tomur.Providers.*.dll", SearchOption.TopDirectoryOnly)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                ManagedProviderReleaseManifest? releaseManifest = null;
                var releaseManifestPath = Path.Combine(directory, ManagedProviderReleaseManifest.FileName);
                if (File.Exists(releaseManifestPath))
                {
                    if (!ManagedProviderReleaseManifest.TryRead(
                            releaseManifestPath,
                            out releaseManifest,
                            out var manifestError))
                    {
                        diagnostics.Add(new ModelProviderLoadDiagnostic(
                            "managed_provider_release_manifest_invalid",
                            manifestError ?? "Managed provider release manifest is invalid.",
                            releaseManifestPath));
                        continue;
                    }

                    foreach (var releaseEntry in releaseManifest!.Providers)
                    {
                        var releaseAssetPath = Path.Combine(directory, releaseEntry.Assembly);
                        if (!File.Exists(releaseAssetPath))
                        {
                            diagnostics.Add(new ModelProviderLoadDiagnostic(
                                "managed_provider_release_asset_invalid",
                                $"Provider release asset is missing: {releaseEntry.Assembly}.",
                                releaseAssetPath));
                        }
                    }
                }

                foreach (var path in assemblyPaths)
                {
                    TryLoadAssembly(path, releaseManifest, providers, loadedProviders, providerIds, diagnostics);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_directory_unavailable",
                    exception.Message,
                    directory));
            }
        }

        return new ModelProviderRegistry(providers, true, directories, loadedProviders, diagnostics);
    }

    private static void TryLoadAssembly(
        string path,
        ManagedProviderReleaseManifest? releaseManifest,
        ICollection<ITextGenerationProvider> providers,
        ICollection<ModelProviderInfo> loadedProviders,
        ISet<string> providerIds,
        ICollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            ManagedProviderReleaseEntry? releaseEntry = null;
            if (releaseManifest is not null &&
                !releaseManifest.TryVerify(
                    Path.GetDirectoryName(fullPath) ?? string.Empty,
                    Path.GetFileName(fullPath),
                    out releaseEntry,
                    out var releaseError))
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_release_asset_invalid",
                    releaseError ?? "Managed provider release asset failed manifest verification.",
                    fullPath));
                return;
            }

            var assembly = LoadProviderAssembly(fullPath);
            var assemblyVersion = assembly.GetName().Version?.ToString();
            if (releaseEntry is not null &&
                !string.Equals(assemblyVersion, releaseEntry.Version, StringComparison.Ordinal))
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_release_asset_invalid",
                    $"Provider assembly version {assemblyVersion ?? "unknown"} does not match release manifest version {releaseEntry.Version}.",
                    fullPath));
                return;
            }

            var contractAssembly = assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(static reference =>
                    string.Equals(reference.Name, ModelProviderContract.AssemblyName, StringComparison.Ordinal));
            if (contractAssembly is null)
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_contract_not_found",
                    $"The provider does not reference the '{ModelProviderContract.AssemblyName}' contract assembly.",
                    fullPath));
                return;
            }

            if (!ModelProviderContract.IsCompatible(contractAssembly))
            {
                var expectedVersion = ModelProviderContract.AssemblyVersion?.ToString() ?? "unknown";
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_contract_incompatible",
                    $"The provider references contract assembly version {contractAssembly.Version}, but this Tomur release requires {expectedVersion}.",
                    fullPath));
                return;
            }

            var providerTypes = GetLoadableTypes(assembly, fullPath, diagnostics)
                .Where(static type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(ITextGenerationProvider).IsAssignableFrom(type))
                .ToArray();
            if (providerTypes.Length == 0)
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_contract_not_found",
                    "The assembly does not contain an ITextGenerationProvider implementation compatible with this Tomur build.",
                    fullPath));
                return;
            }

            foreach (var type in providerTypes)
            {
                TryActivateProvider(
                    type,
                    fullPath,
                    releaseEntry?.Id,
                    providers,
                    loadedProviders,
                    providerIds,
                    diagnostics);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            diagnostics.Add(new ModelProviderLoadDiagnostic(
                "managed_provider_load_failed",
                exception.Message,
                path));
        }
    }

    private static void TryActivateProvider(
        Type type,
        string path,
        string? expectedProviderId,
        ICollection<ITextGenerationProvider> providers,
        ICollection<ModelProviderInfo> loadedProviders,
        ISet<string> providerIds,
        ICollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        ITextGenerationProvider? provider = null;
        try
        {
            provider = Activator.CreateInstance(type) as ITextGenerationProvider;
            if (provider is null)
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_activation_failed",
                    $"Managed provider type could not be activated: {type.FullName}",
                    path));
                return;
            }

            var providerId = provider.Id;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                DisposeRejectedProvider(provider);
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_id_invalid",
                    "Managed provider ID must be a non-empty string.",
                    path));
                return;
            }

            if (expectedProviderId is not null &&
                !string.Equals(providerId, expectedProviderId, StringComparison.OrdinalIgnoreCase))
            {
                DisposeRejectedProvider(provider);
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_release_asset_invalid",
                    $"Provider ID '{providerId}' does not match release manifest ID '{expectedProviderId}'.",
                    path));
                return;
            }

            if (!providerIds.Add(providerId))
            {
                DisposeRejectedProvider(provider);
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_id_duplicate",
                    $"Managed provider ID is already registered: {providerId}",
                    path));
                return;
            }

            var assemblyName = type.Assembly.GetName();
            providers.Add(provider);
            loadedProviders.Add(new ModelProviderInfo(
                providerId,
                assemblyName.Name ?? Path.GetFileNameWithoutExtension(path),
                assemblyName.Version?.ToString(),
                path));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DisposeRejectedProvider(provider);
            diagnostics.Add(new ModelProviderLoadDiagnostic(
                "managed_provider_activation_failed",
                $"Managed provider type '{type.FullName}' could not be activated: {exception.Message}",
                path));
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly,
        string path,
        ICollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            foreach (var loaderException in exception.LoaderExceptions.Where(static item => item is not null))
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_type_load_failed",
                    loaderException!.Message,
                    path));
            }

            return exception.Types.Where(static type => type is not null).Select(static type => type!);
        }
    }

    private static Assembly LoadProviderAssembly(string fullPath)
    {
        var loadedAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
        {
            try
            {
                return !string.IsNullOrWhiteSpace(assembly.Location) &&
                    string.Equals(Path.GetFullPath(assembly.Location), fullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        });
        return loadedAssembly ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }

    private static ProviderDirectoryResolution ResolveProviderDirectories()
    {
        var directories = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "providers")
        };
        var diagnostics = new List<ModelProviderLoadDiagnostic>();
        var configured = Environment.GetEnvironmentVariable(ProviderPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var candidate in configured.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    directories.Add(Path.GetFullPath(candidate));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or IOException)
                {
                    diagnostics.Add(new ModelProviderLoadDiagnostic(
                        "managed_provider_path_invalid",
                        $"The configured managed provider path is invalid: {exception.Message}",
                        candidate));
                }
            }
        }

        return new ProviderDirectoryResolution(
            directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics);
    }

    private static void DisposeRejectedProvider(ITextGenerationProvider? provider)
    {
        try
        {
            (provider as IDisposable)?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Rejected providers are isolated from the host lifecycle.
        }
    }

    private sealed record ProviderDirectoryResolution(
        IReadOnlyList<string> Directories,
        IReadOnlyList<ModelProviderLoadDiagnostic> Diagnostics);
#endif
}
