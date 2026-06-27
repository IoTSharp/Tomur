namespace Tomur.Cli;

internal static class CommandLineHelpers
{
    public static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help";
    }

    public static bool IsVersion(string value)
    {
        return value is "-v" or "--version" or "version";
    }

    public static bool HasHelp(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i];
            if (value is "-h" or "--help")
            {
                return true;
            }

            if (i == 0 && value == "help")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }

    public static bool HasFlag(IReadOnlyList<string> args, string name)
    {
        return args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryReadOption(IReadOnlyList<string> args, string name, out string? value, out string error)
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

    public static string FormatNullableBytes(long? bytes)
    {
        return bytes is null ? "unknown" : FormatBytes(bytes.Value);
    }

    public static string FormatBytes(long bytes)
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
}
