using System.Reflection;

namespace Tomur.Config;

public static class Defaults
{
    public const string ProductName = "Tomur";
    public const string DefaultHttpUrl = "http://127.0.0.1:5137";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(Defaults).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "0.1.0-r1"
            : informationalVersion;
    }
}
