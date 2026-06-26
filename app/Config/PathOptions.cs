namespace Tomur.Config;

public sealed class PathOptions
{
    public string DataDirectory { get; init; } = string.Empty;

    public static PathOptions FromArgs(IReadOnlyList<string> args)
    {
        return TryFromArgs(args, out var options, out _)
            ? options
            : new PathOptions();
    }

    public static bool TryFromArgs(IReadOnlyList<string> args, out PathOptions options, out string error)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--data-dir=".Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    options = new PathOptions();
                    error = "--data-dir requires a non-empty path value.";
                    return false;
                }

                options = new PathOptions
                {
                    DataDirectory = value
                };
                error = string.Empty;
                return true;
            }

            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count &&
                !IsOption(args[i + 1]))
            {
                options = new PathOptions
                {
                    DataDirectory = args[i + 1]
                };
                error = string.Empty;
                return true;
            }

            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                options = new PathOptions();
                error = "--data-dir requires a non-empty path value.";
                return false;
            }
        }

        options = new PathOptions();
        error = string.Empty;
        return true;
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal);
    }
}
