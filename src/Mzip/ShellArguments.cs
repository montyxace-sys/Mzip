namespace Mzip;

internal sealed record ShellRequest(string Mode, string Path);

internal static class ShellArguments
{
    public static bool? RegistrationCommand(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument.Equals("--register-shell", StringComparison.OrdinalIgnoreCase)) return true;
            if (argument.Equals("--unregister-shell", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    public static ShellRequest? Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            var mode = argument.ToLowerInvariant() switch
            {
                "--extract-here" => "extract-here",
                "--extract-sub" => "extract-sub",
                "--compress" => "compress",
                _ => null
            };

            if (mode is null) continue;
            if (i + 1 >= args.Length) return null;
            return new ShellRequest(mode, args[i + 1]);
        }

        var path = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        return path is null ? null : new ShellRequest("open", path);
    }
}
