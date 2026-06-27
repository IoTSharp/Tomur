using Tomur.Config;
using Tomur.Storage;

namespace Tomur.Cli;

internal static class ApiKeyCommand
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
            "create" => RunCreate(args),
            "list" => RunList(args),
            _ => RunUnknownCommand(args[0])
        };
    }

    private static int RunCreate(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        var store = CreateApiKeyStore(args, out var error);
        if (store is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (!CommandLineHelpers.TryReadOption(args, "--name", out var name, out var optionError))
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

    private static int RunList(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
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

    private static int RunUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown api-key command: {command}");
        Console.Error.WriteLine();
        WriteHelp();
        return 1;
    }

    private static void WriteHelp()
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
}
