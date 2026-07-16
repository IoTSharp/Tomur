using System.Globalization;
using System.Numerics;

namespace Tomur.Providers.Glm;

internal sealed record KernelExecutionOptions(
    bool EnableSimd = true,
    bool EnableParallel = true,
    int ParallelRowThreshold = 256,
    int ParallelWorkThreshold = 262144,
    int MaxDegreeOfParallelism = 0)
{
    public const string ModeEnvironmentVariable = "TOMUR_GLM_KERNEL_MODE";
    public const string ParallelismEnvironmentVariable = "TOMUR_GLM_PARALLELISM";
    public const string ParallelRowThresholdEnvironmentVariable = "TOMUR_GLM_PARALLEL_ROW_THRESHOLD";
    public const string ParallelWorkThresholdEnvironmentVariable = "TOMUR_GLM_PARALLEL_WORK_THRESHOLD";

    public int EffectiveMaxDegreeOfParallelism => MaxDegreeOfParallelism <= 0
        ? Environment.ProcessorCount
        : Math.Min(MaxDegreeOfParallelism, Environment.ProcessorCount);

    public int SimdWidthBits => EnableSimd && Vector.IsHardwareAccelerated
        ? Vector<float>.Count * sizeof(float) * 8
        : 0;

    public static KernelExecutionOptions FromEnvironment()
    {
        var mode = Environment.GetEnvironmentVariable(ModeEnvironmentVariable)?.Trim();
        var enableSimd = !string.Equals(mode, "scalar", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(mode) &&
            !string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "scalar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{ModeEnvironmentVariable} must be 'auto' or 'scalar'.");
        }

        var parallelism = ReadNonNegativeInt(ParallelismEnvironmentVariable, 0);
        return new KernelExecutionOptions(
            EnableSimd: enableSimd,
            EnableParallel: enableSimd && parallelism != 1,
            ParallelRowThreshold: ReadPositiveInt(ParallelRowThresholdEnvironmentVariable, 256),
            ParallelWorkThreshold: ReadPositiveInt(ParallelWorkThresholdEnvironmentVariable, 262144),
            MaxDegreeOfParallelism: parallelism);
    }

    public void Validate()
    {
        if (ParallelRowThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ParallelRowThreshold));
        }

        if (ParallelWorkThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ParallelWorkThreshold));
        }

        if (MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        }
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        var value = ReadNonNegativeInt(name, fallback);
        if (value == 0)
        {
            throw new InvalidDataException($"{name} must be a positive integer.");
        }

        return value;
    }

    private static int ReadNonNegativeInt(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new InvalidDataException($"{name} must be a non-negative integer.");
        }

        return value;
    }

}
