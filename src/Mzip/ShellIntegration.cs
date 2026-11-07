using System.IO;
using Microsoft.Win32;

namespace Mzip;

internal static class ShellIntegration
{
    private const string ClassesRoot = @"Software\Classes";

    private static readonly string[] ArchiveExtensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz"
    };

    public static string ExecutablePath { get; } = ResolveExecutable();

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesRoot}\SystemFileAssociations\.zip\shell\Mzip.ExtractHere");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        if (enabled) Register();
        else Unregister();
    }

    private static void Register()
    {
        foreach (var extension in ArchiveExtensions)
        {
            var basePath = $@"SystemFileAssociations\{extension}\shell";
            WriteVerb($@"{basePath}\Mzip.ExtractHere", "Extraer aquí con Mzip", "--extract-here \"%1\"");
            WriteVerb($@"{basePath}\Mzip.ExtractSub", "Extraer en una subcarpeta", "--extract-sub \"%1\"");
            WriteVerb($@"{basePath}\Mzip.Open", "Abrir con Mzip", "\"%1\"");
        }

        WriteVerb(@"*\shell\Mzip.Compress", "Comprimir con Mzip", "--compress \"%1\"");
        WriteVerb(@"Directory\shell\Mzip.Compress", "Comprimir con Mzip", "--compress \"%1\"");
    }

    private static void Unregister()
    {
        foreach (var extension in ArchiveExtensions)
        {
            var basePath = $@"SystemFileAssociations\{extension}\shell";
            DeleteVerb($@"{basePath}\Mzip.ExtractHere");
            DeleteVerb($@"{basePath}\Mzip.ExtractSub");
            DeleteVerb($@"{basePath}\Mzip.Open");
        }

        DeleteVerb(@"*\shell\Mzip.Compress");
        DeleteVerb(@"Directory\shell\Mzip.Compress");
    }

    private static void WriteVerb(string path, string label, string arguments)
    {
        using var verb = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{path}");
        verb.SetValue(null, label);
        verb.SetValue("Icon", $"\"{ExecutablePath}\",0");
        verb.SetValue("MultiSelectModel", "Single");
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{ExecutablePath}\" {arguments}");
    }

    private static void DeleteVerb(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{path}", throwOnMissingSubKey: false);
        }
        catch
        {
            // Un verbo bloqueado por el sistema no debe impedir quitar los demás.
        }

        var parent = $@"{ClassesRoot}\{path[..path.LastIndexOf('\\')]}";
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(parent, writable: true);
            if (shell is not null && shell.SubKeyCount == 0 && shell.ValueCount == 0)
            {
                shell.Close();
                Registry.CurrentUser.DeleteSubKeyTree(parent, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // La clave vacía es inofensiva.
        }
    }

    private static string ResolveExecutable()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) &&
            string.Equals(Path.GetFileName(process), "Mzip.exe", StringComparison.OrdinalIgnoreCase))
            return process;

        return Path.Combine(AppContext.BaseDirectory, "Mzip.exe");
    }
}
