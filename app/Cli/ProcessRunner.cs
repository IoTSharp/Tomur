using System.ComponentModel;
using System.Diagnostics;

namespace Tomur.Cli;

internal static class ProcessRunner
{
    public static int Run(string fileName, params string[] arguments)
        => RunInDirectory(null, fileName, arguments);

    public static int RunInDirectory(string? workingDirectory, string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? string.Empty
                    : workingDirectory
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.Write(stdout);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.Write(stderr);
            }

            return process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{fileName} could not be executed: {exception.Message}");
            return 1;
        }
    }
}
