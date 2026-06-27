using Tomur.Config;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Storage;

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
            "api-key" => RunApiKey(commandArgs),
            _ => RunUnknownCommand(command)
        };
    }

    private static int RunApiKey(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteApiKeyHelp();
            return 0;
        }

        return args[0] switch
        {
            "create" => RunApiKeyCreate(args),
            "list" => RunApiKeyList(args),
            _ => RunUnknownApiKeyCommand(args[0])
        };
    }

    private static int RunApiKeyCreate(string[] args)
    {
        if (args.Length > 1 && IsHelp(args[1]))
        {
            WriteApiKeyHelp();
            return 0;
        }

        var store = CreateApiKeyStore(args, out var error);
        if (store is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (!TryReadOption(args, "--name", out var name, out var optionError))
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        name ??= "local";
        var issued = store.CreateKey(name);

        Console.WriteLine("Created local API key.");
        Console.WriteLine($"  Id: {issued.Record.Id}");
        Console.WriteLine($"  Name: {issued.Record.Name}");
        Console.WriteLine($"  Prefix: {issued.Record.Prefix}");
        Console.WriteLine($"  API key: {issued.PlainTextKey}");
        Console.WriteLine();
        Console.WriteLine("Store this value now. Tomur only stores its hash and cannot show it again.");

        return 0;
    }

    private static int RunApiKeyList(string[] args)
    {
        if (args.Length > 1 && IsHelp(args[1]))
        {
            WriteApiKeyHelp();
            return 0;
        }

        var store = CreateApiKeyStore(args, out var error);
        if (store is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var state = store.GetState();
        Console.WriteLine($"Local API keys: {state.ActiveKeyCount} active");
        Console.WriteLine($"  Status: {state.Status}");
        Console.WriteLine($"  Message: {state.Message}");

        foreach (var key in state.Keys)
        {
            Console.WriteLine($"  - {key.Name} ({key.Id}) prefix={key.Prefix} created={key.CreatedAt:O}");
        }

        return state.Status == "error" ? 1 : 0;
    }

    private static int RunDoctor(string[] args)
    {
        if (args.Length > 0 && IsHelp(args[0]))
        {
            WriteDoctorHelp();
            return 0;
        }

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 1;
        }

        var paths = new DataPaths(pathOptions);
        var diagnostics = new RuntimeDiagnosticsProvider(
            new ConfigurationStore(paths),
            paths,
            new NativeBundleProbe(paths)).GetDoctorReport();

        Console.WriteLine($"{Defaults.ProductName} doctor");
        Console.WriteLine($"  Version: {diagnostics.Version}");
        Console.WriteLine($"  Status: {diagnostics.Status}");
        Console.WriteLine($"  OS: {diagnostics.OSDescription}");
        Console.WriteLine($"  Architecture: {diagnostics.ProcessArchitecture}");
        Console.WriteLine($"  Framework: {diagnostics.FrameworkDescription}");
        Console.WriteLine($"  CPU: {diagnostics.Details.System.ProcessorCount} logical processors");
        if (!string.IsNullOrWhiteSpace(diagnostics.Details.System.CpuName))
        {
            Console.WriteLine($"  CPU name: {diagnostics.Details.System.CpuName}");
        }

        if (diagnostics.Details.System.TotalMemoryBytes is not null)
        {
            Console.WriteLine($"  Memory: {FormatBytes((long)diagnostics.Details.System.TotalMemoryBytes.Value)}");
        }

        Console.WriteLine($"  Data directory: {diagnostics.Details.Paths.DataDirectory}");
        Console.WriteLine($"  Config file: {diagnostics.Details.Configuration.Path}");
        Console.WriteLine($"  Database: {diagnostics.Details.Database.Path}");
        Console.WriteLine($"  Runtime directory: {diagnostics.Details.Paths.RuntimeDirectory}");
        Console.WriteLine($"  Models directory: {diagnostics.Details.Paths.ModelsDirectory}");
        Console.WriteLine($"  Logs directory: {diagnostics.Details.Paths.LogsDirectory}");
        Console.WriteLine($"  Disk free: {FormatNullableBytes(diagnostics.Details.Disk.AvailableBytes)}");
        Console.WriteLine($"  Proxy: {diagnostics.Details.Proxy.Status}");
        Console.WriteLine($"  Port: {diagnostics.Details.Port.Status} ({diagnostics.Details.Port.Url})");
        Console.WriteLine($"  API keys: {diagnostics.Details.ApiKeys.Status} ({diagnostics.Details.ApiKeys.ActiveKeyCount} active)");
        Console.WriteLine($"  Native bundle: {diagnostics.NativeBundle.Status} ({diagnostics.NativeBundle.Rid})");
        Console.WriteLine($"  Runtime: {diagnostics.Runtime.Status} / {diagnostics.Runtime.Code}");
        Console.WriteLine();
        Console.WriteLine("Diagnostics:");

        foreach (var item in diagnostics.Diagnostics)
        {
            Console.WriteLine($"  [{item.Severity}] {item.Name}: {item.Message}");
            if (!string.IsNullOrWhiteSpace(item.Value))
            {
                Console.WriteLine($"      Value: {item.Value}");
            }

            foreach (var action in item.Actions)
            {
                Console.WriteLine($"      Action: {action}");
            }
        }

        return 0;
    }

    internal static void WriteServeHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} serve

