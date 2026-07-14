namespace Tomur.Providers.Glm;

internal static class RotaryEmbedding
{
    public static void ApplyInterleaved(
        Span<float> values,
        int position,
        float theta)
    {
        if (values.IsEmpty || (values.Length & 1) != 0)
        {
            throw new ArgumentException(
                "Interleaved RoPE values must have a positive even length.",
                nameof(values));
        }

        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!float.IsFinite(theta) || theta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(theta));
        }

        for (var offset = 0; offset < values.Length; offset += 2)
        {
            var first = values[offset];
            var second = values[offset + 1];
            if (!float.IsFinite(first) || !float.IsFinite(second))
            {
                throw new InvalidDataException(
                    $"RoPE input pair at offset {offset} must be finite.");
            }

            var inverseFrequency = Math.Pow(theta, -(double)offset / values.Length);
            var angle = position * inverseFrequency;
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            values[offset] = (float)((first * cosine) - (second * sine));
            values[offset + 1] = (float)((first * sine) + (second * cosine));
        }
    }
}
