namespace Tomur.Providers.Glm;

internal static class MoeExecutor
{
    public static async ValueTask RunTokenAsync(
        ManagedGlmModel model,
        int layer,
        ReadOnlyMemory<float> input,
        ExpertCache cache,
        MoeWorkspace workspace,
        Memory<float> destination,
        CancellationToken cancellationToken = default,
        MoeTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(workspace);
        Validate(model, layer, input, cache, workspace, destination);
        trace?.Clear();
        cancellationToken.ThrowIfCancellationRequested();
        Route(model, layer, input, workspace);

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var acquisition = cache.AcquireLayerAsync(
            layer,
            workspace.SelectedExpertIdMemory,
            operation.Token).AsTask();
        ExpertLeaseBatch? leases = null;
        try
        {
            RunSharedExpert(model, layer, input, workspace);
            cancellationToken.ThrowIfCancellationRequested();
            leases = await acquisition.ConfigureAwait(false);
            await leases.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RunRoutedExperts(input, workspace, leases, cancellationToken);
            Commit(workspace, destination);
            CaptureTrace(trace, input, workspace, destination);
        }
        catch
        {
            operation.Cancel();
            if (leases is null)
            {
                try
                {
                    leases = await acquisition.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            trace?.Clear();
            throw;
        }
        finally
        {
            leases?.Dispose();
        }
    }

    private static void Validate(
        ManagedGlmModel model,
        int layer,
        ReadOnlyMemory<float> input,
        ExpertCache cache,
        MoeWorkspace workspace,
        Memory<float> destination)
    {
        var configuration = model.Configuration;
        if (layer < configuration.FirstMoeLayer || layer >= configuration.LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), $"Layer {layer} is not a MoE layer.");
        }

        if (input.Length != configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"MoE input must contain {configuration.HiddenSize} elements.",
                nameof(input));
        }

        if (destination.Length != configuration.HiddenSize)
        {
            throw new ArgumentException(
                $"MoE destination must contain {configuration.HiddenSize} elements.",
                nameof(destination));
        }

        if (input.Span.Overlaps(destination.Span))
        {
            throw new ArgumentException("MoE input and destination cannot overlap.", nameof(destination));
        }

        workspace.EnsureCompatible(configuration);
        if (!ReferenceEquals(cache.Layout, model.ExpertLayout))
        {
            throw new ArgumentException("Expert cache does not belong to this model.", nameof(cache));
        }
    }

    private static void Route(
        ManagedGlmModel model,
        int layer,
        ReadOnlyMemory<float> input,
        MoeWorkspace workspace)
        => MoeRouter.Route(model, layer, input.Span, workspace);

    private static void RunSharedExpert(
        ManagedGlmModel model,
        int layer,
        ReadOnlyMemory<float> input,
        MoeWorkspace workspace)
    {
        var configuration = model.Configuration;
        var destination = workspace.SharedOutput;
        destination.Clear();
        if (configuration.SharedExpertCount == 0)
        {
            return;
        }

        var intermediateSize = checked(
            configuration.SharedExpertCount * configuration.MoeIntermediateSize);
        var activations = workspace.GetExpertActivations(intermediateSize);
        var gate = activations[..intermediateSize];
        var up = activations.Slice(intermediateSize, intermediateSize);
        var activated = activations.Slice(checked(intermediateSize * 2), intermediateSize);
        var prefix = $"model.layers.{layer}.mlp.shared_experts.";
        model.MultiplyResidentWeightPair(
            $"{prefix}gate_proj.weight",
            $"{prefix}up_proj.weight",
            input.Span,
            gate,
            up);
        ScalarKernels.SiLU(gate, activated);
        ScalarKernels.Multiply(activated, up, gate);
        model.MultiplyResidentWeight($"{prefix}down_proj.weight", gate, destination);
    }

    private static void RunRoutedExperts(
        ReadOnlyMemory<float> input,
        MoeWorkspace workspace,
        ExpertLeaseBatch leases,
        CancellationToken cancellationToken)
    {
        if (leases.Count != workspace.ExpertsPerToken)
        {
            throw new InvalidOperationException(
                $"MoE route selected {workspace.ExpertsPerToken} experts, but acquired {leases.Count} leases.");
        }

        var routed = workspace.RoutedOutput;
        routed.Clear();
        for (var route = 0; route < leases.Count; route++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expertOutput = workspace.ExpertOutput;
            leases[route].Run(input.Span, workspace, expertOutput);
            var weight = workspace.SelectedWeights[route];
            for (var component = 0; component < routed.Length; component++)
            {
                routed[component] += weight * expertOutput[component];
            }
        }
    }

    private static void Commit(MoeWorkspace workspace, Memory<float> destination)
    {
        var completed = workspace.ExpertOutput;
        ScalarKernels.Add(workspace.RoutedOutput, workspace.SharedOutput, completed);
        for (var index = 0; index < completed.Length; index++)
        {
            if (!float.IsFinite(completed[index]))
            {
                throw new InvalidDataException($"MoE output at index {index} is not finite.");
            }
        }

        completed.CopyTo(destination.Span);
    }

    private static void CaptureTrace(
        MoeTrace? trace,
        ReadOnlyMemory<float> input,
        MoeWorkspace workspace,
        Memory<float> destination)
        => trace?.Set(input.Span, workspace, destination.Span);
}
