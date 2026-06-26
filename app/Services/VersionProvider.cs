using System.Text.Json.Serialization;
using Tomur.Config;

namespace Tomur.Services;

public sealed class VersionProvider
{
    public VersionResponse GetVersionResponse()
    {
        return new VersionResponse(Defaults.Version);
    }
}

public sealed record VersionResponse(
    [property: JsonPropertyName("version")] string Version);