Usage:
  tomur serve [--urls <url>] [--data-dir <path>]

Options:
  --urls <url>    Bind the local HTTP API service to the specified URL.
  --data-dir      Override the local data directory for this process.

Default service URL:
  {Defaults.DefaultHttpUrl}
""");
    }

    private static void WriteApiKeyHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} api-key

Usage:
  tomur api-key create [--name <name>] [--data-dir <path>]
  tomur api-key list [--data-dir <path>]

Commands:
  create     Create a local compatibility API key and print the secret once.
  list       List stored local API key summaries.
""");
    }

    private static void WriteDoctorHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} doctor

Usage:
  tomur doctor [--data-dir <path>]

Options:
  --data-dir      Override the local data directory for this process.

Prints local OS, data directory, SQLite, API key, port and runtime diagnostics.
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
  tomur serve [--urls <url>] [--data-dir <path>]
  tomur doctor [--data-dir <path>]
  tomur api-key create [--name <name>] [--data-dir <path>]
  tomur api-key list [--data-dir <path>]

Commands:
  serve      Start the local HTTP API service.
  doctor     Print local runtime diagnostics.
  api-key    Manage local compatibility API keys.

Default service URL:
  {Defaults.DefaultHttpUrl}

Data directory:
  Windows: %LOCALAPPDATA%\Tomur
  Linux:   ~/.local/share/tomur
  Override: --data-dir <path> or {Defaults.DataDirectoryEnvironmentVariable}
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

    private static int RunUnknownApiKeyCommand(string command)
    {
        Console.Error.WriteLine($"Unknown api-key command: {command}");
        Console.Error.WriteLine();
        WriteApiKeyHelp();
        return 1;
    }

    private static ApiKeyStore? CreateApiKeyStore(string[] args, out string error)
    {
        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            error = pathError;
            return null;
        }

        var basePaths = new DataPaths(pathOptions);
        var configurationState = new ConfigurationStore(basePaths).EnsureConfiguration();
        if (configurationState.Status == "error")
        {
            error = configurationState.Message;
            return null;
        }

        var paths = basePaths.WithConfiguration(configurationState.Configuration);
        var database = new LocalDatabaseInitializer(paths);
        var databaseState = database.EnsureDatabase();
        if (databaseState.Status == "error")
        {
            error = databaseState.Message;
            return null;
        }

        error = string.Empty;
        return new ApiKeyStore(database);
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

    private static string FormatNullableBytes(long? bytes)
    {
        return bytes is null ? "unknown" : FormatBytes(bytes.Value);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
