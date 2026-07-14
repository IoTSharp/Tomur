using System.Text.Json;
using Tomur.Runtime;

namespace Tomur.Providers.M1.Tests;

internal sealed class ManagedModelFixture : IDisposable
{
    private static readonly (string Name, long[] Shape)[] RequiredTensors =
    [
        ("model.embed_tokens.weight", [4, 4]),
        ("model.norm.weight", [4]),
        ("lm_head.weight", [4, 4]),
        ("model.layers.0.input_layernorm.weight", [4]),
        ("model.layers.0.post_attention_layernorm.weight", [4]),
        ("model.layers.0.self_attn.q_a_proj.weight", [1, 4]),
        ("model.layers.0.self_attn.q_a_layernorm.weight", [1]),
        ("model.layers.0.self_attn.q_b_proj.weight", [2, 1]),
        ("model.layers.0.self_attn.kv_a_proj_with_mqa.weight", [2, 4]),
        ("model.layers.0.self_attn.kv_a_layernorm.weight", [1]),
        ("model.layers.0.self_attn.kv_b_proj.weight", [2, 1]),
        ("model.layers.0.self_attn.o_proj.weight", [4, 1]),
        ("model.layers.0.mlp.gate_proj.weight", [4, 4]),
        ("model.layers.0.mlp.up_proj.weight", [4, 4]),
        ("model.layers.0.mlp.down_proj.weight", [4, 4])
    ];

    public ManagedModelFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"tomur-m1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string ManifestPath => Path.Combine(Root, ModelProviderManifest.FileName);

    public LocalModelDescriptor CreateValidModel(
        bool omitRequiredTensor = false,
        bool outOfBoundsOffset = false,
        bool unsupportedDataType = false)
    {
        File.WriteAllText(ManifestPath, """
            {
              "schema_version": 1,
              "provider": "managed-glm",
              "architecture": "glm_moe_dsa",
              "display_name": "M1 tiny metadata fixture",
              "config": "config.json",
              "tokenizer": "tokenizer.json",
              "tensor_pattern": "*.safetensors",
              "quantization": "f32",
              "capabilities": ["completion", "chat"]
            }
            """);
        File.WriteAllText(Path.Combine(Root, "config.json"), """
            {
              "hidden_size": 4,
              "num_hidden_layers": 1,
              "num_attention_heads": 1,
              "n_routed_experts": 1,
              "num_experts_per_tok": 1,
              "moe_intermediate_size": 4,
              "intermediate_size": 4,
              "first_k_dense_replace": 1,
              "q_lora_rank": 1,
              "kv_lora_rank": 1,
              "qk_nope_head_dim": 1,
              "qk_rope_head_dim": 1,
              "v_head_dim": 1,
              "n_shared_experts": 0,
              "vocab_size": 4,
              "n_group": 1,
              "topk_group": 1,
              "rms_norm_eps": 0.00001,
              "routed_scaling_factor": 1.0,
              "rope_parameters": { "rope_theta": 10000.0 }
            }
            """);
        File.WriteAllText(Path.Combine(Root, "tokenizer.json"), """
            {
              "added_tokens": [
                { "id": 0, "content": "<pad>", "special": true },
                { "id": 1, "content": "<bos>", "special": true },
                { "id": 2, "content": "<eos>", "special": true },
                { "id": 3, "content": "<unk>", "special": true }
              ],
              "pre_tokenizer": { "type": "WhitespaceSplit" },
              "model": {
                "type": "WordLevel",
                "unk_token": "<unk>",
                "vocab": { "<pad>": 0, "<bos>": 1, "<eos>": 2, "<unk>": 3 }
              }
            }
            """);

        var tensors = omitRequiredTensor
            ? RequiredTensors.Where(static tensor => tensor.Name != "lm_head.weight").ToArray()
            : RequiredTensors;
        WriteSafeTensors(
            Path.Combine(Root, "model.safetensors"),
            tensors,
            outOfBoundsOffset,
            unsupportedDataType);
        return CreateDescriptor();
    }

    public LocalModelDescriptor CreateDescriptor()
        => new(
            "m1-fixture",
            "M1 tiny metadata fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            ManifestPath,
            new FileInfo(ManifestPath).Length,
            File.GetLastWriteTimeUtc(ManifestPath),
            "managed-model",
            "glm_moe_dsa",
            "f32",
            ["completion", "chat"]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteSafeTensors(
        string path,
        IReadOnlyList<(string Name, long[] Shape)> tensors,
        bool outOfBoundsOffset,
        bool unsupportedDataType)
    {
        using var headerStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(headerStream))
        {
            writer.WriteStartObject();
            long offset = 0;
            for (var index = 0; index < tensors.Count; index++)
            {
                var tensor = tensors[index];
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", unsupportedDataType && index == 0 ? "F64" : "F32");
                writer.WriteStartArray("shape");
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("data_offsets");
                writer.WriteNumberValue(offset);
                var elementCount = tensor.Shape.Aggregate(1L, static (product, value) => checked(product * value));
                var end = checked(offset + checked(elementCount * sizeof(float)));
                if (outOfBoundsOffset && index == tensors.Count - 1)
                {
                    end += sizeof(float);
                }

                writer.WriteNumberValue(end);
                writer.WriteEndArray();
                writer.WriteEndObject();
                offset = checked(offset + checked(elementCount * sizeof(float)));
            }

            writer.WriteEndObject();
        }

        var header = headerStream.ToArray();
        using var stream = File.Create(path);
        using var binaryWriter = new BinaryWriter(stream);
        binaryWriter.Write((ulong)header.Length);
        binaryWriter.Write(header);
        foreach (var tensor in tensors)
        {
            var elementCount = tensor.Shape.Aggregate(1L, static (product, value) => checked(product * value));
            for (long index = 0; index < elementCount; index++)
            {
                binaryWriter.Write(1.0f);
            }
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-provider-{Guid.NewGuid():N}");
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

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string name;
    private readonly string? originalValue;

    public EnvironmentVariableScope(string name, string? value)
    {
        this.name = name;
        originalValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(name, originalValue);
}
