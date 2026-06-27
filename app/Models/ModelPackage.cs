namespace Tomur.Models;

public sealed record ModelPackage(
    string Id,
    string DisplayName,
    string Description,
    string Segment,
    string FolderName,
    string Task,
    string Runtime,
    string Family,
    string Format,
    string? Quantization,
    string? License,
    long? SizeBytes,
    long? ParameterCount,
    string PrimaryFileName,
    bool Recommended,
    bool Optional,
    bool Research,
    string[] Tags,
    IReadOnlyList<DownloadAsset> Assets,
    IReadOnlyList<ModelBundleAsset> BundleAssets,
    long MinimumMemoryBytes,
    string HardwareTier,
    string LicenseNotice,
    string? LowMemoryAlternativeId = null)
{
    public string ModelKey => FolderName;

    public string RelativeDirectory => CombineUrlPath(Segment, FolderName);

    public string PrimaryRelativePath => CombineUrlPath(RelativeDirectory, PrimaryFileName);

    public bool IsBundle => BundleAssets.Count > 0 || Assets.Count > 1;

    internal static string CombineUrlPath(params string[] parts)
    {
        return string.Join(
            '/',
            parts.Where(static part => !string.IsNullOrWhiteSpace(part))
                .Select(static part => part.Trim().Replace('\\', '/').Trim('/')));
    }
}
