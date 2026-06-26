using System.Text.Json.Serialization;

namespace Tomur.Api;

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("duration_ms")] double DurationMilliseconds,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt);
