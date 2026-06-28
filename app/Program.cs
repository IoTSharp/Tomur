using Tomur.Cli;
using Tomur.Config;

try
{
    return await RunAsync(args);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Tomur failed: {exception.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        return await ServeCommand.RunInteractiveLaunchAsync();
    }

    if (CommandLineHelpers.IsHelp(args[0]))
    {
        WriteHelp();
        return 0;
    }

    if (CommandLineHelpers.IsVersion(args[0]))
    {
        Console.WriteLine(Defaults.Version);
        return 0;
    }

    var command = args[0];
    var commandArgs = args[1..];

    return command switch
    {
        "serve" => await ServeCommand.RunAsync(
            commandArgs,
            configureServiceLifetime: false,
            openBrowser: CommandLineHelpers.HasFlag(commandArgs, "--open")),
        "open" => await ServeCommand.RunInteractiveLaunchAsync(commandArgs),
        "service" => await ServiceCommand.RunAsync(commandArgs),
        "doctor" => DoctorCommand.Run(commandArgs),
        "api-key" => ApiKeyCommand.Run(commandArgs),
        "native" => NativeCommand.Run(commandArgs),
        "pull" => await ModelCommand.RunPullAsync(commandArgs),
        "list" => ModelCommand.RunList(commandArgs),
        "ps" => ModelCommand.RunPs(commandArgs),
        "internal" => InternalCommand.Run(commandArgs),
        _ => RunUnknownCommand(command)
    };
}

static int RunUnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    Console.Error.WriteLine();
    WriteHelp();
    return 1;
}

static void WriteHelp()
{
    Console.WriteLine($"""
{Defaults.ProductName} {Defaults.Version}

Usage:
  tomur
  tomur --help
  tomur --version
  tomur open [--urls <url>] [--data-dir <path>] [--no-tray]
  tomur serve [--urls <url>] [--data-dir <path>] [--open]
  tomur service install [--urls <url>] [--data-dir <path>] [--user]
  tomur service uninstall [--data-dir <path>] [--user]
  tomur service start [--user]
  tomur service stop [--user]
  tomur service status [--user]
  tomur doctor [--data-dir <path>]
  tomur native prepare [--data-dir <path>]
  tomur pull [recommended|optional|all|<package-id>...] [--data-dir <path>] [--proxy <url>] [--force]
  tomur list [--catalog|--recommended] [--data-dir <path>]
  tomur ps [--data-dir <path>]
  tomur api-key create [--name <name>] [--data-dir <path>]
  tomur api-key list [--data-dir <path>]

Commands:
  open       Start the local service and open the workspace. This is also the no-argument double-click path. On Windows it also creates a tray icon unless --no-tray is set.
  serve      Start the local HTTP API service.
  service    Install, run and manage the OS service integration.
  doctor     Print local runtime diagnostics.
  native     Manage the local native runtime bundle.
  pull       Download local model packages with resume and checksum validation.
  list       List installed models or the built-in model catalog.
  ps         Show visible model assets and local loading state.
  api-key    Manage local compatibility API keys.

Default service URL:
  {Defaults.DefaultHttpUrl}

Data directory:
  Windows: %LOCALAPPDATA%\Tomur
  Linux:   ~/.local/share/tomur
  macOS:   ~/Library/Application Support/Tomur
  Override: --data-dir <path> or {Defaults.DataDirectoryEnvironmentVariable}
""");
}
