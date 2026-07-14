using Tomur.Config;
using Tomur.Inference;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M1.Tests;

public sealed class ModelProbeTests
{
    [Fact]
    public void ValidFixtureCreatesProbeSessionWithoutEnablingForward()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        var provider = new ManagedGlmProvider();

        using var session = provider.CreateSession(model, new ModelSessionOptions(4096));

        var snapshot = session.GetSnapshot();
        Assert.False(snapshot.Loaded);
        Assert.Equal("managed-glm-probe", snapshot.Mode);
        var exception = Assert.Throws<InferenceException>(() =>
            session.Generate("hello", CompletionOptions.Default, CancellationToken.None));
        Assert.Equal("managed_forward_not_ready", exception.Code);
    }

    [Fact]
    public void MissingRequiredTensorReturnsManagedModelInvalid()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(omitRequiredTensor: true);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_invalid", exception.Code);
        Assert.Contains("Required model tensor is missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TensorOffsetBeyondShardReturnsManagedModelInvalid()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(outOfBoundsOffset: true);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_invalid", exception.Code);
        Assert.Contains("extends beyond its shard", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedTensorDataTypeReturnsManagedModelInvalid()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(unsupportedDataType: true);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_invalid", exception.Code);
        Assert.Contains("data type is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidManifestDirectoryIsExcludedWithoutHidingValidNativeModel()
    {
        using var dataDirectory = new TemporaryDirectory();
        var paths = new DataPaths(new PathOptions { DataDirectory = dataDirectory.Path });
        var invalidDirectory = Path.Combine(paths.ModelsDirectory, "invalid-managed");
        Directory.CreateDirectory(invalidDirectory);
        File.WriteAllText(Path.Combine(invalidDirectory, ModelProviderManifest.FileName), "{ invalid json");
        File.WriteAllText(Path.Combine(invalidDirectory, "weights.safetensors"), "not a tensor file");
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "native.gguf"), "fixture");

        var models = new LocalModelCatalog(paths).ListModels();

        var nativeModel = Assert.Single(models);
        Assert.Equal("gguf", nativeModel.Format);
        Assert.Equal("native", nativeModel.Id);
    }

    [Fact]
    public void MalformedManifestOnManagedDescriptorFailsProbeExplicitly()
    {
        using var fixture = new ManagedModelFixture();
        File.WriteAllText(fixture.ManifestPath, "{ invalid json");
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InvalidDataException>(() => provider.CanHandle(fixture.CreateDescriptor()));

        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }
}
