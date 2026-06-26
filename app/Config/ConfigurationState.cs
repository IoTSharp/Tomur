using System.Text.Json.Serialization;

namespace Tomur.Config;

public sealed record ConfigurationState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("recovered_path")] string? RecoveredPath,
    [property: JsonPropertyName("configuration")] LocalConfiguration Configuration)
{
    public bool HasWarning => Status is "recovered" or "upgraded";
}
