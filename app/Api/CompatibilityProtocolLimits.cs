namespace Tomur.Api;

internal static class CompatibilityProtocolLimits
{
    public const int MaxInputCharacters = 262_144;

    public const long MaxAudioBytes = 512L * 1024L * 1024L;

    public const int MaxImageCount = 8;

    public const long MaxImageBytes = 64L * 1024L * 1024L;
}
