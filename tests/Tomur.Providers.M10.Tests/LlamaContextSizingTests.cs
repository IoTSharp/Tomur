using Tomur.Inference;

namespace Tomur.Providers.M10.Tests;

public sealed class LlamaContextSizingTests
{
    [Theory]
    [InlineData(512, 512, 512)]
    [InlineData(1024, 1024, 512)]
    [InlineData(2048, 2048, 512)]
    [InlineData(131072, 2048, 512)]
    public void NativeBatchSizesRemainBounded(
        int contextSize,
        int expectedBatchSize,
        int expectedMicroBatchSize)
    {
        var sizing = LlamaNativeSession.ResolveBatchSizes(contextSize);

        Assert.Equal(expectedBatchSize, sizing.BatchSize);
        Assert.Equal(expectedMicroBatchSize, sizing.MicroBatchSize);
    }

    [Fact]
    public void LongPromptPrefillIsSplitAtTheConfiguredBatchBoundary()
    {
        var chunks = LlamaNativeSession.ResolvePrefillChunks(5000, 2048).ToArray();

        Assert.Equal(
            [(0, 2048), (2048, 2048), (4096, 904)],
            chunks);
    }

    [Fact]
    public void EmptyPromptHasNoPrefillChunks()
    {
        Assert.Empty(LlamaNativeSession.ResolvePrefillChunks(0, 2048));
    }
}
