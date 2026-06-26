using Tomur.Config;
using Tomur.Runtime;

namespace Tomur.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp();
            return 0;
        }

        if (IsVersion(args[0]))
        {
            Console.WriteLine(Defaults.Version);
            return 0;
        }

        var command = args[0];
        var commandArgs = args[1..];

        return command switch
        {
            "serve" => await LocalApiHost.RunAsync(commandArgs),
            "doctor" => RunDoctor(commandArgs),
            _ => RunUnknownCommand(command)
        };
    }

    private static int RunDoctor(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            WriteDoctorHelp();
            return 0;
        }

        var diagnostics = new RuntimeDiagnosticsProvider().GetDoctorReport();

        Console.WriteLine($"{Defaults.ProductName} doctor");
        Console.WriteLine($"  Version: {diagnostics.Version}");
        Console.WriteLine($"  OS: {diagnostics.OSDescription}");
        Console.WriteLine($"  Architecture: {diagnostics.ProcessArchitecture}");
        Console.WriteLine($"  Framework: {diagnostics.FrameworkDescription}");
        Console.WriteLine($"  Runtime status: {diagnostics.Runtime.Status}");
        Console.WriteLine($"  Runtime code: {diagnostics.Runtime.Code}");
        Console.WriteLine($"  Runtime message: {diagnostics.Runtime.Message}");
        Console.WriteLine("  Next actions:");

        foreach (var action in diagnostics.Runtime.Actions)
        {
            Console.WriteLine($"    - {action}");
        }

        return 0;
    }

    internal static void WriteServeHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} serve

Usage:
  tomur serve [--urls <url>]

Options:
  --urls <url>    Bind the local HTTP API service to the specified URL.

Default service URL:
  {Defaults.DefaultHttpUrl}
""");
    }

    private static void WriteDoctorHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} doctor

Usage:
  tomur doctor

Prints local OS, framework and runtime availability diagnostics.
""");
    }

    private static int RunUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine();
        WriteHelp();
        return 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} {Defaults.Version}

Usage:
  tomur --help
  tomur --version
  tomur serve [--urls <url>]
  tomur doctor

Commands:
  serve      Start the local HTTP API service.
  doctor     Print local runtime diagnostics.

Default service URL:
  {Defaults.DefaultHttpUrl}
""");
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help";
    }

    private static bool IsVersion(string value)
    {
        return value is "-v" or "--version" or "version";
    }
}
