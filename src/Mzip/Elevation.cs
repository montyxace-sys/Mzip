using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace Mzip;

internal sealed class JobRequest
{
    public string Type { get; set; } = "compress";
    public string? ArchivePath { get; set; }
    public string? Destination { get; set; }
    public string Format { get; set; } = "zip";
    public string? Password { get; set; }
    public bool Overwrite { get; set; }
    public List<string> Files { get; set; } = new();
}

internal sealed class JobResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
    public int Skipped { get; set; }
}

internal static class Elevation
{
    private const string JobSwitch = "--job";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string? GetJobPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(JobSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(JobSwitch + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(JobSwitch.Length + 1)..];
        }
        return null;
    }

    public static async Task<JobResult> RunElevatedAsync(
        JobRequest request, CancellationToken cancellationToken, Action? started = null)
    {
        var jobDirectory = Path.Combine(Path.GetTempPath(), "Mzip", "jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);

        var jobPath = Path.Combine(jobDirectory, "job.json");
        var resultPath = Path.Combine(jobDirectory, "result.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);

        try
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("No se pudo localizar el ejecutable de Mzip.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{JobSwitch} \"{jobPath}\""
            };

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("No se pudo iniciar la operación elevada.");
            started?.Invoke();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // El proceso pudo terminar entre la comprobación y la finalización.
                }
                throw;
            }

            if (!File.Exists(resultPath))
                throw new InvalidOperationException(
                    $"La operación elevada terminó sin devolver resultado (código {process.ExitCode}).");

            var json = await File.ReadAllTextAsync(resultPath);
            return JsonSerializer.Deserialize<JobResult>(json, JsonOptions)
                   ?? throw new InvalidOperationException("El resultado de la operación elevada no es válido.");
        }
        finally
        {
            try { Directory.Delete(jobDirectory, recursive: true); } catch { }
        }
    }

    public static async Task<int> ExecuteJobAsync(string jobPath)
    {
        var result = new JobResult();
        try
        {
            var json = await File.ReadAllTextAsync(jobPath);
            var job = JsonSerializer.Deserialize<JobRequest>(json, JsonOptions)
                      ?? throw new InvalidOperationException("El trabajo recibido no es válido.");

            switch (job.Type)
            {
                case "compress":
                {
                    var output = job.ArchivePath
                                 ?? throw new InvalidOperationException("Falta la ruta del archivo de salida.");
                    var items = new List<FileItem>();
                    foreach (var source in job.Files)
                    {
                        if (!File.Exists(source) && !Directory.Exists(source))
                            throw new FileNotFoundException($"No existe «{source}».", source);
                        items.Add(await FileItem.CreateAsync(source));
                    }

                    var format = job.Format.Equals("tar", StringComparison.OrdinalIgnoreCase)
                        ? ArchiveFormat.Tar
                        : ArchiveFormat.Zip;
                    result.Count = await ArchiveService.CompressAsync(
                        items, output, format, null, CancellationToken.None);
                    break;
                }
                case "extract":
                {
                    var archive = job.ArchivePath
                                  ?? throw new InvalidOperationException("Falta la ruta del archivo comprimido.");
                    var destination = job.Destination
                                      ?? throw new InvalidOperationException("Falta la carpeta de destino.");

                    var entries = ArchiveService.ReadEntries(archive, job.Password);
                    var total = entries.Sum(entry => entry.Size);
                    var extract = await ArchiveService.ExtractAsync(
                        archive, destination, job.Password, job.Overwrite, total, null, CancellationToken.None);
                    result.Count = extract.Extracted;
                    result.Skipped = extract.Skipped;
                    break;
                }
                default:
                    throw new InvalidOperationException($"Tipo de trabajo desconocido: {job.Type}.");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        try
        {
            var resultPath = Path.Combine(Path.GetDirectoryName(jobPath)!, "result.json");
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch
        {
            // Si no se puede escribir el resultado, el proceso padre lo detectará por su ausencia.
        }

        return result.Success ? 0 : 1;
    }
}
