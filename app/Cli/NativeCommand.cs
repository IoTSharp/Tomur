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
            "build" => RunBuild(args),
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

    private static int RunBuild(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!CommandLineHelpers.TryReadOption(args, "--rid", out var rid, out var ridError))
        {
            Console.Error.WriteLine(ridError);
            return 1;
        }

        if (!CommandLineHelpers.TryReadOption(args, "--backend", out var backend, out var backendError))
        {
            Console.Error.WriteLine(backendError);
            return 1;
        }

        NativeBuildPlan plan;
        try
        {
            plan = NativeBuildPlanner.Create(
                rid ?? "win-x64",
                backend ?? "all",
                CommandLineHelpers.HasFlag(args, "--clean"));
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var repoRoot = ResolveRepositoryRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Tomur repository root could not be resolved.");
            return 1;
        }

        Console.WriteLine("Native runtime build");
        Console.WriteLine($"  RID: {plan.Rid}");
        Console.WriteLine($"  Backend: {plan.Backend}");
        Console.WriteLine($"  Root: {repoRoot}");
        Console.WriteLine($"  Output: {Path.Combine(repoRoot, "native", "runtimes", plan.Rid, "native")}");

        foreach (var step in plan.Steps)
        {
            var sourceDirectory = Path.Combine(repoRoot, "native", step.SourceDirectory);
            if (!Directory.Exists(sourceDirectory))
            {
                Console.Error.WriteLine($"Native source directory was not found: {sourceDirectory}");
                return 1;
            }

            if (plan.Clean && !TryCleanBuildDirectory(sourceDirectory, step.Preset, out var cleanError))
            {
                Console.Error.WriteLine(cleanError);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"[{step.Component}] Configure preset: {step.Preset}");
            var configureExitCode = ProcessRunner.RunInDirectory(
                sourceDirectory,
                "cmake",
                "--preset",
                step.Preset);
            if (configureExitCode != 0)
            {
                Console.Error.WriteLine($"Native configure failed for {step.Component}.");
                return configureExitCode;
            }

            Console.WriteLine($"[{step.Component}] Build preset: {step.BuildPreset}");
            var buildExitCode = ProcessRunner.RunInDirectory(
                sourceDirectory,
                "cmake",
                "--build",
                "--preset",
                step.BuildPreset,
                "--target",
                "install");
            if (buildExitCode != 0)
            {
                Console.Error.WriteLine($"Native build failed for {step.Component}.");
                return buildExitCode;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Native runtime build completed.");
        Console.WriteLine("Run `tomur native prepare` to copy the bundle into the managed runtime directory.");
        return 0;
    }

    private static bool TryCleanBuildDirectory(string sourceDirectory, string preset, out string error)
    {
        error = string.Empty;
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var buildRoot = Path.GetFullPath(Path.Combine(sourceRoot, "build"));
        var target = Path.GetFullPath(Path.Combine(buildRoot, preset));
        if (!target.StartsWith(buildRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Refusing to clean unexpected build directory: {target}";
            return false;
        }

        if (!Directory.Exists(target))
        {
            return true;
        }

        try
        {
            Directory.Delete(target, recursive: true);
            Console.WriteLine($"Cleaned build directory: {target}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Build directory could not be cleaned: {exception.Message}";
            return false;
        }
    }

    private static string? ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "app", "Tomur.csproj")) &&
                File.Exists(Path.Combine(current.FullName, "native", "bundle.manifest.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "app", "Tomur.csproj")) &&
                File.Exists(Path.Combine(current.FullName, "native", "bundle.manifest.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
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
  tomur native build [--rid win-x64] [--backend all|cuda13|cpu] [--clean]

Commands:
  prepare    Extract or repair the versioned native runtime bundle.
  build      Build native runtime assets with CMake and install them into native/runtimes.
""");
    }
}
