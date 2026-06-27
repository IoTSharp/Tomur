using System.Reflection;
using System.Security;
using Tomur.Config;

namespace Tomur.Cli;

internal static class ServiceCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || CommandLineHelpers.IsHelp(args[0]))
        {
            WriteHelp();
            return 0;
        }

        var command = args[0];
        var commandArgs = args[1..];

        return command switch
        {
            "install" => RunInstall(commandArgs),
            "uninstall" => RunUninstall(commandArgs),
            "start" => RunStart(commandArgs),
            "stop" => RunStop(commandArgs),
            "status" => RunStatus(commandArgs),
            "run" => await ServeCommand.RunAsync(commandArgs, configureServiceLifetime: true, openBrowser: false),
            _ => RunUnknownCommand(command)
        };
    }

    private static int RunInstall(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!TryResolveInstall(args, out var install, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            return InstallWindowsService(install);
        }

        if (OperatingSystem.IsLinux())
        {
            return InstallSystemdService(install);
        }

        if (OperatingSystem.IsMacOS())
        {
            return InstallLaunchAgent(install);
        }

        Console.Error.WriteLine("System service install is supported on Windows, Linux and macOS.");
        return 1;
    }

    private static int RunUninstall(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!TryResolveInstall(args, out var install, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        if (OperatingSystem.IsWindows())
        {
            _ = ProcessRunner.Run("sc.exe", "stop", Defaults.WindowsServiceName);
            return ProcessRunner.Run("sc.exe", "delete", Defaults.WindowsServiceName);
        }

        if (OperatingSystem.IsLinux())
        {
            _ = RunSystemctl(install.UserMode, "stop", Defaults.LinuxServiceName);
            _ = RunSystemctl(install.UserMode, "disable", Defaults.LinuxServiceName);
            try
            {
                if (File.Exists(install.SystemdUnitPath))
                {
                    File.Delete(install.SystemdUnitPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot remove {install.SystemdUnitPath}: {exception.Message}");
                if (!install.UserMode)
                {
                    Console.Error.WriteLine("Run this command with elevated privileges, or use --user for a user-level unit.");
                }

                return 1;
            }

            return RunSystemctl(install.UserMode, "daemon-reload");
        }

        if (OperatingSystem.IsMacOS())
        {
            _ = StopLaunchAgent(reportOutput: false);
            try
            {
                if (File.Exists(install.LaunchAgentPath))
                {
                    File.Delete(install.LaunchAgentPath);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine($"Cannot remove {install.LaunchAgentPath}: {exception.Message}");
                return 1;
            }

            Console.WriteLine($"Removed {install.LaunchAgentPath}");
            return 0;
        }

        Console.Error.WriteLine("System service uninstall is supported on Windows, Linux and macOS.");
        return 1;
    }

    private static int RunStart(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            return ProcessRunner.Run("sc.exe", "start", Defaults.WindowsServiceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return RunSystemctl(CommandLineHelpers.HasFlag(args, "--user"), "start", Defaults.LinuxServiceName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return StartLaunchAgent();
        }

        Console.Error.WriteLine("System service start is supported on Windows, Linux and macOS.");
        return 1;
    }

    private static int RunStop(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            return ProcessRunner.Run("sc.exe", "stop", Defaults.WindowsServiceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return RunSystemctl(CommandLineHelpers.HasFlag(args, "--user"), "stop", Defaults.LinuxServiceName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return StopLaunchAgent(reportOutput: true);
        }

        Console.Error.WriteLine("System service stop is supported on Windows, Linux and macOS.");
        return 1;
    }

    private static int RunStatus(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            return ProcessRunner.Run("sc.exe", "query", Defaults.WindowsServiceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return RunSystemctl(CommandLineHelpers.HasFlag(args, "--user"), "status", Defaults.LinuxServiceName, "--no-pager");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ProcessRunner.Run("launchctl", "print", $"{ResolveLaunchdDomain()}/{Defaults.MacLaunchAgentLabel}");
        }

        Console.Error.WriteLine("System service status is supported on Windows, Linux and macOS.");
        return 1;
    }

    private static int InstallWindowsService(ServiceInstallState install)
    {
        var binPath = BuildServiceCommandLine(install.ProgramArguments);
        var createExitCode = ProcessRunner.Run(
            "sc.exe",
            "create",
            Defaults.WindowsServiceName,
            "binPath=",
            binPath,
            "start=",
            "auto",
            "DisplayName=",
            Defaults.ProductName);
        if (createExitCode != 0)
        {
            Console.Error.WriteLine("Windows Service install failed. Run this command from an elevated terminal.");
            return createExitCode;
        }

        _ = ProcessRunner.Run(
            "sc.exe",
            "description",
            Defaults.WindowsServiceName,
            "Tomur local AI runner and compatibility API service.");

        Console.WriteLine($"Installed Windows Service '{Defaults.WindowsServiceName}'.");
        Console.WriteLine($"Command: {binPath}");
        return 0;
    }

    private static int InstallSystemdService(ServiceInstallState install)
    {
        var unit = BuildSystemdUnit(install);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(install.SystemdUnitPath)!);
            File.WriteAllText(install.SystemdUnitPath, unit);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot write {install.SystemdUnitPath}: {exception.Message}");
            if (!install.UserMode)
            {
                Console.Error.WriteLine("Run this command with elevated privileges, for example through sudo, or use --user for a user-level unit.");
            }

            return 1;
        }

        var reloadExitCode = RunSystemctl(install.UserMode, "daemon-reload");
        if (reloadExitCode != 0)
        {
            return reloadExitCode;
        }

        var enableExitCode = RunSystemctl(install.UserMode, "enable", Defaults.LinuxServiceName);
        if (enableExitCode != 0)
        {
            return enableExitCode;
        }

        Console.WriteLine($"Installed systemd unit '{Defaults.LinuxServiceName}'.");
        Console.WriteLine($"Unit file: {install.SystemdUnitPath}");
        return 0;
    }

    private static int InstallLaunchAgent(ServiceInstallState install)
    {
        var plist = BuildLaunchAgentPlist(install);
        Directory.CreateDirectory(Path.GetDirectoryName(install.LaunchAgentPath)!);
        Directory.CreateDirectory(install.Paths.LogsDirectory);
        File.WriteAllText(install.LaunchAgentPath, plist);

        _ = StopLaunchAgent(reportOutput: false);
        var startExitCode = StartLaunchAgent();
        if (startExitCode != 0)
        {
            return startExitCode;
        }

        Console.WriteLine($"Installed launchd agent '{Defaults.MacLaunchAgentLabel}'.");
        Console.WriteLine($"Plist: {install.LaunchAgentPath}");
        return 0;
    }

    private static bool TryResolveInstall(string[] args, out ServiceInstallState install, out string error)
    {
        install = default!;

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
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);

        var urls = ServeCommand.ResolveServiceUrls(args, configuration.Configuration, out var urlError);
        if (!string.IsNullOrWhiteSpace(urlError))
        {
            error = urlError;
            return false;
        }

        var userMode = CommandLineHelpers.HasFlag(args, "--user");
        var invocation = ResolveExecutableInvocation();
        var programArguments = new List<string> { invocation.ExecutablePath };
        programArguments.AddRange(invocation.PrefixArguments);
        programArguments.Add("service");
        programArguments.Add("run");
        programArguments.Add("--data-dir");
        programArguments.Add(paths.DataDirectory);
        programArguments.Add("--urls");
        programArguments.Add(urls);

        install = new ServiceInstallState(
            paths,
            urls,
            AppContext.BaseDirectory,
            programArguments,
            ResolveSystemdUnitPath(userMode),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents",
                $"{Defaults.MacLaunchAgentLabel}.plist"),
            userMode);
        error = string.Empty;
        return true;
    }

    private static ExecutableInvocation ResolveExecutableInvocation()
    {
        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var processName = string.IsNullOrWhiteSpace(processPath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(processPath);

        if (!string.IsNullOrWhiteSpace(processPath) &&
            processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(assemblyPath) &&
            assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutableInvocation(processPath, [assemblyPath]);
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return new ExecutableInvocation(processPath, []);
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            return assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new ExecutableInvocation("dotnet", [assemblyPath])
                : new ExecutableInvocation(assemblyPath, []);
        }

        throw new InvalidOperationException("Current executable path could not be resolved.");
    }

    private static string BuildSystemdUnit(ServiceInstallState install)
    {
        var commandLine = BuildServiceCommandLine(install.ProgramArguments);
        var bundleExtractDirectory = Path.Combine(install.Paths.DataDirectory, "bundle-cache");
        var wantedBy = install.UserMode ? "default.target" : "multi-user.target";
        var networkDependencies = install.UserMode
            ? string.Empty
            : """
After=network-online.target
Wants=network-online.target

""";

        return $"""
[Unit]
Description=Tomur local AI runner
{networkDependencies}

[Service]
Type=notify
WorkingDirectory={QuoteArgument(install.WorkingDirectory)}
ExecStart={commandLine}
Restart=on-failure
RestartSec=5
Environment={QuoteArgument($"{Defaults.DataDirectoryEnvironmentVariable}={install.Paths.DataDirectory}")}
Environment={QuoteArgument($"DOTNET_BUNDLE_EXTRACT_BASE_DIR={bundleExtractDirectory}")}

[Install]
WantedBy={wantedBy}
""";
    }

    private static string BuildLaunchAgentPlist(ServiceInstallState install)
    {
        var bundleExtractDirectory = Path.Combine(install.Paths.DataDirectory, "bundle-cache");
        var stdoutPath = Path.Combine(install.Paths.LogsDirectory, "launchd.out.log");
        var stderrPath = Path.Combine(install.Paths.LogsDirectory, "launchd.err.log");
        var arguments = string.Join(
            Environment.NewLine,
            install.ProgramArguments.Select(argument => $"        <string>{XmlEscape(argument)}</string>"));

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{XmlEscape(Defaults.MacLaunchAgentLabel)}</string>
    <key>ProgramArguments</key>
    <array>
{arguments}
    </array>
    <key>WorkingDirectory</key>
    <string>{XmlEscape(install.WorkingDirectory)}</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>EnvironmentVariables</key>
    <dict>
        <key>{XmlEscape(Defaults.DataDirectoryEnvironmentVariable)}</key>
        <string>{XmlEscape(install.Paths.DataDirectory)}</string>
        <key>DOTNET_BUNDLE_EXTRACT_BASE_DIR</key>
        <string>{XmlEscape(bundleExtractDirectory)}</string>
    </dict>
    <key>StandardOutPath</key>
    <string>{XmlEscape(stdoutPath)}</string>
    <key>StandardErrorPath</key>
    <string>{XmlEscape(stderrPath)}</string>
</dict>
</plist>
""";
    }

    private static string BuildServiceCommandLine(IReadOnlyList<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static int RunSystemctl(bool userMode, params string[] arguments)
    {
        if (!userMode)
        {
            return ProcessRunner.Run("systemctl", arguments);
        }

        var userArguments = new string[arguments.Length + 1];
        userArguments[0] = "--user";
        Array.Copy(arguments, 0, userArguments, 1, arguments.Length);
        return ProcessRunner.Run("systemctl", userArguments);
    }

    private static string ResolveSystemdUnitPath(bool userMode)
    {
        if (!userMode)
        {
            return Path.Combine("/etc/systemd/system", Defaults.LinuxServiceName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "systemd",
            "user",
            Defaults.LinuxServiceName);
    }

    private static int StartLaunchAgent()
    {
        var plistPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            $"{Defaults.MacLaunchAgentLabel}.plist");
        if (!File.Exists(plistPath))
        {
            Console.Error.WriteLine($"LaunchAgent plist was not found: {plistPath}");
            Console.Error.WriteLine("Run `tomur service install` first.");
            return 1;
        }

        var domain = ResolveLaunchdDomain();
        var bootstrapExitCode = ProcessRunner.Run("launchctl", "bootstrap", domain, plistPath);
        if (bootstrapExitCode != 0)
        {
            Console.Error.WriteLine("launchctl bootstrap failed. If the agent is already loaded, Tomur will try kickstart next.");
        }

        return ProcessRunner.Run("launchctl", "kickstart", "-k", $"{domain}/{Defaults.MacLaunchAgentLabel}");
    }

    private static int StopLaunchAgent(bool reportOutput)
    {
        var domain = ResolveLaunchdDomain();
        var exitCode = ProcessRunner.Run("launchctl", "bootout", $"{domain}/{Defaults.MacLaunchAgentLabel}");
        if (!reportOutput && exitCode != 0)
        {
            return 0;
        }

        return exitCode;
    }

    private static string ResolveLaunchdDomain()
    {
        var uid = Environment.GetEnvironmentVariable("UID");
        if (string.IsNullOrWhiteSpace(uid))
        {
            uid = ResolveCurrentUid();
        }

        return $"gui/{uid}";
    }

    private static string ResolveCurrentUid()
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("id")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-u");
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return string.IsNullOrWhiteSpace(stdout) ? Environment.UserName : stdout;
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(static character => char.IsWhiteSpace(character) || character is '"' or '\'' or ';' or '=');
        return needsQuotes
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string XmlEscape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static int RunUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown service command: {command}");
        Console.Error.WriteLine();
        WriteHelp();
        return 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} service

Usage:
  tomur service install [--urls <url>] [--data-dir <path>] [--user]
  tomur service uninstall [--data-dir <path>] [--user]
  tomur service start [--user]
  tomur service stop [--user]
  tomur service status [--user]

Platform integration:
  Windows: Windows Service named {Defaults.WindowsServiceName}
  Linux:   systemd unit named {Defaults.LinuxServiceName}
  macOS:   launchd user agent named {Defaults.MacLaunchAgentLabel}

Notes:
  Linux install writes /etc/systemd/system/tomur.service and usually requires sudo.
  Linux --user writes ~/.config/systemd/user/tomur.service.
  Windows install usually requires an elevated terminal.
  macOS install creates a user LaunchAgent under ~/Library/LaunchAgents.
""");
    }

    private sealed record ExecutableInvocation(
        string ExecutablePath,
        IReadOnlyList<string> PrefixArguments);

    private sealed record ServiceInstallState(
        DataPaths Paths,
        string Urls,
        string WorkingDirectory,
        IReadOnlyList<string> ProgramArguments,
        string SystemdUnitPath,
        string LaunchAgentPath,
        bool UserMode);
}
