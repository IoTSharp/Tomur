namespace Tomur.Providers.Glm;

internal sealed class MoeTrace
{
    public float[] Input { get; private set; } = [];

    public float[] Scores { get; private set; } = [];

    public float[] AdjustedScores { get; private set; } = [];

    public int[] ExpertIds { get; private set; } = [];

    public float[] RoutingWeights { get; private set; } = [];

    public float[] RoutedOutput { get; private set; } = [];

    public float[] SharedOutput { get; private set; } = [];

    public float[] Output { get; private set; } = [];

    internal void Set(
        ReadOnlySpan<float> input,
        MoeWorkspace workspace,
        ReadOnlySpan<float> output)
    {
        Input = input.ToArray();
        Scores = workspace.Scores.ToArray();
        AdjustedScores = workspace.AdjustedScores.ToArray();
        ExpertIds = workspace.SelectedExpertIds.ToArray();
        RoutingWeights = workspace.SelectedWeights.ToArray();
        RoutedOutput = workspace.RoutedOutput.ToArray();
        SharedOutput = workspace.SharedOutput.ToArray();
        Output = output.ToArray();
    }

    internal void Clear()
    {
        Input = [];
        Scores = [];
        AdjustedScores = [];
        ExpertIds = [];
        RoutingWeights = [];
        RoutedOutput = [];
        SharedOutput = [];
        Output = [];
    }
}
