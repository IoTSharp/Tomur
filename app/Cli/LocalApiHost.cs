using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tomur.Api;
using Tomur.Config;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Serialization;
using Tomur.Services;

namespace Tomur.Cli;

public static class LocalApiHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            CliApplication.WriteServeHelp();
            return 0;
        }

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 1;
        }

        var paths = new DataPaths(pathOptions);
        var configurationStore = new ConfigurationStore(paths);
        var configuration = configurationStore.EnsureConfiguration();
        if (configuration.Status == "error")
        {
            Console.Error.WriteLine(configuration.Message);
            return 1;
        }

        var serviceUrls = ResolveServiceUrls(args, configuration.Configuration, out var urlError);
        if (!string.IsNullOrWhiteSpace(urlError))
        {
            Console.Error.WriteLine(urlError);
            return 1;
        }

        var serverOptions = new ServerOptions
        {
            Urls = serviceUrls
        };
        var nativeBundleProbe = new NativeBundleProbe(paths);
        var startupStatus = new RuntimeDiagnosticsProvider(configurationStore, paths, nativeBundleProbe, serverOptions).GetRuntimeStatus();
        if (startupStatus.Status == "error")
        {
            Console.Error.WriteLine("Local runtime state could not be initialized.");
            foreach (var diagnostic in startupStatus.Diagnostics.Where(static item => item.Severity == "error"))
            {
                Console.Error.WriteLine($"  - {diagnostic.Name}: {diagnostic.Message}");
            }

            return 1;
        }

        var builder = WebApplication.CreateBuilder(FilterHostArgs(args));

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(serverOptions);
        builder.Services.AddSingleton(configurationStore);
        builder.Services.AddSingleton<INativeBundleProbe>(nativeBundleProbe);
        builder.Services.AddSingleton<INativeLibraryResolver, NativeLibraryResolver>();
        builder.Services.AddSingleton<INativeLibraryLoader, NativeLibraryLoader>();
        builder.Services.AddSingleton<RuntimeDiagnosticsProvider>();
        builder.Services.AddSingleton<VersionProvider>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        builder.WebHost.UseUrls(serverOptions.Urls);

        var app = builder.Build();

        app.MapApiRoutes();

        await app.RunAsync();
        return 0;
    }

    internal static string[] FilterHostArgs(string[] args)
    {
        var filtered = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (arg.Equals("--urls", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            filtered.Add(arg);
        }

        return filtered.ToArray();
    }

    private static string ResolveServiceUrls(string[] args, LocalConfiguration configuration, out string error)
    {
        if (!TryReadOption(args, "--urls", out var argumentUrls, out error))
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

    private static bool TryReadOption(
        IReadOnlyList<string> args,
        string name,
        out string? value,
        out string error)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
            {
                var inlineValue = arg[(name.Length + 1)..];
                if (string.IsNullOrWhiteSpace(inlineValue))
                {
                    value = null;
                    error = $"{name} requires a non-empty value.";
                    return false;
                }

                value = inlineValue;
                error = string.Empty;
                return true;
            }

            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count &&
                !IsOption(args[i + 1]))
            {
                value = args[i + 1];
                error = string.Empty;
                return true;
            }

            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = null;
                error = $"{name} requires a non-empty value.";
                return false;
            }
        }

        value = null;
        error = string.Empty;
        return true;
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help";
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
