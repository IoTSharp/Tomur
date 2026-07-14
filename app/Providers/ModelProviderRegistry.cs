#if !TOMUR_NATIVE_AOT
using System.Reflection;
using System.Runtime.Loader;
#endif
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Providers;

public sealed record ModelProviderLoadDiagnostic(string Code, string Message, string? Path = null);

public sealed class ModelProviderRegistry : IDisposable
{
    public const string ProviderPathEnvironmentVariable = "TOMUR_PROVIDER_PATH";

    private readonly IReadOnlyList<ITextGenerationProvider> textProviders;

    private ModelProviderRegistry(
        IReadOnlyList<ITextGenerationProvider> textProviders,
        IReadOnlyList<ModelProviderLoadDiagnostic> diagnostics)
    {
        this.textProviders = textProviders;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<ModelProviderLoadDiagnostic> Diagnostics { get; }

    public static ModelProviderRegistry CreateDefault()
    {
#if TOMUR_NATIVE_AOT
        return new ModelProviderRegistry(
            [],
            [
                new ModelProviderLoadDiagnostic(
                    "dynamic_managed_providers_unavailable",
                    "Dynamic managed provider assemblies are unavailable in the Native AOT release profile.")
            ]);
#else
        return Load(ResolveProviderDirectories());
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

    public void Dispose()
    {
        foreach (var disposable in textProviders.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }

#if !TOMUR_NATIVE_AOT
    private static ModelProviderRegistry Load(IReadOnlyList<string> directories)
    {
        var providers = new List<ITextGenerationProvider>();
        var diagnostics = new List<ModelProviderLoadDiagnostic>();
        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(directory, "Tomur.Providers.*.dll", SearchOption.TopDirectoryOnly))
                {
                    TryLoadAssembly(path, providers, providerIds, diagnostics);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ModelProviderLoadDiagnostic(
                    "managed_provider_directory_unavailable",
                    exception.Message,
                    directory));
            }
        }

        return new ModelProviderRegistry(providers, diagnostics);
    }

    private static void TryLoadAssembly(
        string path,
        ICollection<ITextGenerationProvider> providers,
        ISet<string> providerIds,
        ICollection<ModelProviderLoadDiagnostic> diagnostics)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in GetLoadableTypes(assembly, path, diagnostics))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(ITextGenerationProvider).IsAssignableFrom(type))
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not ITextGenerationProvider provider)
                {
                    diagnostics.Add(new ModelProviderLoadDiagnostic(
                        "managed_provider_activation_failed",
                        $"Managed provider type could not be activated: {type.FullName}",
                        path));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(provider.Id) || !providerIds.Add(provider.Id))
                {
                    (provider as IDisposable)?.Dispose();
                    diagnostics.Add(new ModelProviderLoadDiagnostic(
                        "managed_provider_id_invalid",
                        $"Managed provider ID is empty or duplicated: {provider.Id}",
                        path));
                    continue;
                }

                providers.Add(provider);
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

    private static IReadOnlyList<string> ResolveProviderDirectories()
    {
        var directories = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "providers")
        };
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
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    // Ignore invalid optional paths; the default provider directory remains available.
                }
            }
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
#endif
}
