using Tomur.Config;
using Tomur.Inference;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M1.Tests;

public sealed class ModelProbeTests
{
    [Fact]
    public void ValidFixtureLoadsManagedGenerationSession()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel();
        var provider = new ManagedGlmProvider();

        using var session = provider.CreateSession(model, new ModelSessionOptions(4096));

        var snapshot = session.GetSnapshot();
        Assert.True(snapshot.Loaded);
        Assert.Equal("managed-glm-generation", snapshot.Mode);
        Assert.Contains(
            snapshot.Diagnostics,
            static diagnostic => diagnostic.StartsWith("kernel execution:", StringComparison.Ordinal));
    }

    [Fact]
    public void Glm4MoeLiteFixtureLoadsManagedGenerationSession()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(architecture: GlmModelConfiguration.MoeLiteModelType);
        var provider = new ManagedGlmProvider();

        using var session = provider.CreateSession(model, new ModelSessionOptions(4096));

        Assert.True(session.GetSnapshot().Loaded);
    }

    [Fact]
    public void ManifestArchitectureMustMatchConfigurationModelType()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(
            architecture: GlmModelConfiguration.MoeLiteModelType,
            modelType: GlmModelConfiguration.DsaModelType);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_invalid", exception.Code);
        Assert.Contains("does not match manifest architecture", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedAttentionBiasFailsBeforeTensorLoading()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(architecture: GlmModelConfiguration.MoeLiteModelType);
        var config = File.ReadAllText(fixture.ConfigPath)
            .Replace("\"attention_bias\": false", "\"attention_bias\": true", StringComparison.Ordinal);
        File.WriteAllText(fixture.ConfigPath, config);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_invalid", exception.Code);
        Assert.Contains("attention_bias=false", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredTensorReturnsAssetsIncomplete()
    {
        using var fixture = new ManagedModelFixture();
        var model = fixture.CreateValidModel(omitRequiredTensor: true);
        var provider = new ManagedGlmProvider();

        var exception = Assert.Throws<InferenceException>(() =>
            provider.CreateSession(model, new ModelSessionOptions(4096)));

        Assert.Equal("managed_model_assets_incomplete", exception.Code);
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
