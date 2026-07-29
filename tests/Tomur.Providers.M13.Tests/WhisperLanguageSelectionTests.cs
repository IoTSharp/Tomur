using Tomur.Multimodal;
using Xunit;

namespace Tomur.Providers.M13.Tests;

public sealed class WhisperLanguageSelectionTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData(" zh ", "zh")]
    [InlineData("KK", "kk")]
    public void ResolvesWhisperLanguageWithoutEnablingDetectionOnlyMode(
        string? language,
        string expected)
    {
        var selection = MultimodalExecutionService.ResolveWhisperLanguage(language);

        Assert.Equal(expected, selection.NativeLanguage);
        Assert.False(selection.DetectLanguageOnly);
    }
}
