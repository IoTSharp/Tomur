using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.SpaServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tomur.Api;
using Tomur.Agents;
using Tomur.Config;
using Tomur.Conversations;
using Tomur.Diagnostics;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Providers;
using Tomur.Runtime;
using Tomur.Serialization;
using Tomur.Services;
using Tomur.Storage;

namespace Tomur.Cli;

internal static class ServeCommand
{
    public static async Task<int> RunInteractiveLaunchAsync(params string[] args)
    {
        Console.WriteLine($"{Defaults.ProductName} {Defaults.Version}");
        Console.WriteLine("Starting local API and opening the workspace...");
        Console.WriteLine();
        return await RunAsync(
            args,
            configureServiceLifetime: false,
            openBrowser: true,
            useTray: OperatingSystem.IsWindows() && !CommandLineHelpers.HasFlag(args, "--no-tray"));
    }

    public static async Task<int> RunAsync(
        string[] args,
        bool configureServiceLifetime,
        bool openBrowser,
        bool useTray = false)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!TryBuildHostState(args, out var state, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var app = BuildApp(args, state, configureServiceLifetime);
        TrayApplication? tray = null;
        var serviceUrl = ResolveFirstUrl(state.ServerOptions.Urls);
        if (openBrowser)
        {
            app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(serviceUrl));
        }

