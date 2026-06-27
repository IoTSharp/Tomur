using System.Text.Json.Serialization;

namespace Tomur.Api.Models;

public sealed record ModelCatalogResponse(
    [property: JsonPropertyName("hardware")] ModelHardwareProfileResponse Hardware,
    [property: JsonPropertyName("packages")] IReadOnlyList<ModelCatalogPackageResponse> Packages);

public sealed record ModelHardwareProfileResponse(
    [property: JsonPropertyName("os_description")] string OSDescription,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("processor_count")] int ProcessorCount,
    [property: JsonPropertyName("total_memory_bytes")] ulong? TotalMemoryBytes,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("recommendations")] IReadOnlyList<string> Recommendations);

public sealed record ModelCatalogPackageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model_key")] string ModelKey,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("segment")] string Segment,
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("quantization")] string? Quantization,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("parameter_count")] long? ParameterCount,
    [property: JsonPropertyName("primary_file_name")] string PrimaryFileName,
    [property: JsonPropertyName("recommended")] bool Recommended,
    [property: JsonPropertyName("optional")] bool Optional,
    [property: JsonPropertyName("research")] bool Research,
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("install_status")] string InstallStatus,
    [property: JsonPropertyName("minimum_memory_bytes")] long MinimumMemoryBytes,
    [property: JsonPropertyName("hardware_tier")] string HardwareTier,
    [property: JsonPropertyName("license_notice")] string LicenseNotice,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("assets")] IReadOnlyList<ModelCatalogAssetResponse> Assets,
    [property: JsonPropertyName("bundle_assets")] IReadOnlyList<ModelCatalogBundleAssetResponse> BundleAssets);

public sealed record ModelCatalogAssetResponse(
    [property: JsonPropertyName("repository_id")] string RepositoryId,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("target_relative_path")] string TargetRelativePath,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("source_kind")] string SourceKind);

public sealed record ModelCatalogBundleAssetResponse(
    [property: JsonPropertyName("asset_key")] string AssetKey,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("required")] bool IsRequired,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("quantization")] string? Quantization,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("description")] string? Description);

public sealed record InstalledModelsResponse(
    [property: JsonPropertyName("models_directory")] string ModelsDirectory,
    [property: JsonPropertyName("packages")] IReadOnlyList<InstalledModelPackageResponse> Packages,
    [property: JsonPropertyName("visible_models")] IReadOnlyList<VisibleModelResponse> VisibleModels);

public sealed record InstalledModelPackageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model_key")] string ModelKey,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("segment")] string Segment,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("primary_path")] string PrimaryPath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("license_notice")] string LicenseNotice,
    [property: JsonPropertyName("installed_at_utc")] DateTimeOffset InstalledAtUtc,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("assets")] IReadOnlyList<InstalledModelAssetResponse> Assets);

public sealed record InstalledModelAssetResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source_repository_id")] string SourceRepositoryId,
    [property: JsonPropertyName("source_relative_path")] string SourceRelativePath,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("actual_sha256")] string? ActualSha256,
    [property: JsonPropertyName("sha256_verified")] bool Sha256Verified,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes);

public sealed record VisibleModelResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("package_id")] string? PackageId,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("quantization_level")] string QuantizationLevel,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("verified")] bool IsVerified);
