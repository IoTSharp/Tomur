using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Native;
using Tomur.Providers.Glm;
using Tomur.Providers.Olmoe;
using Tomur.Runtime;

namespace Tomur.Providers.M1.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void DefaultRegistryContainsProjectReferencedProviders()
    {
        using var registry = ModelProviderRegistry.CreateDefault();

        Assert.Equal("ready", registry.Status.Status);
        Assert.False(registry.Status.DynamicLoadingSupported);
        Assert.Empty(registry.Status.SearchDirectories);
        Assert.Empty(registry.Diagnostics);
        Assert.Collection(
            registry.Status.Loaded.OrderBy(static provider => provider.Id),
            provider =>
            {
                Assert.Equal(ManagedGlmProvider.ProviderId, provider.Id);
                Assert.Equal("Tomur.Providers.Glm", provider.Assembly);
            },
            provider =>
            {
                Assert.Equal(ManagedOlmoeProvider.ProviderId, provider.Id);
                Assert.Equal("Tomur.Providers.Olmoe", provider.Assembly);
            });
    }

    [Fact]
    public void MalformedManagedManifestIsReportedAsProviderProbeFailure()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        File.WriteAllText(fixture.ManifestPath, "{ invalid json");
        using var registry = ModelProviderRegistry.CreateDefault();

        var exception = Assert.Throws<InferenceException>(() => registry.FindTextProvider(model));

        Assert.Equal("managed_model_invalid", exception.Code);
    }

    [Fact]
    public void ManagedModelWithoutMatchingProviderReturnsUnavailableDiagnostic()
    {
        using var dataDirectory = new TemporaryDirectory();
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        File.WriteAllText(
            fixture.ManifestPath,
            File.ReadAllText(fixture.ManifestPath).Replace(
                "\"managed-glm\"",
                "\"missing-provider\"",
                StringComparison.Ordinal));
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
        Assert.Contains(
            exception.Actions,
            static action => action.Contains("compiled into this Tomur build", StringComparison.Ordinal));
    }
}
