using System.Buffers.Binary;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.M2.Tests;

public sealed class TinyFixtureTests
{
    [Fact]
    public void FixedSeedProducesByteIdenticalFixtureAndOracle()
    {
        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first");
        var second = Path.Combine(root.Path, "second");

        var firstResult = TinyFixtureBundle.Generate(first);
        var secondResult = TinyFixtureBundle.Generate(second);

        Assert.Equal(firstResult.TensorCount, secondResult.TensorCount);
        Assert.Equal(firstResult.OracleCheckpointCount, secondResult.OracleCheckpointCount);
        var firstFiles = Directory.GetFiles(first).Select(static path => Path.GetFileName(path)).Order().ToArray();
        var secondFiles = Directory.GetFiles(second).Select(static path => Path.GetFileName(path)).Order().ToArray();
        Assert.Equal(firstFiles, secondFiles);
        foreach (var fileName in firstFiles)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(first, fileName!)),
                File.ReadAllBytes(Path.Combine(second, fileName!)));
        }
    }

    [Fact]
    public void OracleExposesIndependentKernelAndGenerationCheckpoints()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);

        var oracle = TinyFixtureBundle.ReadOracle(fixturePath);

        Assert.Equal(1, oracle.SchemaVersion);
        Assert.Equal("managed-glm-scalar-reference", oracle.Generator.Name);
        Assert.Equal("F32", oracle.GetCheckpoint("embedding.lookup").DataType);
        Assert.Equal(4, oracle.GetCheckpoint("rms_norm.output").Values.Count);
        Assert.Equal(3, oracle.GetCheckpoint("attention.scores").Values.Count);
        Assert.Equal(2, oracle.Router.ExpertIds.Count);
        Assert.Equal(3, oracle.TeacherForcing.Count);
        Assert.Equal(oracle.GreedyDecode.MaxNewTokens, oracle.GreedyDecode.TokenIds.Count);
        Assert.Contains(oracle.Tokenization, item => item.Text == "本地 AI");
    }

    [Fact]
    public void ChecksumMismatchRejectsTamperedFixture()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        TinyFixtureBundle.Generate(fixturePath);
        var tensorPath = Path.Combine(fixturePath, TinyFixtureFiles.Tensors);
        using (var stream = File.Open(tensorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(value ^ 0xff));
        }

        var exception = Assert.Throws<InvalidDataException>(() => TinyFixtureBundle.Verify(fixturePath));

        Assert.Contains("checksum mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedFixtureLoadsManagedGenerationSession()
    {
        using var root = new TemporaryDirectory();
        var fixturePath = Path.Combine(root.Path, "fixture");
        var provider = new ManagedGlmProvider();
        var result = provider.GenerateFixture(fixturePath);
        var manifestPath = Path.Combine(fixturePath, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var model = new LocalModelDescriptor(
            "m2-fixture",
            "Managed GLM tiny F32 oracle fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);

        using var session = provider.CreateSession(model, new ModelSessionOptions(16));
        Span<byte> headerLengthBytes = stackalloc byte[sizeof(ulong)];
        using (var tensorStream = File.OpenRead(Path.Combine(fixturePath, TinyFixtureFiles.Tensors)))
        {
            tensorStream.ReadExactly(headerLengthBytes);
        }

        Assert.True(result.TensorCount > 0);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(headerLengthBytes) % 8);
        Assert.Equal("managed-glm-generation", session.GetSnapshot().Mode);
    }

    [Fact]
    public void RegistryProvidesFixtureContractWithoutChangingTextProviderContract()
    {
        using var registry = ModelProviderRegistry.CreateDefault();

        var provider = registry.FindFixtureProvider(ManagedGlmProvider.ProviderId);

        Assert.NotNull(provider);
        Assert.IsAssignableFrom<ITextGenerationProvider>(provider);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-m2-{Guid.NewGuid():N}");
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
