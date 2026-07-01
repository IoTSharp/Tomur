namespace Tomur.Native;

public sealed record NativeBuildPlan(
    string Rid,
    string Backend,
    bool Clean,
    IReadOnlyList<NativeBuildStep> Steps);

public sealed record NativeBuildStep(
    string Component,
    string SourceDirectory,
    string Preset,
    string BuildPreset,
    bool Required);
