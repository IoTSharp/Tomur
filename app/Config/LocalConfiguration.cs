using System.Text.Json.Serialization;

namespace Tomur.Config;

public sealed record LocalConfiguration(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("server")] ServerConfiguration Server,
    [property: JsonPropertyName("paths")] PathConfiguration Paths,
    [property: JsonPropertyName("runtime")] RuntimeConfiguration Runtime)
{
    public static LocalConfiguration CreateDefault(DataPaths paths)
    {
        return new LocalConfiguration(
            Defaults.ConfigurationSchemaVersion,
            new ServerConfiguration(Defaults.DefaultHttpUrl),
            new PathConfiguration(
                paths.DataDirectory,
                paths.ModelsDirectory,
                paths.RuntimeDirectory,
                paths.LogsDirectory,
                paths.DatabasePath),
            RuntimeConfiguration.CreateDefault());
    }
}

public sealed record ServerConfiguration(
    [property: JsonPropertyName("urls")] string Urls);

public sealed record PathConfiguration(
    [property: JsonPropertyName("data_directory")] string DataDirectory,
    [property: JsonPropertyName("models_directory")] string ModelsDirectory,
    [property: JsonPropertyName("runtime_directory")] string RuntimeDirectory,
    [property: JsonPropertyName("logs_directory")] string LogsDirectory,
    [property: JsonPropertyName("database_path")] string DatabasePath);

public sealed record RuntimeConfiguration(
    [property: JsonPropertyName("default_backend")] string DefaultBackend,
    [property: JsonPropertyName("accelerator")] RuntimeAcceleratorConfiguration Accelerator)
{
    public static RuntimeConfiguration CreateDefault()
        => new("not_configured", RuntimeAcceleratorConfiguration.CreateDefault());

    public RuntimeConfiguration Normalize()
        => new(
            string.IsNullOrWhiteSpace(DefaultBackend) ? "not_configured" : DefaultBackend.Trim(),
            RuntimeAcceleratorConfiguration.Normalize(Accelerator));
}

public sealed record RuntimeAcceleratorConfiguration(
    [property: JsonPropertyName("preference")] string Preference,
    [property: JsonPropertyName("device_selection_key")] string? DeviceSelectionKey,
    [property: JsonPropertyName("gpu_layers")] int? GpuLayers,
    [property: JsonPropertyName("openvino_device")] string? OpenVinoDevice,
    [property: JsonPropertyName("allow_npu")] bool AllowNpu,
    [property: JsonPropertyName("npu_prefill_chunk")] int? NpuPrefillChunk)
{
    private static readonly string[] SupportedPreferences =
    [
        "auto",
        "cpu",
        "cuda",
        "vulkan",
        "sycl",
        "openvino"
    ];

    public static RuntimeAcceleratorConfiguration CreateDefault()
        => new("auto", null, null, null, false, null);

    public static RuntimeAcceleratorConfiguration Normalize(RuntimeAcceleratorConfiguration? accelerator)
    {
        if (accelerator is null)
        {
            return CreateDefault();
        }

        var preference = NormalizePreference(accelerator.Preference);
        return new RuntimeAcceleratorConfiguration(
            preference,
            NormalizeOptional(accelerator.DeviceSelectionKey),
            NormalizeGpuLayers(accelerator.GpuLayers),
            NormalizeOpenVinoDevice(accelerator.OpenVinoDevice),
            accelerator.AllowNpu,
            NormalizePrefillChunk(accelerator.NpuPrefillChunk));
    }

    private static string NormalizePreference(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "auto"
            : value.Trim().ToLowerInvariant();

        return SupportedPreferences.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : "auto";
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizeGpuLayers(int? value)
    {
        if (value is null)
        {
            return null;
        }

        return Math.Clamp(value.Value, 0, 999);
    }

    private static string? NormalizeOpenVinoDevice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized.All(static character =>
            character is >= 'A' and <= 'Z' ||
            character is >= '0' and <= '9' ||
            character is '.' or '_' or '-')
            ? normalized
            : null;
    }

    private static int? NormalizePrefillChunk(int? value)
    {
        if (value is null || value.Value <= 0)
        {
            return null;
        }

        return Math.Clamp(value.Value, 1, 4096);
    }
}
