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

    private ModelProviderRegistry(
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
                    throw new InferenceException(
                        "managed_provider_probe_failed",
                        $"Managed provider '{provider.Id}' failed while probing the model: {exception.Message}",
                        ["Verify the provider assembly version and the model provider manifest."],
                        exception);
                }
            }
        }

        return null;
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
                foreach (var path in assemblyPaths)
                {
                    TryLoadAssembly(path, providers, loadedProviders, providerIds, diagnostics);
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
        ICollection<ITextGenerationProvider> providers,
        ICollection<ModelProviderInfo> loadedProviders,
        ISet<string> providerIds,
        ICollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var assembly = LoadProviderAssembly(fullPath);
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
                TryActivateProvider(type, fullPath, providers, loadedProviders, providerIds, diagnostics);
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
