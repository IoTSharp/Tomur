namespace Tomur.Models;

public sealed record ModelBundleAsset(
    string AssetKey,
    string Role,
    bool IsRequired,
    string RelativePath,
    string FileName,
    string Format,
    string? Quantization = null,
    string? License = null,
    long? SizeBytes = null,
    string? ExpectedSha256 = null,
    string? Description = null);