        if (useTray)
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                tray = TrayApplication.TryStart(serviceUrl, () => _ = app.StopAsync());
                if (tray is not null)
                {
                    ConsoleWindow.HideIfOwnedByCurrentProcess();
                }
            });

            app.Lifetime.ApplicationStopped.Register(() => tray?.Dispose());
        }

        try
        {
            await app.RunAsync();
            return 0;
        }
        finally
        {
            tray?.Dispose();
        }
    }

    public static string ResolveServiceUrls(string[] args, LocalConfiguration configuration, out string error)
    {
        if (!CommandLineHelpers.TryReadOption(args, "--urls", out var argumentUrls, out error))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(argumentUrls))
        {
            error = string.Empty;
            return argumentUrls;
        }

        var environmentUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(environmentUrls))
        {
            error = string.Empty;
            return environmentUrls;
        }

        error = string.Empty;
        return configuration.Server.Urls;
    }

    private static WebApplication BuildApp(string[] args, HostState state, bool configureServiceLifetime)
    {
        var builder = WebApplication.CreateBuilder(FilterHostArgs(args));

        if (configureServiceLifetime)
        {
            builder.Host.UseSystemd();
            builder.Host.UseWindowsService(options =>
            {
                options.ServiceName = Defaults.WindowsServiceName;
            });
        }

        // Capture host logs into a bounded ring buffer and fan them out to SSE subscribers.
        // Registered the idiomatic way: the buffer is a DI singleton and the provider is a
        // DI-composed ILoggerProvider (the LoggerFactory discovers and owns it). Both are
        // available before builder.Build() drives the logging pipeline.
        builder.Services.AddSingleton(_ => new LogBroadcastService(capacity: Defaults.LogBufferCapacity));
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new RingBufferLoggerProvider(sp.GetRequiredService<LogBroadcastService>()));

        // Structured request logging via the standard middleware (added to the pipeline below).
        builder.Services.AddHttpLogging(options =>
        {
            options.LoggingFields =
                HttpLoggingFields.RequestMethod |
                HttpLoggingFields.RequestPath |
                HttpLoggingFields.ResponseStatusCode |
                HttpLoggingFields.Duration;
        });

        // Shape what the ring buffer captures: keep first-party (Tomur.*) and lifetime/request
        // logs, but hold the rest of the ASP.NET framework at Warning so routing/static-file
        // noise stays out. HttpLogging is the single source of per-request lines.
        builder.Logging.AddFilter<RingBufferLoggerProvider>("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter<RingBufferLoggerProvider>("Microsoft.AspNetCore.HttpLogging", LogLevel.Information);
        builder.Logging.AddFilter<RingBufferLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(state.Paths);
        builder.Services.AddSingleton(state.ServerOptions);
        builder.Services.AddSingleton(state.ConfigurationStore);
        builder.Services.AddSingleton<LocalDatabaseInitializer>();
        builder.Services.AddSingleton<ConversationStore>();
        builder.Services.AddSingleton<ConversationOrchestrationService>();
        builder.Services.AddSingleton<INativeBundleProbe>(state.NativeBundleProbe);
        builder.Services.AddSingleton<INativeBundlePreparer, NativeBundlePreparer>();
        builder.Services.AddSingleton<INativeLibraryResolver>(provider =>
            new NativeLibraryResolver(provider.GetRequiredService<INativeBundleProbe>()));
        builder.Services.AddSingleton<INativeLibraryLoader>(provider =>
            new NativeLibraryLoader(
                provider.GetRequiredService<INativeLibraryResolver>(),
                provider.GetRequiredService<ILogger<NativeLibraryLoader>>()));
        builder.Services.AddSingleton<NativeRuntimePreference>();
        builder.Services.AddSingleton<LlamaImportResolver>();
        builder.Services.AddSingleton<LlamaBackendInitializer>();
        builder.Services.AddSingleton<HardwareAccelerationService>();
        builder.Services.AddSingleton(_ => ModelProviderRegistry.CreateDefault());
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LocalInferenceService>();
        builder.Services.AddSingleton<LocalChatClient>();
        builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(provider =>
            provider.GetRequiredService<LocalChatClient>());
        builder.Services.AddSingleton<MultimodalRuntimeService>();
        builder.Services.AddSingleton<MultimodalExecutionService>();
        builder.Services.AddSingleton<IsolatedImageGenerationService>();
        builder.Services.AddSingleton<AgentEventLog>();
        builder.Services.AddSingleton<FileIndexStore>();
        builder.Services.AddSingleton<AgentTelemetryExporterOptions>();
        builder.Services.AddSingleton<AgentTelemetry>();
        builder.Services.AddSingleton<AgentRuntimeService>();
        builder.Services.AddSingleton<ToolFactory>();
        builder.Services.AddSingleton<ToolExecutionService>();
        builder.Services.AddSingleton<ToolInvoker>();
        builder.Services.AddSingleton<RuntimeDiagnosticsProvider>();
        builder.Services.AddSingleton<LocalModelCatalog>();
        builder.Services.AddSingleton<VersionProvider>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });
        ConfigureAgentTelemetry(builder);

        builder.WebHost.UseUrls(state.ServerOptions.Urls);

        var app = builder.Build();
        app.UseHttpLogging();
        app.MapApiRoutes();
        app.MapWhen(ShouldUseSpa, spaApp =>
        {
            var spaFileProvider = CreateSpaFileProvider(app.Environment);
            spaApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = spaFileProvider
            });
            spaApp.UseSpa(spa =>
            {
                spa.Options.DefaultPageStaticFileOptions = new StaticFileOptions
                {
                    FileProvider = spaFileProvider
                };
                spa.Options.DefaultPage = "/index.html";

                if (app.Environment.IsDevelopment())
                {
                    spa.UseProxyToSpaDevelopmentServer("http://127.0.0.1:5173");
                }
            });
        });
        return app;
    }

    private static void ConfigureAgentTelemetry(WebApplicationBuilder builder)
    {
        var exporterOptions = new AgentTelemetryExporterOptions();
        if (!exporterOptions.Enabled)
        {
            return;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: Defaults.ProductName,
                serviceVersion: Defaults.Version))
            .WithTracing(tracing =>
            {
                tracing.AddSource(AgentTelemetry.SourceName);
                tracing.AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(exporterOptions.Endpoint!);
                    if (!string.IsNullOrWhiteSpace(exporterOptions.Headers))
                    {
                        options.Headers = exporterOptions.Headers;
                    }
                });
            });
    }

    private static bool ShouldUseSpa(HttpContext context)
    {
        var path = context.Request.Path;
        return !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    private static IFileProvider CreateSpaFileProvider(IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return new PhysicalFileProvider(ResolveWebRootPath());
        }

        return new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
    }

    private static string ResolveWebRootPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "wwwroot"))
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static bool TryBuildHostState(string[] args, out HostState state, out string error)
    {
        state = default!;

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            error = pathError;
            return false;
        }

        var basePaths = new DataPaths(pathOptions);
        var configurationStore = new ConfigurationStore(basePaths);
        var configuration = configurationStore.EnsureConfiguration();
        if (configuration.Status == "error")
        {
            error = configuration.Message;
            return false;
        }

        var paths = basePaths.WithConfiguration(configuration.Configuration);
        using var providerRegistry = ModelProviderRegistry.CreateDefault();
        var managedProviderAvailable = providerRegistry.Status.Loaded.Count > 0;
        var prepareResult = new NativeBundlePreparer(paths).Prepare();
        if (prepareResult.Status == "error" && !managedProviderAvailable)
        {
            error = $"Native runtime bundle could not be prepared. {prepareResult.Message}";
            return false;
        }

        var serviceUrls = ResolveServiceUrls(args, configuration.Configuration, out var urlError);
        if (!string.IsNullOrWhiteSpace(urlError))
        {
            error = urlError;
            return false;
        }

        var serverOptions = new ServerOptions
        {
            Urls = serviceUrls
        };
        var nativeBundleProbe = new NativeBundleProbe(paths);
        var startupStatus = new RuntimeDiagnosticsProvider(configurationStore, paths, nativeBundleProbe, serverOptions).GetRuntimeStatus();
        var blockingDiagnostics = startupStatus.Diagnostics
            .Where(item =>
                item.Severity == "error" &&
                (!managedProviderAvailable || !IsNativeOnlyStartupDiagnostic(item.Name)))
            .ToArray();
        if (blockingDiagnostics.Length > 0)
        {
            var diagnostics = blockingDiagnostics
                .Select(static item => $"  - {item.Name}: {item.Message}");
            error = "Local runtime state could not be initialized." + Environment.NewLine + string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        state = new HostState(paths, configurationStore, serverOptions, nativeBundleProbe);
        error = string.Empty;
        return true;
    }

    private static bool IsNativeOnlyStartupDiagnostic(string name)
        => name.Equals("runtime", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("acceleration", StringComparison.OrdinalIgnoreCase);

    private static string[] FilterHostArgs(string[] args)
    {
        var filtered = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--open", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--no-tray", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--service", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--urls", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            filtered.Add(arg);
        }

        return filtered.ToArray();
    }

    private static string ResolveFirstUrl(string urls)
    {
        var firstUrl = urls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?? Defaults.DefaultHttpUrl;

        if (!Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri))
        {
            return Defaults.DefaultHttpUrl;
        }

        if (uri.Host is "*" or "+" or "0.0.0.0" or "::")
        {
            var builder = new UriBuilder(uri)
            {
                Host = "127.0.0.1"
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return uri.ToString().TrimEnd('/');
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                _ = ProcessRunner.Run("open", url);
                return;
            }

            _ = ProcessRunner.Run("xdg-open", url);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Browser could not be opened automatically: {exception.Message}");
            Console.Error.WriteLine($"Open manually: {url}");
        }
    }

    private static void WriteHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} serve

Usage:
  tomur serve [--urls <url>] [--data-dir <path>] [--open]

Options:
  --urls <url>    Bind the local HTTP API service to the specified URL.
  --data-dir      Override the local data directory for this process.
  --open          Open the default browser after the local service starts.
  --no-tray       Disable the Windows tray icon when using the interactive launch path.

Default service URL:
  {Defaults.DefaultHttpUrl}
""");
    }

    private sealed record HostState(
        DataPaths Paths,
        ConfigurationStore ConfigurationStore,
        ServerOptions ServerOptions,
        INativeBundleProbe NativeBundleProbe);
}
