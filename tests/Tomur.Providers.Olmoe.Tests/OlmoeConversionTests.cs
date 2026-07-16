using System.Security.Cryptography;
using System.Text.Json;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Runtime;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeConversionTests
{
    [Fact]
    public async Task FloatingFixtureConvertsToValidatedRowwiseInt8Directory()
    {
        using var source = new OlmoeFixture(quantizedExperts: false);
        var output = Path.Combine(Path.GetTempPath(), $"tomur-olmoe-converted-{Guid.NewGuid():N}");
        try
        {
            var provider = new ManagedOlmoeProvider();
            var result = provider.ConvertModel(new ModelConversionRequest(source.Path, output));

            Assert.Equal("int8", result.Quantization);
            Assert.Equal("rowwise-qs", result.QuantizationLayout);
            Assert.True(result.OutputTensorCount > result.SourceTensorCount);
            var tensorPath = Path.Combine(output, "model.int8.safetensors");
            await using (var stream = File.OpenRead(tensorPath))
            {
                Assert.Equal(result.OutputSha256, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
            }

            var descriptor = CreateDescriptor(output);
            var probe = OlmoeModelDirectoryProbe.Read(descriptor, ManagedOlmoeProvider.ProviderId);
            var expert = probe.Tensors.GetRequired("model.layers.0.mlp.experts.0.gate_proj.weight");
            Assert.Equal("I8", expert.DataTypeName);
            Assert.True(probe.Tensors.Contains($"{expert.Name}.qs"));
            using var session = provider.CreateSession(descriptor, new ModelSessionOptions(8));
            var resultText = ((IChatGenerationSession)session).GenerateChat(
                [new ChatTurn("user", "hello")],
                CreateOptions(),
                CancellationToken.None);
            Assert.Equal("hello", resultText.Text);

            using var conversion = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "conversion.manifest.json")));
            Assert.Equal(
                result.OutputSha256,
                conversion.RootElement.GetProperty("output_file").GetProperty("sha256").GetString());
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public void CancelledConversionDoesNotPublishPartialDirectory()
    {
        using var source = new OlmoeFixture(quantizedExperts: false);
        var output = Path.Combine(Path.GetTempPath(), $"tomur-olmoe-cancelled-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ManagedOlmoeProvider().ConvertModel(
                new ModelConversionRequest(source.Path, output),
                cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetDirectoryName(output)!,
            $".{Path.GetFileName(output)}.partial-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ExistingOutputIsNeverOverwritten()
    {
        using var source = new OlmoeFixture(quantizedExperts: false);
        var output = Path.Combine(Path.GetTempPath(), $"tomur-olmoe-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var marker = Path.Combine(output, "owner.txt");
        File.WriteAllText(marker, "keep");
        try
        {
            Assert.Throws<IOException>(() =>
                new ManagedOlmoeProvider().ConvertModel(new ModelConversionRequest(source.Path, output)));
            Assert.Equal("keep", File.ReadAllText(marker));
        }
        finally
        {
            TryDelete(output);
        }
    }

    private static LocalModelDescriptor CreateDescriptor(string directory)
    {
        var path = Path.Combine(directory, ModelProviderManifest.FileName);
        var info = new FileInfo(path);
        return new LocalModelDescriptor(
            "converted-olmoe",
            "Converted OLMoE",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            path,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "olmoe",
            "int8",
            ["completion", "chat"]);
    }

    private static CompletionOptions CreateOptions()
        => CompletionOptions.Default with
        {
            ContextSize = 8,
            MaxOutputTokens = 1,
            Temperature = 0,
            TopK = 8,
            TopP = 1,
            StopSequences = []
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
