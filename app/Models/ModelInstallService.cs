using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Tomur.Config;

namespace Tomur.Models;

public sealed class ModelInstallService
{
    private readonly DataPaths paths;
    private readonly InstallManifestStore manifestStore;
    private readonly ProxySettings proxySettings;
    private readonly TextWriter output;

    public ModelInstallService(DataPaths paths, ProxySettings proxySettings, TextWriter output)
    {
        this.paths = paths;
        this.proxySettings = proxySettings;
        this.output = output;
        manifestStore = new InstallManifestStore(paths);
    }

    public async Task InstallAsync(IReadOnlyList<ModelPackage> packages, bool force, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);
        Directory.CreateDirectory(Path.Combine(paths.ModelsDirectory, Defaults.DownloadCacheDirectoryName));

        using var httpClient = CreateHttpClient();

        output.WriteLine($"Models directory: {paths.ModelsDirectory}");
        output.WriteLine($"Proxy: {proxySettings.DisplayText}");
        output.WriteLine();

        foreach (var package in packages)
        {
            await InstallPackageAsync(httpClient, package, force, cancellationToken);
        }
    }

    private async Task InstallPackageAsync(HttpClient httpClient, ModelPackage package, bool force, CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(paths.ModelsDirectory, NormalizePath(package.RelativeDirectory));
        Directory.CreateDirectory(packageRoot);

        output.WriteLine($"> {package.DisplayName}");
        output.WriteLine($"  package: {package.Id}");
        output.WriteLine($"  license: {package.License ?? "see upstream"}");
        output.WriteLine($"  notice: {package.LicenseNotice}");

        var installedAssets = new List<InstalledModelAsset>();
        foreach (var asset in package.Assets)
        {
            var targetRelativePath = CombineUrlPath(asset.TargetRelativePath);
            var targetPath = Path.Combine(packageRoot, NormalizePath(targetRelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (File.Exists(targetPath) && !force)
            {
                output.WriteLine($"  skip  {ToModelsRelativePath(targetPath)}");
            }
            else
            {
                if (force)
                {
                    DeleteIfExists(targetPath);
                    DeleteIfExists(ResolveTempPath(targetPath));
                }

                var urls = BuildCandidateUrls(asset).ToArray();
                await DownloadWithFallbackAsync(httpClient, urls, targetPath, cancellationToken);
                output.WriteLine($"  saved {ToModelsRelativePath(targetPath)}");
            }

            var actualSha256 = await ComputeSha256Async(targetPath, cancellationToken);
            var verified = VerifySha256(asset.ExpectedSha256, actualSha256, targetPath);
            var sizeBytes = new FileInfo(targetPath).Length;
            output.WriteLine($"  sha256 {actualSha256}{(verified ? " verified" : asset.ExpectedSha256 is null ? " recorded" : string.Empty)}");

            installedAssets.Add(new InstalledModelAsset(
                CombineUrlPath(package.RelativeDirectory, targetRelativePath),
                asset.RepositoryId,
                asset.RelativePath,
                asset.ExpectedSha256,
                actualSha256,
                verified,
                sizeBytes));
        }

        var installedAt = DateTimeOffset.UtcNow;
        var existing = manifestStore.Read().Packages.FirstOrDefault(item => string.Equals(item.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        var installedPackage = new InstalledModelPackage(
            package.Id,
            package.ModelKey,
            package.DisplayName,
            package.Segment,
            package.RelativeDirectory,
            package.PrimaryRelativePath,
            ResolvePackageStatus(package, installedAssets),
            package.License,
            package.LicenseNotice,
            installedAssets,
            package.BundleAssets,
            existing?.InstalledAtUtc ?? installedAt,
            installedAt);

        manifestStore.Upsert(installedPackage);
        output.WriteLine($"  wrote {Defaults.ModelInstallManifestFileName}");
        output.WriteLine();
    }

    private async Task DownloadWithFallbackAsync(HttpClient httpClient, IReadOnlyList<Uri> urls, string targetPath, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        foreach (var url in urls)
        {
            try
            {
                await DownloadSingleAsync(httpClient, url, targetPath, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
            {
                errors.Add($"{url}: {exception.Message}");
            }
        }

        throw new InvalidOperationException($"Failed to download {ToModelsRelativePath(targetPath)}. {string.Join(" | ", errors)}");
    }

    private async Task DownloadSingleAsync(HttpClient httpClient, Uri url, string targetPath, CancellationToken cancellationToken)
    {
        var tempPath = ResolveTempPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var totalLength = response.Content.Headers.ContentRange?.Length;
            if (existingBytes > 0 && totalLength.HasValue && totalLength.Value == existingBytes)
            {
                File.Move(tempPath, targetPath, true);
                return;
            }
        }

        response.EnsureSuccessStatusCode();

        var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingBytes > 0 && !append)
        {
            existingBytes = 0;
        }

        var totalBytes = ResolveTotalLength(response, existingBytes);
        var reporter = new ProgressReporter(output, ToModelsRelativePath(targetPath), existingBytes, totalBytes);
        var downloaded = existingBytes;

        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(
            tempPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true))
        {
            var buffer = new byte[1024 * 128];
            while (true)
            {
                var read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                reporter.Report(downloaded);
            }
        }

        reporter.Complete(downloaded);
        File.Move(tempPath, targetPath, true);
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = proxySettings.UseProxy,
            Proxy = proxySettings.Proxy,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tomur", Defaults.Version));
        return client;
    }

    private IEnumerable<Uri> BuildCandidateUrls(DownloadAsset asset)
    {
        if (asset.SourceKind == DownloadSourceKind.DirectUrl)
        {
            yield return new Uri(asset.RelativePath, UriKind.Absolute);
        }
        else
        {
            yield return BuildHuggingFaceResolveUrl(asset.RepositoryId, asset.RelativePath);
        }

        if (asset.AlternateUrls is null)
        {
            yield break;
        }

        foreach (var alternateUrl in asset.AlternateUrls)
        {
            if (Uri.TryCreate(alternateUrl, UriKind.Absolute, out var uri))
            {
                yield return uri;
            }
        }
    }

    private static Uri BuildHuggingFaceResolveUrl(string repositoryId, string relativePath)
    {
        var encodedRepository = string.Join('/', repositoryId.Split('/').Select(Uri.EscapeDataString));
        var encodedPath = string.Join('/', relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri($"https://huggingface.co/{encodedRepository}/resolve/main/{encodedPath}");
    }

    private static long? ResolveTotalLength(HttpResponseMessage response, long existingBytes)
    {
        if (response.Content.Headers.ContentRange?.Length is { } rangeLength)
        {
            return rangeLength;
        }

        if (response.Content.Headers.ContentLength is { } contentLength)
        {
            return contentLength + existingBytes;
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifySha256(string? expectedSha256, string actualSha256, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        var normalizedExpected = expectedSha256.Trim().ToLowerInvariant();
        if (string.Equals(normalizedExpected, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        File.Delete(targetPath);
        throw new InvalidOperationException($"SHA256 mismatch for {targetPath}. Expected {normalizedExpected}, got {actualSha256}.");
    }

    private static string ResolvePackageStatus(ModelPackage package, IReadOnlyList<InstalledModelAsset> installedAssets)
    {
        var requiredPaths = package.BundleAssets
            .Where(static asset => asset.IsRequired)
            .Select(asset => ModelPackage.CombineUrlPath(package.RelativeDirectory, asset.RelativePath))
            .ToArray();

        if (requiredPaths.Length == 0)
        {
            return installedAssets.Count == package.Assets.Count ? "installed" : "partial";
        }

        return requiredPaths.All(requiredPath => installedAssets.Any(asset => string.Equals(asset.Path, requiredPath, StringComparison.OrdinalIgnoreCase)))
            ? "installed"
            : "partial";
    }

    private string ToModelsRelativePath(string path)
    {
        try
        {
            return Path.GetRelativePath(paths.ModelsDirectory, path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private string ResolveTempPath(string targetPath)
    {
        var relativePath = ToModelsRelativePath(targetPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath))).ToLowerInvariant();
        var fileName = $"{Path.GetFileName(targetPath)}.{hash[..12]}.part";
        return Path.Combine(paths.ModelsDirectory, Defaults.DownloadCacheDirectoryName, fileName);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string CombineUrlPath(string path)
    {
        return ModelPackage.CombineUrlPath(path);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class ProgressReporter
    {
        private readonly TextWriter output;
        private readonly string displayPath;
        private readonly long? totalBytes;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastPrintedBytes;
        private TimeSpan lastPrintedAt = TimeSpan.Zero;

        public ProgressReporter(TextWriter output, string displayPath, long startingBytes, long? totalBytes)
        {
            this.output = output;
            this.displayPath = displayPath;
            this.totalBytes = totalBytes;
            lastPrintedBytes = startingBytes;
        }

        public void Report(long downloadedBytes)
        {
            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastPrintedAt < TimeSpan.FromSeconds(2) && totalBytes != downloadedBytes)
            {
                return;
            }

            lastPrintedAt = elapsed;
            lastPrintedBytes = downloadedBytes;

            if (totalBytes is { } total && total > 0)
            {
                var percent = downloadedBytes * 100d / total;
                output.WriteLine($"  get   {displayPath} {FormatBytes(downloadedBytes)} / {FormatBytes(total)} ({percent:0.0}%)");
            }
            else
            {
                output.WriteLine($"  get   {displayPath} {FormatBytes(downloadedBytes)}");
            }
        }

        public void Complete(long downloadedBytes)
        {
            if (downloadedBytes != lastPrintedBytes)
            {
                Report(downloadedBytes);
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }
    }
}
