using Tomur.Inference;
using Tomur.Models;

namespace Tomur.Providers.M10.Tests;

public sealed class Qwen35ModelPackageTests
{
    /// <summary>
    /// 验证 Qwen3.5 聊天提示使用官方空思考段预填充，生成内容可直接作为业务正文解析。
    /// </summary>
    [Fact]
    public void Qwen35ChatPromptUsesNonThinkingPrefill()
    {
        var builder = new LlamaPromptBuilder();

        var prompt = builder.BuildChatPrompt(
            [new ChatTurn("user", "只输出 JSON。")],
            "Qwen3.5 35B-A3B Q4_K_M");

        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证普通 Qwen 模型仍沿用既有 ChatML 结尾，不被 Qwen3.5 专用预填充影响。
    /// </summary>
    [Fact]
    public void LegacyQwenChatPromptKeepsExistingAssistantPrefix()
    {
        var builder = new LlamaPromptBuilder();

        var prompt = builder.BuildChatPrompt(
            [new ChatTurn("user", "hello")],
            "Qwen2.5 7B");

        Assert.EndsWith("<|im_start|>assistant\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<think>", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证木垒 35B 模型包固定到已核对的主模型和视觉 projector 元数据。
    /// </summary>
    [Fact]
    public void Qwen35CatalogPackageDeclaresPinnedVisionBundleMetadata()
    {
        var package = new ModelCatalog().Find("qwen35-35b-a3b-q4km");

        Assert.NotNull(package);
        Assert.Equal("vision", package.Task);
        Assert.Equal(22_285_080_384, package.SizeBytes);
        Assert.Equal(2, package.Assets.Count);
        Assert.Equal(2, package.BundleAssets.Count);

        Assert.Collection(
            package.BundleAssets,
            asset =>
            {
                Assert.Equal("primary", asset.AssetKey);
                Assert.Equal("Qwen_Qwen3.5-35B-A3B-Q4_K_M.gguf", asset.RelativePath);
                Assert.Equal(22_285_080_384, asset.SizeBytes);
                Assert.Equal("2f2df1e8b2e92b642c1850ea1734b341cc8ca5098c42cc0a8b8c436a8d4751ab", asset.ExpectedSha256);
            },
            asset =>
            {
                Assert.Equal("mmproj", asset.AssetKey);
                Assert.Equal("mmproj-Qwen_Qwen3.5-35B-A3B-f16.gguf", asset.RelativePath);
                Assert.Equal("F16", asset.Quantization);
                Assert.Equal(899_283_552, asset.SizeBytes);
                Assert.Equal("10cf13cb1f8434f30df8fa7e5bde98d542fbf397550cb489dfa9eb8ac7069035", asset.ExpectedSha256);
            });

        Assert.Collection(
            package.Assets,
            asset =>
            {
                Assert.Equal(DownloadSourceKind.DirectUrl, asset.SourceKind);
                Assert.Equal("Qwen_Qwen3.5-35B-A3B-Q4_K_M.gguf", asset.TargetRelativePath);
                Assert.Contains("3d2a22d4eb631b9c8fd2be94599dc2fd84b4a595", asset.RelativePath, StringComparison.Ordinal);
                Assert.Equal("2f2df1e8b2e92b642c1850ea1734b341cc8ca5098c42cc0a8b8c436a8d4751ab", asset.ExpectedSha256);
            },
            asset =>
            {
                Assert.Equal(DownloadSourceKind.DirectUrl, asset.SourceKind);
                Assert.Equal("mmproj-Qwen_Qwen3.5-35B-A3B-f16.gguf", asset.TargetRelativePath);
                Assert.Contains("3d2a22d4eb631b9c8fd2be94599dc2fd84b4a595", asset.RelativePath, StringComparison.Ordinal);
                Assert.Equal("10cf13cb1f8434f30df8fa7e5bde98d542fbf397550cb489dfa9eb8ac7069035", asset.ExpectedSha256);
            });
    }
}
