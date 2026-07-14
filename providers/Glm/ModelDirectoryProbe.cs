using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Providers.Glm;

internal sealed record ModelProbe(
    ModelProviderManifest Manifest,
    GlmModelConfiguration Configuration,
    string ModelDirectory,
    string TokenizerPath,
    ManagedTokenizer Tokenizer,
    int TensorFileCount,
    SafeTensorCatalog Tensors);

internal static class ModelDirectoryProbe
{
    private const int MaximumTensorFileCount = 4096;

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

        if (!manifest.Capabilities.Any(static capability =>
                string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Managed GLM model manifest must declare the completion or chat capability.");
        }

        if (!IsSupportedQuantization(manifest.Quantization))
        {
            throw new InvalidDataException(
                $"Managed GLM provider does not support quantization '{manifest.Quantization}'.");
        }

        var modelDirectory = Path.GetDirectoryName(manifestPath)!;
        var configPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile);
        var tokenizerPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile);
        RejectLinkedAsset(modelDirectory, configPath);
        RejectLinkedAsset(modelDirectory, tokenizerPath);
        var configuration = GlmModelConfiguration.Read(configPath);
        var tokenizer = ManagedTokenizer.Read(tokenizerPath);
        if (tokenizer.MaximumTokenId >= configuration.VocabularySize)
        {
            throw new InvalidDataException(
                $"Tokenizer token ID {tokenizer.MaximumTokenId} exceeds model vocabulary size {configuration.VocabularySize}.");
        }

        var tensorPaths = new List<string>();
        foreach (var tensorPath in Directory.EnumerateFiles(
                     modelDirectory,
                     manifest.TensorPattern,
                     SearchOption.TopDirectoryOnly))
        {
            if (tensorPaths.Count >= MaximumTensorFileCount)
            {
                throw new InvalidDataException(
                    $"Managed GLM model contains more than {MaximumTensorFileCount} tensor files.");
            }

            RejectLinkedAsset(modelDirectory, tensorPath);
            tensorPaths.Add(tensorPath);
        }

        if (tensorPaths.Count == 0)
        {
            throw new InvalidDataException(
                $"No tensor files matched '{manifest.TensorPattern}' in {modelDirectory}.");
        }

        tensorPaths.Sort(StringComparer.OrdinalIgnoreCase);
        var tensors = SafeTensorCatalog.Read(tensorPaths);
        ValidateRequiredTensors(configuration, tensors, manifest.Quantization);
        return new ModelProbe(
            manifest,
            configuration,
            modelDirectory,
            tokenizerPath,
            tokenizer,
            tensorPaths.Count,
            tensors);
    }

    private static void RejectLinkedAsset(string modelDirectory, string assetPath)
    {
        var relativePath = Path.GetRelativePath(modelDirectory, assetPath);
        var currentPath = modelDirectory;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath)) &&
                File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"Managed model assets must not resolve through symbolic links or reparse points: {assetPath}");
            }
        }
    }

    private static bool IsSupportedQuantization(string quantization)
        => quantization.Equals("f32", StringComparison.OrdinalIgnoreCase) ||
            quantization.Equals("f16", StringComparison.OrdinalIgnoreCase) ||
            quantization.Equals("bf16", StringComparison.OrdinalIgnoreCase) ||
            quantization.Equals("int8", StringComparison.OrdinalIgnoreCase) ||
            quantization.Equals("int4", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequiredTensors(
        GlmModelConfiguration configuration,
        SafeTensorCatalog tensors,
        string quantization)
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
                if (quantization.Equals("int8", StringComparison.OrdinalIgnoreCase) ||
                    quantization.Equals("int4", StringComparison.OrdinalIgnoreCase))
                {
                    RequireTensor(
                        tensors,
                        ExpertDescriptorLayout.GetScaleTensorName($"{expertPrefix}gate_proj.weight"));
                    RequireTensor(
                        tensors,
                        ExpertDescriptorLayout.GetScaleTensorName($"{expertPrefix}up_proj.weight"));
                    RequireTensor(
                        tensors,
                        ExpertDescriptorLayout.GetScaleTensorName($"{expertPrefix}down_proj.weight"));
                }
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
