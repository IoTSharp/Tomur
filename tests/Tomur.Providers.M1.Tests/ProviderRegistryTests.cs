using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Native;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M1.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void DefaultProviderDirectoryIsAlwaysPartOfDiscoveryStatus()
    {
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            null);
        using var registry = ModelProviderRegistry.CreateDefault();

        Assert.Contains(
            Path.Combine(AppContext.BaseDirectory, "providers"),
            registry.Status.SearchDirectories,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentProviderDirectoryLoadsManagedGlmProvider()
    {
        var providerDirectory = Path.GetDirectoryName(typeof(ManagedGlmProvider).Assembly.Location)!;
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            providerDirectory);
        using var registry = ModelProviderRegistry.CreateDefault();

        var provider = Assert.Single(
            registry.Status.Loaded,
            static provider => provider.Id == ManagedGlmProvider.ProviderId);
        Assert.Equal("Tomur.Providers.Glm", provider.Assembly);
        Assert.Contains(providerDirectory, registry.Status.SearchDirectories, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrokenProviderAssemblyProducesStructuredDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "Tomur.Providers.Broken.dll");
        File.WriteAllText(path, "not a managed assembly");
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            directory.Path);

        using var registry = ModelProviderRegistry.CreateDefault();

        var diagnostic = Assert.Single(
            registry.Diagnostics,
            static item => item.Code == "managed_provider_load_failed");
        Assert.Equal(path, diagnostic.Path);
        Assert.Equal("warning", registry.Status.Status);
    }

    [Fact]
    public void DuplicateAndInvalidProviderIdsAreIsolated()
    {
        var providerDirectory = Path.GetDirectoryName(typeof(ProviderRegistryTests).Assembly.Location)!;
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            providerDirectory);

        using var registry = ModelProviderRegistry.CreateDefault();

        Assert.Contains(registry.Diagnostics, static item => item.Code == "managed_provider_id_duplicate");
        Assert.Contains(registry.Diagnostics, static item => item.Code == "managed_provider_id_invalid");
        Assert.Contains(registry.Status.Loaded, static provider => provider.Id == DuplicateProviderOne.ProviderId);
    }

    [Fact]
    public void MalformedManagedManifestIsReportedAsProviderProbeFailure()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        File.WriteAllText(fixture.ManifestPath, "{ invalid json");
        var providerDirectory = Path.GetDirectoryName(typeof(ManagedGlmProvider).Assembly.Location)!;
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            providerDirectory);
        using var registry = ModelProviderRegistry.CreateDefault();

        var exception = Assert.Throws<InferenceException>(() => registry.FindTextProvider(model));

        Assert.Equal("managed_provider_probe_failed", exception.Code);
    }

    [Fact]
    public void ManagedModelWithoutMatchingProviderReturnsUnavailableDiagnostic()
    {
        using var dataDirectory = new TemporaryDirectory();
        using var providerDirectory = new TemporaryDirectory();
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        File.WriteAllText(
            fixture.ManifestPath,
            File.ReadAllText(fixture.ManifestPath).Replace(
                "\"managed-glm\"",
                "\"missing-provider\"",
                StringComparison.Ordinal));
        using var environment = new EnvironmentVariableScope(
            ModelProviderRegistry.ProviderPathEnvironmentVariable,
            providerDirectory.Path);
        using var registry = ModelProviderRegistry.CreateDefault();
        var paths = new DataPaths(new PathOptions { DataDirectory = dataDirectory.Path });
        var configurationStore = new ConfigurationStore(paths);
        var nativeProbe = new NativeBundleProbe(paths);
        var resolver = new NativeLibraryResolver(nativeProbe);
        var importResolver = new LlamaImportResolver(resolver);
        var backendInitializer = new LlamaBackendInitializer(importResolver, resolver, configurationStore);
        var accelerationService = new HardwareAccelerationService(backendInitializer, nativeProbe, configurationStore);
        using var sessionManager = new SessionManager(
            backendInitializer,
            accelerationService,
            registry,
            NullLogger<SessionManager>.Instance);
        var inferenceService = new LocalInferenceService(sessionManager);

        var exception = Assert.Throws<InferenceException>(() =>
            inferenceService.Complete(
                model,
                "hello",
                CompletionOptions.Default,
                CancellationToken.None));

        Assert.Equal("managed_provider_unavailable", exception.Code);
    }
}

public sealed class DuplicateProviderOne : ITextGenerationProvider
{
    public const string ProviderId = "duplicate-test";

    public string Id => ProviderId;

    public bool CanHandle(LocalModelDescriptor model) => false;

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
        => throw new NotSupportedException();
}

public sealed class DuplicateProviderTwo : ITextGenerationProvider
{
    public string Id => DuplicateProviderOne.ProviderId;

    public bool CanHandle(LocalModelDescriptor model) => false;

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
        => throw new NotSupportedException();
}

public sealed class InvalidIdProvider : ITextGenerationProvider
{
    public string Id => " ";

    public bool CanHandle(LocalModelDescriptor model) => false;

    public ITextGenerationSession CreateSession(LocalModelDescriptor model, ModelSessionOptions options)
        => throw new NotSupportedException();
}
