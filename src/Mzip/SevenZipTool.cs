using System.IO;
using System.Reflection;

namespace Mzip;

internal static class SevenZipTool
{
    private const string ResourceName = "Mzip.Tools.7zr.exe";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _path;

    public static async Task<string> GetPathAsync(CancellationToken cancellationToken)
    {
        if (_path is { } cached && File.Exists(cached)) return cached;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_path is { } again && File.Exists(again)) return again;

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mzip", "tools");
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "7zr.exe");

            await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("No se encontró el motor 7-Zip integrado.");

            if (!File.Exists(executable) || new FileInfo(executable).Length != resource.Length)
            {
                var temporary = executable + ".tmp";
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    await resource.CopyToAsync(output, cancellationToken);

                try
                {
                    File.Move(temporary, executable, overwrite: true);
                }
                catch (IOException) when (File.Exists(executable))
                {
                    // Otra instancia acaba de escribirlo y puede estar usándolo.
                    try { File.Delete(temporary); } catch { }
                }
            }

            _path = executable;
            return executable;
        }
        finally
        {
            Gate.Release();
        }
    }
}
