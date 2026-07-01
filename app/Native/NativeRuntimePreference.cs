namespace Tomur.Native;

public sealed class NativeRuntimePreference
{
    private readonly AsyncLocal<string?> preferredVariant = new();

    public string? PreferredVariant
        => preferredVariant.Value;

    public void SetPreferredVariant(string? variant)
        => preferredVariant.Value = Normalize(variant);

    public IDisposable UsePreferredVariant(string? variant)
    {
        var previous = preferredVariant.Value;
        preferredVariant.Value = Normalize(variant);
        return new PreferenceScope(this, previous);
    }

    private static string? Normalize(string? variant)
        => string.IsNullOrWhiteSpace(variant) ? null : variant.Trim();

    private sealed class PreferenceScope(NativeRuntimePreference owner, string? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            owner.preferredVariant.Value = previous;
            disposed = true;
        }
    }
}
