namespace Tomur.Providers.Glm;

internal static class MoeRouter
{
    public static void Route(
        ManagedGlmModel model,
        int layer,
        ReadOnlySpan<float> input,
        MoeWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);
        var configuration = model.Configuration;
        workspace.EnsureCompatible(configuration);
        if (layer < configuration.FirstMoeLayer || layer >= configuration.LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} is not a MoE layer.");
        }

        if (configuration.ExpertGroupCount != 1 || configuration.ExpertGroupsPerToken != 1)
        {
            throw new InvalidDataException("The current MoE router requires n_group=1 and topk_group=1.");
        }

        if (input.Length != configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"MoE router input must contain {configuration.HiddenSize} elements.",
                nameof(input));
        }

        var prefix = $"model.layers.{layer}.mlp.gate";
        ScalarKernels.MatVec(
            model.GetResidentWeight($"{prefix}.weight"),
            configuration.RoutedExpertCount,
            configuration.HiddenSize,
            configuration.HiddenSize,
            input,
            workspace.AdjustedScores);
        ScalarKernels.Sigmoid(workspace.AdjustedScores, workspace.Scores);

        var correction = model.GetResidentWeight($"{prefix}.e_score_correction_bias");
        for (var expertId = 0; expertId < configuration.RoutedExpertCount; expertId++)
        {
            workspace.AdjustedScores[expertId] = workspace.Scores[expertId] + correction[expertId];
        }

        ScalarKernels.TopK(
            workspace.AdjustedScores,
            configuration.ExpertsPerToken,
            workspace.SelectedExpertIds,
            workspace.SelectedWeights);

        double denominator = 0;
        for (var route = 0; route < configuration.ExpertsPerToken; route++)
        {
            var score = workspace.Scores[workspace.SelectedExpertIds[route]];
            workspace.SelectedWeights[route] = score;
            denominator += score;
        }

        if (configuration.NormalizeTopKProbabilities &&
            (!double.IsFinite(denominator) || denominator <= 0))
        {
            throw new InvalidDataException("MoE router selected probabilities have an invalid normalization sum.");
        }

        for (var route = 0; route < configuration.ExpertsPerToken; route++)
        {
            var weight = workspace.SelectedWeights[route];
            if (configuration.NormalizeTopKProbabilities)
            {
                weight = (float)(weight / denominator);
            }

            workspace.SelectedWeights[route] = weight * configuration.RoutedScalingFactor;
        }
    }
}
