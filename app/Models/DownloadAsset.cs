namespace Tomur.Models;

public sealed record DownloadAsset(
    string RepositoryId,
    string RelativePath,
    string TargetRelativePath,
    string? ExpectedSha256 = null,
    DownloadSourceKind SourceKind = DownloadSourceKind.HuggingFaceResolve,
    IReadOnlyList<string>? AlternateUrls = null)
{
    public static DownloadAsset HuggingFace(
        string repositoryId,
        string relativePath,
        string? targetRelativePath = null,
        string? expectedSha256 = null,
        params string[] alternateUrls)
    {
        return new DownloadAsset(
            repositoryId,
            relativePath,
            string.IsNullOrWhiteSpace(targetRelativePath) ? relativePath : targetRelativePath,
            expectedSha256,
            DownloadSourceKind.HuggingFaceResolve,
            alternateUrls);
    }

    public static DownloadAsset Direct(
        string sourceId,
        string url,
        string? targetRelativePath = null,
        string? expectedSha256 = null,
        params string[] alternateUrls)
    {
        var fallbackTarget = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.LocalPath)
            : Path.GetFileName(url);

        return new DownloadAsset(
            sourceId,
            url,
            string.IsNullOrWhiteSpace(targetRelativePath) ? fallbackTarget : targetRelativePath,
            expectedSha256,
            DownloadSourceKind.DirectUrl,
            alternateUrls);
    }
}

public enum DownloadSourceKind
{
    HuggingFaceResolve,
    DirectUrl
}
