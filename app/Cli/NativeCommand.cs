using Tomur.Config;
using Tomur.Native;

namespace Tomur.Cli;

internal static class NativeCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        return args[0] switch
        {
            "prepare" => RunPrepare(args),
            _ => RunUnknownCommand(args[0])
        };
    }

    private static int RunPrepare(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 1;
        }

        var basePaths = new DataPaths(pathOptions);
        var configurationState = new ConfigurationStore(basePaths).EnsureConfiguration();
        if (configurationState.Status == "error")
        {
            Console.Error.WriteLine(configurationState.Message);
            return 1;
        }

        var paths = basePaths.WithConfiguration(configurationState.Configuration);
        var result = new NativeBundlePreparer(paths).Prepare();

        Console.WriteLine("Native runtime bundle prepare");
        Console.WriteLine($"  Status: {result.Status}");
        Console.WriteLine($"  RID: {result.Rid}");
        Console.WriteLine($"  Bundle: {result.BundleId} {result.Version}");
        Console.WriteLine($"  Source: {result.SourceRuntimeRoot}");
        Console.WriteLine($"  Runtime root: {result.RuntimeRoot}");
        Console.WriteLine($"  Message: {result.Message}");

        var changed = result.Files.Count(file => file.Status is "copied" or "repaired" or "aliased");
        var errors = result.Files.Count(static file => file.Status == "error");
        Console.WriteLine($"  Files: {result.Files.Count} total, {changed} changed, {errors} errors");

        foreach (var file in result.Files.Where(static item => item.Status is "copied" or "repaired" or "aliased" or "error"))
        {
            Console.WriteLine($"  [{file.Status}] {file.DestinationPath}");
            Console.WriteLine($"      {file.Message}");
        }

        return result.Status == "error" ? 1 : 0;
    }

    private static int RunUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown native command: {command}");
        Console.Error.WriteLine();
        WriteHelp();
        return 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} native

Usage:
  tomur native prepare [--data-dir <path>]

Commands:
  prepare    Extract or repair the versioned native runtime bundle.
""");
    }
}
