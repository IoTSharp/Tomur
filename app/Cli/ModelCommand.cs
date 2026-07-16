using System.Text.Json;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Models;
using Tomur.Providers;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Cli;

internal static class ModelCommand
{
    public static async Task<int> RunPullAsync(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WritePullHelp();
            return 0;
        }

        if (!TryCreatePaths(args, out var paths, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (!CommandLineHelpers.TryReadOption(args, "--proxy", out var proxyUrl, out error) ||
            !TryReadSelections(args, out var selections, out error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var noProxy = CommandLineHelpers.HasFlag(args, "--no-proxy");
        var force = CommandLineHelpers.HasFlag(args, "--force");
        var dryRun = CommandLineHelpers.HasFlag(args, "--dry-run");
        var hardwareProfile = HardwareProfile.Detect();
        var catalog = new ModelCatalog();
        IReadOnlyList<ModelPackage> selectedPackages;

        try
        {
            selectedPackages = catalog.Select(selections, hardwareProfile);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        PrintHardwareRecommendation(hardwareProfile);
        PrintSelectedPackages(selectedPackages);

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run only; no downloads were started.");
            return 0;
        }

        var proxySettings = ProxySettings.Resolve(proxyUrl, noProxy);
        var installer = new ModelInstallService(paths, proxySettings, Console.Out);
        await installer.InstallAsync(selectedPackages, force, CancellationToken.None);
        Console.WriteLine("Done.");
        return 0;
    }

    public static int RunList(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteListHelp();
            return 0;
        }

        if (!TryCreatePaths(args, out var paths, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (CommandLineHelpers.HasFlag(args, "--catalog"))
        {
            PrintCatalog(paths, includeAll: true);
            return 0;
        }

        if (CommandLineHelpers.HasFlag(args, "--recommended"))
        {
            PrintCatalog(paths, includeAll: false);
            return 0;
        }

        PrintInstalledModels(paths);
        return 0;
    }

    public static int RunPs(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WritePsHelp();
            return 0;
        }

        if (!TryCreatePaths(args, out var paths, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        using var providerRegistry = ModelProviderRegistry.CreateDefault();
        var catalog = new LocalModelCatalog(paths, providerRegistry);
        var models = catalog.ListModelCandidates();
        var runtimeStatus = TryGetRunningRuntimeStatus(paths);
        var session = runtimeStatus?.Session;
        Console.WriteLine($"{Defaults.ProductName} ps");
        Console.WriteLine($"  Models directory: {paths.ModelsDirectory}");
        Console.WriteLine($"  Service: {(runtimeStatus is null ? "not-reachable" : runtimeStatus.Port.Url)}");
        Console.WriteLine();

        if (models.Count == 0)
        {
            Console.WriteLine("No local models are visible.");
            Console.WriteLine("Run `tomur pull recommended` to install the default package set.");
            return 0;
        }

        foreach (var model in models)
        {
            var readiness = runtimeStatus?.ManagedModels.FirstOrDefault(status =>
                    string.Equals(status.ModelId, model.Id, StringComparison.OrdinalIgnoreCase))
                ?? providerRegistry.InspectModel(model, session);
            Console.WriteLine($"- {model.Id}");
            Console.WriteLine($"  package: {model.PackageId ?? "manual-import"}");
            Console.WriteLine($"  status: {readiness.Status}");
            Console.WriteLine($"  provider: {readiness.ProviderId ?? "unavailable"}");
            Console.WriteLine($"  readiness: provider={readiness.ProviderDiscovered}, metadata={readiness.MetadataValid}, assets={readiness.AssetsComplete}, forward={readiness.ForwardVerified}");
            Console.WriteLine($"  runtime: {(readiness.SessionLoaded ? "loaded" : "load-on-first-request")}");
            Console.WriteLine($"  file: {model.RelativePath}");
            Console.WriteLine($"  size: {CommandLineHelpers.FormatBytes(model.SizeBytes)}");
            if (readiness.ResidentBytes is not null)
            {
                Console.WriteLine($"  resident plan: {CommandLineHelpers.FormatBytes(readiness.ResidentBytes.Value)}");
                Console.WriteLine($"  KV plan: {CommandLineHelpers.FormatNullableBytes(readiness.KvBytes)}");
                Console.WriteLine($"  scratch plan: {CommandLineHelpers.FormatNullableBytes(readiness.ScratchBytes)}");
                Console.WriteLine($"  expert cache plan: {CommandLineHelpers.FormatNullableBytes(readiness.ExpertCacheBytes)}");
            }

            foreach (var diagnostic in readiness.Diagnostics)
            {
                Console.WriteLine($"  diagnostic: {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        if (session is { Loaded: true })
        {
            Console.WriteLine();
            Console.WriteLine("Active session:");
            Console.WriteLine($"  model: {session.ModelId}");
            Console.WriteLine($"  provider: {session.ProviderId ?? session.Mode ?? "unknown"}");
            Console.WriteLine($"  busy: {session.Busy}");
            Console.WriteLine($"  context: {session.ContextSize?.ToString() ?? "unknown"}");
            Console.WriteLine($"  resident: {CommandLineHelpers.FormatNullableBytes(session.ResidentBytes)}");
            Console.WriteLine($"  KV: {CommandLineHelpers.FormatNullableBytes(session.KvBytes)}");
            Console.WriteLine($"  scratch: {CommandLineHelpers.FormatNullableBytes(session.ScratchBytes)}");
            Console.WriteLine($"  expert cache: {CommandLineHelpers.FormatNullableBytes(session.ExpertCacheBytes)}");
            Console.WriteLine($"  requests: {session.RequestCount}");
            Console.WriteLine($"  tokens: prompt={session.PromptTokens}, completion={session.CompletionTokens}");
            Console.WriteLine($"  load ms: {session.LoadElapsedMilliseconds?.ToString() ?? "unknown"}");
            Console.WriteLine($"  first token ms: {FormatMetric(session.LastFirstTokenMilliseconds)}");
            Console.WriteLine($"  generation ms: {FormatMetric(session.LastGenerationMilliseconds)}");
            Console.WriteLine($"  output tokens/s: {FormatMetric(session.LastOutputTokensPerSecond)}");
            Console.WriteLine($"  decode tokens/s: {FormatMetric(session.LastDecodeTokensPerSecond)}");
        }

        Console.WriteLine();
        Console.WriteLine("Use /api/runtime/status on the running service for the full readiness and session snapshot.");
        return 0;
    }

    private static string FormatMetric(double? value)
        => value is null ? "unknown" : value.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static RuntimeStatusResponse? TryGetRunningRuntimeStatus(DataPaths paths)
    {
        try
        {
            var configuration = new ConfigurationStore(paths).EnsureConfiguration();
            if (configuration.Status == "error")
            {
                return null;
            }

            var configuredUrl = configuration.Configuration.Server.Urls
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var baseUri))
            {
                return null;
            }

            using var client = new HttpClient
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var response = client.GetAsync("/api/runtime/status").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            var status = JsonSerializer.Deserialize(
                stream,
                AppJsonSerializerContext.Default.RuntimeStatusResponse);
            return status is not null &&
                string.Equals(
                    Path.GetFullPath(status.Paths.DataDirectory),
                    Path.GetFullPath(paths.DataDirectory),
                    StringComparison.OrdinalIgnoreCase)
                ? status
                : null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or IOException or
                UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void PrintCatalog(DataPaths paths, bool includeAll)
    {
        var hardwareProfile = HardwareProfile.Detect();
        var catalog = new ModelCatalog();
        var packages = includeAll ? catalog.GetAll() : catalog.SelectRecommended(hardwareProfile);
        var manifest = new InstallManifestStore(paths).Read();

        PrintHardwareRecommendation(hardwareProfile);
        Console.WriteLine(includeAll ? "Catalog packages:" : "Recommended packages:");

        foreach (var package in packages)
        {
            var installed = manifest.Packages.FirstOrDefault(item => string.Equals(item.Id, package.Id, StringComparison.OrdinalIgnoreCase));
            var flags = ResolveFlags(package);
            var status = installed?.Status ?? "not-installed";

            Console.WriteLine($"- {package.Id} [{package.Segment}] ({flags})");
            Console.WriteLine($"  name: {package.DisplayName}");
            Console.WriteLine($"  status: {status}");
            Console.WriteLine($"  task: {package.Task}");
            Console.WriteLine($"  runtime: {package.Runtime}");
            Console.WriteLine($"  primary: {package.PrimaryFileName}");
            Console.WriteLine($"  minimum memory: {CommandLineHelpers.FormatBytes(package.MinimumMemoryBytes)}");
            Console.WriteLine($"  license: {package.License ?? "see upstream"}");
            Console.WriteLine($"  notice: {package.LicenseNotice}");
        }
    }

    private static void PrintInstalledModels(DataPaths paths)
    {
        var manifest = new InstallManifestStore(paths).Read();
        var visibleModels = new LocalModelCatalog(paths).ListModels();

        Console.WriteLine($"{Defaults.ProductName} list");
        Console.WriteLine($"  Models directory: {paths.ModelsDirectory}");
        Console.WriteLine();

        if (manifest.Packages.Count == 0 && visibleModels.Count == 0)
        {
            Console.WriteLine("No local models are installed yet.");
            Console.WriteLine("Run `tomur list --catalog` to inspect downloadable packages.");
            Console.WriteLine("Run `tomur pull recommended` to install the default package set.");
            return;
        }

        if (manifest.Packages.Count > 0)
        {
            Console.WriteLine("Installed packages:");
            foreach (var package in manifest.Packages)
            {
                Console.WriteLine($"- {package.Id}");
                Console.WriteLine($"  name: {package.DisplayName}");
                Console.WriteLine($"  status: {package.Status}");
                Console.WriteLine($"  primary: {package.PrimaryPath}");
                Console.WriteLine($"  updated: {package.UpdatedAtUtc:O}");
                Console.WriteLine($"  verified assets: {package.Assets.Count(asset => asset.Sha256Verified)} / {package.Assets.Count}");
            }

            Console.WriteLine();
        }

        if (visibleModels.Count > 0)
        {
            Console.WriteLine("Visible model files:");
            foreach (var model in visibleModels)
            {
                Console.WriteLine($"- {model.Id}");
                Console.WriteLine($"  file: {model.RelativePath}");
                Console.WriteLine($"  format: {model.Format}");
                Console.WriteLine($"  family: {model.Family}");
                Console.WriteLine($"  size: {CommandLineHelpers.FormatBytes(model.SizeBytes)}");
                Console.WriteLine($"  verified: {(model.IsVerified ? "yes" : "not-recorded")}");
            }
        }
    }

    private static void PrintHardwareRecommendation(HardwareProfile hardwareProfile)
    {
        Console.WriteLine("Hardware profile:");
        Console.WriteLine($"  OS: {hardwareProfile.OSDescription}");
        Console.WriteLine($"  Architecture: {hardwareProfile.ProcessArchitecture}");
        Console.WriteLine($"  CPU: {hardwareProfile.ProcessorCount} logical processors");
        Console.WriteLine($"  Memory: {CommandLineHelpers.FormatNullableBytes(hardwareProfile.TotalMemoryBytes is null ? null : (long)hardwareProfile.TotalMemoryBytes.Value)}");
        Console.WriteLine($"  Tier: {hardwareProfile.Tier}");
        foreach (var recommendation in hardwareProfile.Recommendations)
        {
            Console.WriteLine($"  Recommendation: {recommendation}");
        }

        Console.WriteLine();
    }

    private static void PrintSelectedPackages(IReadOnlyList<ModelPackage> packages)
    {
        Console.WriteLine("Selected packages:");
        foreach (var package in packages)
        {
            Console.WriteLine($"- {package.Id} [{package.Segment}]");
            Console.WriteLine($"  {package.DisplayName}");
            Console.WriteLine($"  assets: {package.Assets.Count}");
            Console.WriteLine($"  minimum memory: {CommandLineHelpers.FormatBytes(package.MinimumMemoryBytes)}");
            Console.WriteLine($"  license: {package.License ?? "see upstream"}");
            Console.WriteLine($"  notice: {package.LicenseNotice}");
        }
    }

    private static string ResolveFlags(ModelPackage package)
    {
        var flags = new List<string>();
        if (package.Recommended)
        {
            flags.Add("recommended");
        }

        if (package.Optional)
        {
            flags.Add("optional");
        }

        if (package.Research)
        {
            flags.Add("research");
        }

        if (package.IsBundle)
        {
            flags.Add("bundle");
        }

        return flags.Count == 0 ? "manual" : string.Join(", ", flags);
    }

    private static bool TryReadSelections(IReadOnlyList<string> args, out List<string> selections, out string error)
    {
        selections = [];
        error = string.Empty;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--proxy", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--proxy=", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--no-proxy", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--force", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CommandLineHelpers.IsOption(arg))
            {
                error = $"Unsupported option '{arg}'.";
                return false;
            }

            selections.Add(arg);
        }

        return true;
    }

    private static bool TryCreatePaths(IReadOnlyList<string> args, out DataPaths paths, out string error)
    {
        paths = default!;

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out error))
        {
            return false;
        }

        var basePaths = new DataPaths(pathOptions);
        var configuration = new ConfigurationStore(basePaths).EnsureConfiguration();
        if (configuration.Status == "error")
        {
            error = configuration.Message;
            return false;
        }

        paths = basePaths.WithConfiguration(configuration.Configuration);
        error = string.Empty;
        return true;
    }

    private static void WritePullHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} pull

Usage:
  tomur pull [recommended|optional|all|<package-id>...] [--data-dir <path>] [--proxy <url>] [--no-proxy] [--force] [--dry-run]

Examples:
  tomur pull recommended
  tomur pull qwen35-9b-q4km embeddinggemma-300m-q8
  tomur pull recommended --proxy 127.0.0.1:7890

Downloads model assets into the local models directory with resume and checksum validation.
""");
    }

    private static void WriteListHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} list

Usage:
  tomur list [--catalog|--recommended] [--data-dir <path>]

Options:
  --catalog       Show every package in the built-in model catalog.
  --recommended   Show hardware-aware recommended packages.
  --data-dir      Override the local data directory for this process.
""");
    }

    private static void WritePsHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} ps

Usage:
  tomur ps [--data-dir <path>]

Shows model readiness and, when the local service is reachable, the active inference session.
""");
    }
}
