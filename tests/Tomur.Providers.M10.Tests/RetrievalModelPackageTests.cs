using Tomur.Models;

namespace Tomur.Providers.M10.Tests;

public sealed class RetrievalModelPackageTests
{
    [Theory]
    [InlineData(
        "embeddinggemma-300m-q8",
        328_577_056,
        "embeddinggemma-300M-Q8_0.gguf",
        "a0f7b4e13c397a6e1b32c2de75b1f65a14c92ec524d5f674d94a4290a1c4969b")]
    [InlineData(
        "bge-reranker-v2-m3-q8",
        635_676_416,
        "bge-reranker-v2-m3-Q8_0.gguf",
        "a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3")]
    public void RetrievalPackagesUsePinnedVerifiedAssets(
        string packageId,
        long sizeBytes,
        string fileName,
        string sha256)
    {
        var package = new ModelCatalog().Find(packageId);

        Assert.NotNull(package);
        Assert.Equal(sizeBytes, package.SizeBytes);
        var asset = Assert.Single(package.Assets);
        Assert.Equal(DownloadSourceKind.DirectUrl, asset.SourceKind);
        Assert.Equal(fileName, asset.TargetRelativePath);
        Assert.Equal(sha256, asset.ExpectedSha256);
        Assert.Single(asset.AlternateUrls!);
    }

    [Fact]
    public void FluxBundlePinsEveryRequiredAsset()
    {
        var package = new ModelCatalog().Find("flux2-klein-4b-q4km");

        Assert.NotNull(package);
        Assert.Equal(2_604_311_104, package.SizeBytes);
        Assert.Equal(3, package.Assets.Count);
        Assert.Equal(3, package.BundleAssets.Count);
        Assert.All(package.Assets, asset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(asset.ExpectedSha256));
            Assert.NotEmpty(asset.AlternateUrls!);
        });
        Assert.All(package.BundleAssets, asset =>
        {
            Assert.NotNull(asset.SizeBytes);
            Assert.False(string.IsNullOrWhiteSpace(asset.ExpectedSha256));
        });
    }
}
