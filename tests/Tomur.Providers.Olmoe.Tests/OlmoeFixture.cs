using System.Text.Json;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Olmoe.Tests;

internal sealed class OlmoeFixture : IDisposable
{
    private readonly Dictionary<string, float[]> logicalWeights = new(StringComparer.Ordinal);

    public OlmoeFixture(bool quantizedExperts = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomur-olmoe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        WriteText(ModelProviderManifest.FileName, $$"""
            {
              "schema_version": 1,
              "provider": "managed-olmoe",
              "architecture": "olmoe",
              "display_name": "Tiny OLMoE fixture",
              "config": "config.json",
              "tokenizer": "tokenizer.json",
              "tensor_pattern": "*.safetensors",
              "quantization": "{{(quantizedExperts ? "int8" : "f32")}}",
              "quantization_layout": "{{(quantizedExperts ? "rowwise-qs" : "separate-scales")}}",
              "capabilities": ["completion", "chat"]
            }
            """);
        WriteText("config.json", """
            {
              "model_type": "olmoe",
              "hidden_size": 4,
              "num_hidden_layers": 1,
              "num_attention_heads": 2,
              "num_key_value_heads": 2,
              "num_experts": 2,
              "num_experts_per_tok": 1,
              "intermediate_size": 3,
              "vocab_size": 8,
              "max_position_embeddings": 16,
              "eos_token_id": 2,
              "norm_topk_prob": false,
              "rms_norm_eps": 0.00001,
              "rope_theta": 10000.0
            }
            """);
        WriteText("tokenizer.json", """
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
                "vocab": {
                  "<pad>": 0,
                  "<bos>": 1,
                  "<eos>": 2,
                  "<unk>": 3,
                  "hello": 4,
                  "Tomur": 5,
                  "user": 6,
                  "assistant": 7
                }
              }
            }
            """);

        var tensors = CreateTensors(quantizedExperts, logicalWeights);
        TensorPath = System.IO.Path.Combine(Path, "model.safetensors");
        WriteSafeTensors(TensorPath, tensors);
        var manifestPath = System.IO.Path.Combine(Path, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        Descriptor = new LocalModelDescriptor(
            "tiny-olmoe",
            "Tiny OLMoE fixture",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "olmoe",
            quantizedExperts ? "int8" : "f32",
            ["completion", "chat"]);
    }

    public string Path { get; }
    public string TensorPath { get; }
    public LocalModelDescriptor Descriptor { get; }
    public IReadOnlyDictionary<string, float[]> LogicalWeights => logicalWeights;

    public OlmoeModelProbe ReadProbe()
        => OlmoeModelDirectoryProbe.Read(Descriptor, ManagedOlmoeProvider.ProviderId);

    public ManagedOlmoeModel LoadModel(
        int contextSize = 8,
        long availableMemoryBytes = long.MaxValue,
        CancellationToken cancellationToken = default)
        => ManagedOlmoeModel.Load(
            ReadProbe(),
            contextSize,
            availableMemoryBytes,
            cancellationToken);

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

    private void WriteText(string name, string content)
        => File.WriteAllText(System.IO.Path.Combine(Path, name), content);

    private static IReadOnlyList<TensorFixture> CreateTensors(
        bool quantizedExperts,
        IDictionary<string, float[]> logicalWeights)
    {
        var tensors = new List<TensorFixture>
        {
            Float(logicalWeights, "model.embed_tokens.weight", [8, 4], index => 0.05f * (1 + (index % 7))),
            Float(logicalWeights, "model.norm.weight", [4], _ => 1.0f),
            Float(logicalWeights, "lm_head.weight", [8, 4], index => index / 4 == 4 ? 1.0f : 0.01f * (index / 4)),
            Float(logicalWeights, "model.layers.0.input_layernorm.weight", [4], _ => 1.0f),
            Float(logicalWeights, "model.layers.0.post_attention_layernorm.weight", [4], _ => 1.0f),
            Float(logicalWeights, "model.layers.0.self_attn.q_proj.weight", [4, 4], Identity),
            Float(logicalWeights, "model.layers.0.self_attn.k_proj.weight", [4, 4], Identity),
            Float(logicalWeights, "model.layers.0.self_attn.v_proj.weight", [4, 4], Identity),
            Float(logicalWeights, "model.layers.0.self_attn.o_proj.weight", [4, 4], Identity),
            Float(logicalWeights, "model.layers.0.self_attn.q_norm.weight", [4], _ => 1.0f),
            Float(logicalWeights, "model.layers.0.self_attn.k_norm.weight", [4], _ => 1.0f),
            Float(logicalWeights, "model.layers.0.mlp.gate.weight", [2, 4], index => index < 4 ? 0.2f : -0.2f)
        };

        for (var expert = 0; expert < 2; expert++)
        {
            var prefix = $"model.layers.0.mlp.experts.{expert}.";
            AddExpert(tensors, logicalWeights, $"{prefix}gate_proj.weight", [3, 4], quantizedExperts, expert);
            AddExpert(tensors, logicalWeights, $"{prefix}up_proj.weight", [3, 4], quantizedExperts, expert + 1);
            AddExpert(tensors, logicalWeights, $"{prefix}down_proj.weight", [4, 3], quantizedExperts, expert + 2);
        }

        return tensors;
    }

    private static void AddExpert(
        ICollection<TensorFixture> tensors,
        IDictionary<string, float[]> logicalWeights,
        string name,
        int[] shape,
        bool quantized,
        int seed)
    {
        if (!quantized)
        {
            tensors.Add(Float(logicalWeights, name, shape, index => 0.02f * (1 + ((index + seed) % 5))));
            return;
        }

        var count = shape.Aggregate(1, static (product, value) => checked(product * value));
        var payload = new byte[count];
        var values = new float[count];
        for (var index = 0; index < payload.Length; index++)
        {
            var quantizedValue = (sbyte)(1 + ((index + seed) % 5));
            payload[index] = unchecked((byte)quantizedValue);
            values[index] = quantizedValue * 0.02f;
        }

        logicalWeights.Add(name, values);
        tensors.Add(new TensorFixture(name, "I8", shape, payload));
        tensors.Add(Float(logicalWeights, $"{name}.qs", [shape[0]], _ => 0.02f));
    }

    private static float Identity(int index)
        => index / 4 == index % 4 ? 1.0f : 0.0f;

    private static TensorFixture Float(
        IDictionary<string, float[]> logicalWeights,
        string name,
        int[] shape,
        Func<int, float> value)
    {
        var count = shape.Aggregate(1, static (product, item) => checked(product * item));
        var values = new float[count];
        var bytes = new byte[checked(count * sizeof(float))];
        for (var index = 0; index < count; index++)
        {
            values[index] = value(index);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), values[index]);
        }

        logicalWeights.Add(name, values);
        return new TensorFixture(name, "F32", shape, bytes);
    }

    private static void WriteSafeTensors(string path, IReadOnlyList<TensorFixture> tensors)
    {
        using var headerStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(headerStream))
        {
            writer.WriteStartObject();
            long offset = 0;
            foreach (var tensor in tensors)
            {
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", tensor.Dtype);
                writer.WriteStartArray("shape");
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("data_offsets");
                writer.WriteNumberValue(offset);
                offset = checked(offset + tensor.Payload.Length);
                writer.WriteNumberValue(offset);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        var header = headerStream.ToArray();
        using var stream = File.Create(path);
        using var binary = new BinaryWriter(stream);
        binary.Write((ulong)header.Length);
        binary.Write(header);
        foreach (var tensor in tensors)
        {
            binary.Write(tensor.Payload);
        }
    }

    private sealed record TensorFixture(string Name, string Dtype, int[] Shape, byte[] Payload);
}
