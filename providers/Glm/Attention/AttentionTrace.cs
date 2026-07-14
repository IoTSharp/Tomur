namespace Tomur.Providers.Glm;

internal sealed class AttentionTrace
{
    public float[] QueryLatent { get; private set; } = [];

    public float[] NormalizedQueryLatent { get; private set; } = [];

    public float[] KeyValueLatent { get; private set; } = [];

    public float[] NormalizedKeyValueLatent { get; private set; } = [];

    public float[] Query { get; private set; } = [];

    public float[] Key { get; private set; } = [];

    public float[] Value { get; private set; } = [];

    public float[] Scores { get; private set; } = [];

    public float[] Probabilities { get; private set; } = [];

    public float[] Output { get; private set; } = [];

    internal void Set(
        float[] queryLatent,
        float[] normalizedQueryLatent,
        float[] keyValueLatent,
        float[] normalizedKeyValueLatent,
        float[] query,
        float[] key,
        float[] value,
        float[] scores,
        float[] probabilities,
        float[] output)
    {
        QueryLatent = queryLatent;
        NormalizedQueryLatent = normalizedQueryLatent;
        KeyValueLatent = keyValueLatent;
        NormalizedKeyValueLatent = normalizedKeyValueLatent;
        Query = query;
        Key = key;
        Value = value;
        Scores = scores;
        Probabilities = probabilities;
        Output = output;
    }

    internal void Clear()
    {
        QueryLatent = [];
        NormalizedQueryLatent = [];
        KeyValueLatent = [];
        NormalizedKeyValueLatent = [];
        Query = [];
        Key = [];
        Value = [];
        Scores = [];
        Probabilities = [];
        Output = [];
    }
}
