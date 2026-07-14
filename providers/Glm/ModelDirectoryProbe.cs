using System.Text.Json;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Glm;

internal sealed record ModelProbe(
    ModelProviderManifest Manifest,
    GlmModelConfiguration Configuration,
    string ModelDirectory,
    string TokenizerPath,
    int TensorFileCount,
    SafeTensorCatalog Tensors);

internal static class ModelDirectoryProbe
{
    private const long MaximumTokenizerBytes = 512L * 1024 * 1024;

    public static ModelProbe Read(LocalModelDescriptor model, string providerId)
    {
        var manifestPath = Path.GetFullPath(model.AbsolutePath);
        if (!string.Equals(Path.GetFileName(manifestPath), ModelProviderManifest.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Managed model descriptor must point to {ModelProviderManifest.FileName}.");
        }

        if (!ModelProviderManifestReader.TryRead(manifestPath, out var manifest, out var error) || manifest is null)
        {
            throw new InvalidDataException(error ?? "Managed model provider manifest is invalid.");
        }

        if (!string.Equals(manifest.Provider, providerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model provider '{manifest.Provider}' does not match loaded provider '{providerId}'.");
        }

        if (!string.Equals(manifest.Architecture, "glm_moe_dsa", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Managed GLM provider does not support architecture '{manifest.Architecture}'.");
        }

        var modelDirectory = Path.GetDirectoryName(manifestPath)!;
        var configPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile);
        var tokenizerPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile);
        var configuration = GlmModelConfiguration.Read(configPath);
        ValidateTokenizer(tokenizerPath);

        var tensorPaths = Directory
            .EnumerateFiles(modelDirectory, manifest.TensorPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tensorPaths.Length == 0)
        {
            throw new InvalidDataException(
                $"No tensor files matched '{manifest.TensorPattern}' in {modelDirectory}.");
        }

        var tensors = SafeTensorCatalog.Read(tensorPaths);
        ValidateRequiredTensors(configuration, tensors);
        return new ModelProbe(
            manifest,
            configuration,
            modelDirectory,
            tokenizerPath,
            tensorPaths.Length,
            tensors);
    }

    private static void ValidateTokenizer(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumTokenizerBytes)
        {
            throw new InvalidDataException(
                $"Tokenizer must exist and be between 1 and {MaximumTokenizerBytes} bytes: {path}");
        }

        using var stream = File.OpenRead(info.FullName);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("model", out var tokenizerModel) ||
            tokenizerModel.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tokenizer JSON must contain a model object.");
        }
    }

    private static void ValidateRequiredTensors(
        GlmModelConfiguration configuration,
        SafeTensorCatalog tensors)
    {
        RequireTensor(tensors, "model.embed_tokens.weight");
        RequireTensor(tensors, "model.norm.weight");
        RequireTensor(tensors, "lm_head.weight");

        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            var prefix = $"model.layers.{layer}.";
            RequireTensor(tensors, $"{prefix}input_layernorm.weight");
            RequireTensor(tensors, $"{prefix}post_attention_layernorm.weight");
            RequireTensor(tensors, $"{prefix}self_attn.q_a_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.q_a_layernorm.weight");
            RequireTensor(tensors, $"{prefix}self_attn.q_b_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.kv_a_proj_with_mqa.weight");
            RequireTensor(tensors, $"{prefix}self_attn.kv_a_layernorm.weight");
            RequireTensor(tensors, $"{prefix}self_attn.kv_b_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.o_proj.weight");

            if (layer < configuration.FirstMoeLayer)
            {
                RequireTensor(tensors, $"{prefix}mlp.gate_proj.weight");
                RequireTensor(tensors, $"{prefix}mlp.up_proj.weight");
                RequireTensor(tensors, $"{prefix}mlp.down_proj.weight");
                continue;
            }

            RequireTensor(tensors, $"{prefix}mlp.gate.weight");
            RequireTensor(tensors, $"{prefix}mlp.gate.e_score_correction_bias");
            if (configuration.SharedExpertCount > 0)
            {
                RequireTensor(tensors, $"{prefix}mlp.shared_experts.gate_proj.weight");
                RequireTensor(tensors, $"{prefix}mlp.shared_experts.up_proj.weight");
                RequireTensor(tensors, $"{prefix}mlp.shared_experts.down_proj.weight");
            }

            for (var expert = 0; expert < configuration.RoutedExpertCount; expert++)
            {
                var expertPrefix = $"{prefix}mlp.experts.{expert}.";
                RequireTensor(tensors, $"{expertPrefix}gate_proj.weight");
                RequireTensor(tensors, $"{expertPrefix}up_proj.weight");
                RequireTensor(tensors, $"{expertPrefix}down_proj.weight");
            }
        }
    }

    private static void RequireTensor(SafeTensorCatalog tensors, string name)
    {
        if (!tensors.Contains(name))
        {
            throw new InvalidDataException($"Required model tensor is missing: {name}");
        }
    }
}
