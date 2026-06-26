using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tomur.Api;
using Tomur.Config;
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

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton<RuntimeDiagnosticsProvider>();
        builder.Services.AddSingleton<VersionProvider>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        if (!HasExplicitUrl(args))
        {
            builder.WebHost.UseUrls(Defaults.DefaultHttpUrl);
        }

        var app = builder.Build();

        app.MapApiRoutes();

        await app.RunAsync();
        return 0;
    }

    private static bool HasExplicitUrl(string[] args)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            return true;
        }

        foreach (var arg in args)
        {
            if (arg.Equals("--urls", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help";
    }
}
