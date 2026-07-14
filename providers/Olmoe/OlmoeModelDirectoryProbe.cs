using Tomur.Providers;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.Olmoe;

internal sealed record OlmoeModelProbe(
    ModelProviderManifest Manifest,
    OlmoeModelConfiguration Configuration,
    string ModelDirectory,
    ManagedTokenizer Tokenizer,
    int TensorFileCount,
    SafeTensorCatalog Tensors);

internal static class OlmoeModelDirectoryProbe
{
    private const int MaximumTensorFileCount = 4096;

    public static OlmoeModelProbe Read(LocalModelDescriptor model, string providerId)
    {
        var manifestPath = Path.GetFullPath(model.AbsolutePath);
        if (!Path.GetFileName(manifestPath).Equals(ModelProviderManifest.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Managed model descriptor must point to {ModelProviderManifest.FileName}.");
        }

        if (!ModelProviderManifestReader.TryRead(manifestPath, out var manifest, out var error) || manifest is null)
        {
            throw new InvalidDataException(error ?? "Managed model provider manifest is invalid.");
        }

        if (!manifest.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase) ||
            !manifest.Architecture.Equals("olmoe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Provider '{manifest.Provider}' and architecture '{manifest.Architecture}' do not identify managed OLMoE.");
        }

        if (!manifest.Capabilities.Any(static capability =>
                capability.Equals("completion", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("chat", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Managed OLMoE manifest must declare completion or chat capability.");
        }

        ValidateStorageDeclaration(manifest.Quantization, manifest.QuantizationLayout);
        var modelDirectory = Path.GetDirectoryName(manifestPath)!;
        var configPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.ConfigFile);
        var tokenizerPath = ModelProviderManifestReader.ResolveAssetPath(modelDirectory, manifest.TokenizerFile);
        RejectLinkedAsset(modelDirectory, configPath);
        RejectLinkedAsset(modelDirectory, tokenizerPath);
        var configuration = OlmoeModelConfiguration.Read(configPath);
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
                    $"Managed OLMoE model contains more than {MaximumTensorFileCount} tensor files.");
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
        ValidateRequiredTensors(configuration, tensors, manifest.Quantization, manifest.QuantizationLayout);
        return new OlmoeModelProbe(
            manifest,
            configuration,
            modelDirectory,
            tokenizer,
            tensorPaths.Count,
            tensors);
    }

    private static void ValidateStorageDeclaration(string quantization, string layout)
    {
        var floating = quantization.Equals("f32", StringComparison.OrdinalIgnoreCase) ||
                       quantization.Equals("f16", StringComparison.OrdinalIgnoreCase) ||
                       quantization.Equals("bf16", StringComparison.OrdinalIgnoreCase);
        if (floating && layout.Equals("separate-scales", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (quantization.Equals("int8", StringComparison.OrdinalIgnoreCase) &&
            layout.Equals("rowwise-qs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException(
            $"Managed OLMoE does not support quantization '{quantization}' with layout '{layout}'.");
    }

    private static void ValidateRequiredTensors(
        OlmoeModelConfiguration configuration,
        SafeTensorCatalog tensors,
        string quantization,
        string quantizationLayout)
    {
        RequireTensor(tensors, "model.embed_tokens.weight");
        RequireTensor(tensors, "model.norm.weight");
        RequireTensor(tensors, "lm_head.weight");
        for (var layer = 0; layer < configuration.LayerCount; layer++)
        {
            var prefix = $"model.layers.{layer}.";
            RequireTensor(tensors, $"{prefix}input_layernorm.weight");
            RequireTensor(tensors, $"{prefix}post_attention_layernorm.weight");
            RequireTensor(tensors, $"{prefix}self_attn.q_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.k_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.v_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.o_proj.weight");
            RequireTensor(tensors, $"{prefix}self_attn.q_norm.weight");
            RequireTensor(tensors, $"{prefix}self_attn.k_norm.weight");
            RequireTensor(tensors, $"{prefix}mlp.gate.weight");
            for (var expert = 0; expert < configuration.RoutedExpertCount; expert++)
            {
                var expertPrefix = $"{prefix}mlp.experts.{expert}.";
                foreach (var projection in new[] { "gate_proj.weight", "up_proj.weight", "down_proj.weight" })
                {
                    var weightName = $"{expertPrefix}{projection}";
                    RequireTensor(tensors, weightName);
                    if (quantization.Equals("int8", StringComparison.OrdinalIgnoreCase))
                    {
                        RequireTensor(
                            tensors,
                            ExpertDescriptorLayout.GetScaleTensorName(weightName, quantizationLayout));
                    }
                }
            }
        }
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

    private static void RequireTensor(SafeTensorCatalog tensors, string name)
    {
        if (!tensors.Contains(name))
        {
            throw new InvalidDataException($"Required model tensor is missing: {name}");
        }
    }
}
