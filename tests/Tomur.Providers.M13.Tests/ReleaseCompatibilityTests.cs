using System.Reflection;
using Tomur.Providers.Glm;
using Tomur.Providers.Olmoe;
using Xunit;

namespace Tomur.Providers.M13.Tests;

public sealed class ReleaseCompatibilityTests
{
    [Fact]
    public void GlmProviderReferencesTheLockedContractAssemblyVersion()
    {
        var reference = typeof(ManagedGlmProvider)
            .Assembly
            .GetReferencedAssemblies()
            .Single(candidate => candidate.Name == ModelProviderContract.AssemblyName);

        Assert.True(ModelProviderContract.IsCompatible(reference));
        Assert.Equal("Tomur.Providers.Abstractions", ModelProviderContract.AssemblyName);
        Assert.Equal(1, ModelProviderContract.Version);
    }

    [Fact]
    public void IncompatibleContractVersionIsRejected()
    {
        var incompatible = new AssemblyName(ModelProviderContract.AssemblyName)
        {
            Version = new Version(99, 0, 0, 0)
        };

        Assert.False(ModelProviderContract.IsCompatible(incompatible));
    }

    [Fact]
    public void DefaultRegistryStaticallyRegistersApprovedProviders()
    {
        using var registry = ModelProviderRegistry.CreateDefault();

        Assert.False(registry.Status.DynamicLoadingSupported);
        Assert.Empty(registry.Status.SearchDirectories);
        Assert.Contains(
            registry.Status.Loaded,
            static provider => provider.Id == ManagedGlmProvider.ProviderId);
        Assert.Contains(
            registry.Status.Loaded,
            static provider => provider.Id == ManagedOlmoeProvider.ProviderId);
    }
}
