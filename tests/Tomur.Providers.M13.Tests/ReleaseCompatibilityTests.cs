using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Xunit;

namespace Tomur.Providers.M13.Tests;

public sealed class ReleaseCompatibilityTests
{
    [Fact]
    public void GlmProviderReferencesTheLockedContractAssemblyVersion()
    {
        var reference = typeof(ManagedGlmProvider)
            .Assembly
            .GetReferencedAssemblies()
            .Single(candidate => candidate.Name == ModelProviderContract.AssemblyName);

        Assert.True(ModelProviderContract.IsCompatible(reference));
        Assert.Equal(ModelProviderContract.Version, 1);
    }

    [Fact]
    public void IncompatibleContractVersionIsRejected()
    {
        var incompatible = new AssemblyName(ModelProviderContract.AssemblyName)
        {
            Version = new Version(99, 0, 0, 0)
        };

        Assert.False(ModelProviderContract.IsCompatible(incompatible));
    }

    [Fact]
    public void ReleaseManifestVerifiesProviderChecksum()
    {
        using var directory = new TemporaryDirectory();
        var providerPath = Path.Combine(directory.Path, "Tomur.Providers.Glm.dll");
        File.WriteAllBytes(providerPath, [1, 2, 3, 5, 8]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(providerPath)));
        var manifestPath = Path.Combine(directory.Path, ManagedProviderReleaseManifest.FileName);
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "schema_version": 1,
              "contract": { "assembly": "Tomur", "version": "1", "assembly_version": "0.5.0.0" },
              "providers": [
                {
                  "id": "managed-glm",
                  "assembly": "Tomur.Providers.Glm.dll",
                  "version": "0.5.0.0",
                  "sha256": "{{hash}}"
                }
              ]
            }
            """);

        Assert.True(ManagedProviderReleaseManifest.TryRead(manifestPath, out var manifest, out var error), error);
        Assert.NotNull(manifest);
        Assert.True(
            manifest!.TryVerify(directory.Path, Path.GetFileName(providerPath), out var entry, out error),
            error);
        Assert.Equal("managed-glm", entry!.Id);

        File.AppendAllText(providerPath, "tampered");
        Assert.False(manifest.TryVerify(directory.Path, Path.GetFileName(providerPath), out _, out error));
        Assert.Contains("checksum", Assert.IsType<string>(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidReleaseManifestIsRejectedBeforeProviderLoad()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = Path.Combine(directory.Path, ManagedProviderReleaseManifest.FileName);
        File.WriteAllText(
            manifestPath,
            "{\"schema_version\":1,\"contract\":{\"assembly\":\"Tomur\",\"version\":\"99\",\"assembly_version\":\"0.5.0.0\"},\"providers\":[]}");

        Assert.False(ManagedProviderReleaseManifest.TryRead(manifestPath, out _, out var error));
        Assert.Contains("contract", Assert.IsType<string>(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseManifestAcceptsUtf8BomFromPublishOutput()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = Path.Combine(directory.Path, ManagedProviderReleaseManifest.FileName);
        File.WriteAllText(
            manifestPath,
            "{\"schema_version\":1,\"contract\":{\"assembly\":\"Tomur\",\"version\":\"1\",\"assembly_version\":\"0.5.0.0\"},\"providers\":[{\"id\":\"managed-glm\",\"assembly\":\"Tomur.Providers.Glm.dll\",\"version\":\"1.0.0.0\",\"sha256\":\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"}]}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.True(ManagedProviderReleaseManifest.TryRead(manifestPath, out var manifest, out var error), error);
        Assert.NotNull(manifest);
        Assert.Equal("managed-glm", Assert.Single(manifest!.Providers).Id);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tomur-m13-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
