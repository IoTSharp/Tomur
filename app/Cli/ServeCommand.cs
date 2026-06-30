using System.ComponentModel;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Tomur.Api;
using Tomur.Agents;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Serialization;
using Tomur.Services;

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

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(state.Paths);
        builder.Services.AddSingleton(state.ServerOptions);
        builder.Services.AddSingleton(state.ConfigurationStore);
        builder.Services.AddSingleton<INativeBundleProbe>(state.NativeBundleProbe);
        builder.Services.AddSingleton<INativeBundlePreparer, NativeBundlePreparer>();
        builder.Services.AddSingleton<INativeLibraryResolver>(provider =>
            new NativeLibraryResolver(provider.GetRequiredService<INativeBundleProbe>()));
        builder.Services.AddSingleton<INativeLibraryLoader>(provider =>
            new NativeLibraryLoader(provider.GetRequiredService<INativeLibraryResolver>()));
        builder.Services.AddSingleton<LlamaImportResolver>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LocalInferenceService>();
        builder.Services.AddSingleton<LocalChatClient>();
        builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(provider =>
            provider.GetRequiredService<LocalChatClient>());
        builder.Services.AddSingleton<MultimodalRuntimeService>();
        builder.Services.AddSingleton<MultimodalExecutionService>();
        builder.Services.AddSingleton<IsolatedImageGenerationService>();
        builder.Services.AddSingleton<AgentEventLog>();
        builder.Services.AddSingleton<AgentRuntimeService>();
        builder.Services.AddSingleton<ToolFactory>();
        builder.Services.AddSingleton<ToolInvoker>();
        builder.Services.AddSingleton<RuntimeDiagnosticsProvider>();
        builder.Services.AddSingleton<LocalModelCatalog>();
        builder.Services.AddSingleton<VersionProvider>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        builder.WebHost.UseUrls(state.ServerOptions.Urls);
        builder.WebHost.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapApiRoutes();
        app.MapFallbackToFile("index.html");
        return app;
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
        var prepareResult = new NativeBundlePreparer(paths).Prepare();
        if (prepareResult.Status == "error")
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
        if (startupStatus.Status == "error")
        {
            var diagnostics = startupStatus.Diagnostics
                .Where(static item => item.Severity == "error")
                .Select(static item => $"  - {item.Name}: {item.Message}");
            error = "Local runtime state could not be initialized." + Environment.NewLine + string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        state = new HostState(paths, configurationStore, serverOptions, nativeBundleProbe);
        error = string.Empty;
        return true;
    }

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
