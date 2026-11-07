using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Readers;
using SharpCompress.Writers.Tar;

namespace Mzip;

public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    TarGz,
    TarBz2
}

public static class ArchiveFormatInfo
{
    public static string Extension(this ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.TarGz => ".tar.gz",
        ArchiveFormat.TarBz2 => ".tar.bz2",
        ArchiveFormat.Tar => ".tar",
        _ => ".zip"
    };

    public static string Key(this ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.TarGz => "tar.gz",
        ArchiveFormat.TarBz2 => "tar.bz2",
        ArchiveFormat.Tar => "tar",
        _ => "zip"
    };

    public static ArchiveFormat Parse(string? key) => key?.ToLowerInvariant() switch
    {
        "7z" => ArchiveFormat.SevenZip,
        "tar.gz" or "tgz" => ArchiveFormat.TarGz,
        "tar.bz2" or "tbz2" => ArchiveFormat.TarBz2,
        "tar" => ArchiveFormat.Tar,
        _ => ArchiveFormat.Zip
    };
}

public readonly record struct ProgressInfo(double Percent, string Message);

public readonly record struct ExtractResult(int Extracted, int Skipped);

public static class ArchiveService
{
    private const int BufferSize = 81920;
    private const int ReportIntervalMs = 80;

    public const string FileFilter =
        "Archivos comprimidos|*.zip;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz2;*.tar.xz;*.txz;*.7z;*.rar;*.gz;*.bz2;*.xz|Todos los archivos|*.*";

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".cab", ".lz", ".zst"
    };

    private static readonly Regex PercentPattern = new(@"(?<!\d)(\d{1,3})%", RegexOptions.Compiled);

    public static bool IsArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return KnownExtensions.Contains(Path.GetExtension(path));
    }

    public static IReadOnlyList<ArchiveEntryItem> ReadEntries(string archivePath, string? password = null)
    {
        var options = new ReaderOptions { LookForHeader = true };
        if (!string.IsNullOrEmpty(password)) options.Password = password;

        var entries = new List<ArchiveEntryItem>();
        if (IsSevenZip(archivePath))
        {
            // El lector secuencial no soporta 7z; el archivo sí (incluidos los sólidos).
            using var archive = ArchiveFactory.OpenArchive(archivePath, options);
            foreach (var entry in archive.Entries)
            {
                var key = NormalizeKey(entry.Key);
                if (key.Length == 0) continue;
                entries.Add(new ArchiveEntryItem(key, entry.Size, entry.IsDirectory || key.EndsWith('/')));
            }
            return entries;
        }

        using var reader = ReaderFactory.OpenReader(archivePath, options);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry is null) continue;
            var key = NormalizeKey(entry.Key);
            if (key.Length == 0) continue;
            entries.Add(new ArchiveEntryItem(key, entry.Size, entry.IsDirectory || key.EndsWith('/')));
        }
        return entries;
    }

    public static Task<int> CompressAsync(
        IReadOnlyList<FileItem> items,
        string outputPath,
        ArchiveFormat format,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("La carpeta de destino no es válida.");
            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var jobs = CollectFiles(items, outputPath);
                var emptyDirectories = CollectEmptyDirectories(items).Distinct(StringComparer.Ordinal).ToList();
                var totalBytes = jobs.Sum(j => j.Length);

                long done = 0;
                var stopwatch = Stopwatch.StartNew();
                long lastReport = -ReportIntervalMs;

                void Report(string current, bool force = false)
                {
                    if (!force && stopwatch.ElapsedMilliseconds - lastReport < ReportIntervalMs) return;
                    lastReport = stopwatch.ElapsedMilliseconds;
                    var percent = totalBytes > 0 ? Math.Clamp(done * 100.0 / totalBytes, 0, 100) : 0;
                    progress?.Report(new ProgressInfo(percent,
                        $"Comprimiendo {current} · {FileItem.FormatSize(done)} de {FileItem.FormatSize(totalBytes)}"));
                }

                Report(Path.GetFileName(outputPath), true);

                if (format == ArchiveFormat.SevenZip)
                {
                    await CompressSevenZipAsync(items, tempPath, progress, cancellationToken);
                }
                else if (format == ArchiveFormat.Zip)
                {
                    using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
                    foreach (var dir in emptyDirectories)
                        zip.CreateEntry(dir + "/", CompressionLevel.NoCompression);

                    foreach (var job in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(job.EntryPath, CompressionLevel.SmallestSize);
                        entry.LastWriteTime = ClampZipTime(job.LastWrite);

                        using var input = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                        using var output = entry.Open();
                        var buffer = new byte[BufferSize];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            done += read;
                            Report(job.EntryPath);
                        }
                    }
                }
                else
                {
                    var compression = format switch
                    {
                        ArchiveFormat.TarGz => CompressionType.GZip,
                        ArchiveFormat.TarBz2 => CompressionType.BZip2,
                        _ => CompressionType.None
                    };

                    using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    using var writer = new TarWriter(file, new TarWriterOptions(compression, finalizeArchiveOnClose: true)
                    {
                        HeaderFormat = TarHeaderWriteFormat.GNU_TAR_LONG_LINK,
                        CompressionLevel = 9
                    });

                    foreach (var dir in emptyDirectories)
                        writer.WriteDirectory(dir + "/", DateTime.Now);

                    foreach (var job in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var raw = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                        using var tracked = new ProgressStream(raw, bytes =>
                        {
                            done += bytes;
                            Report(job.EntryPath);
                        });
                        writer.Write(job.EntryPath, tracked, job.LastWrite, job.Length);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, outputPath, overwrite: true);
                progress?.Report(new ProgressInfo(100, $"Comprimido: {Path.GetFileName(outputPath)}"));
                return jobs.Count;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // Un temporal huérfano no debe ocultar el error real.
                }
            }
        }, cancellationToken);
    }

    private static async Task CompressSevenZipAsync(
        IReadOnlyList<FileItem> items,
        string archivePath,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var tool = await SevenZipTool.GetPathAsync(cancellationToken);
        var totalBytes = items.Sum(item => item.Size);
        long done = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var percent = totalBytes > 0 ? Math.Clamp(done * 100.0 / totalBytes, 0, 100) : 0;
            progress?.Report(new ProgressInfo(percent,
                $"Comprimiendo {item.Name} · {FileItem.FormatSize(done)} de {FileItem.FormatSize(totalBytes)}"));

            await RunSevenZipAddAsync(tool, archivePath, item, done, totalBytes, progress, cancellationToken);
            done += item.Size;
        }
    }

    private static async Task RunSevenZipAddAsync(
        string tool,
        string archivePath,
        FileItem item,
        long completed,
        long totalBytes,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(item.SourcePath);
        if (string.IsNullOrEmpty(workingDirectory))
            throw new InvalidOperationException($"No se puede comprimir «{item.SourcePath}».");

        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // El nombre relativo evita guardar rutas absolutas dentro del archivo 7z.
        foreach (var argument in new[] { "a", "-t7z", "-mx=9", "-y", "-bsp1", archivePath, item.Name })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar el compresor 7-Zip.");

        var errors = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        long lastReport = -ReportIntervalMs;

        var stdoutPump = PumpAsync(process.StandardOutput, line =>
        {
            var match = PercentPattern.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var itemPercent)) return;
            if (stopwatch.ElapsedMilliseconds - lastReport < ReportIntervalMs) return;
            lastReport = stopwatch.ElapsedMilliseconds;

            itemPercent = Math.Clamp(itemPercent, 0, 100);
            var percent = totalBytes > 0
                ? Math.Clamp((completed + item.Size * itemPercent / 100.0) * 100.0 / totalBytes, 0, 100)
                : 0;
            progress?.Report(new ProgressInfo(percent,
                $"Comprimiendo {item.Name} · {itemPercent}% · {FileItem.FormatSize(completed)} de {FileItem.FormatSize(totalBytes)}"));
        });
        var stderrPump = PumpAsync(process.StandardError, line => errors.AppendLine(line));

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
        finally
        {
            try { await Task.WhenAll(stdoutPump, stderrPump); } catch { }
        }

        // 0 = correcto; 1 = aviso (algún archivo ilegible) pero archivo creado.
        if (process.ExitCode > 1)
        {
            var detail = errors.ToString().Trim();
            throw new IOException(detail.Length > 0
                ? detail
                : $"7-Zip terminó con el código {process.ExitCode}.");
        }
    }

    // 7-Zip separa el progreso con \r, así que no sirve BeginOutputReadLine.
    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var character = buffer[i];
                if (character is '\r' or '\n')
                {
                    if (line.Length == 0) continue;
                    onLine(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(character);
                }
            }
        }

        if (line.Length > 0) onLine(line.ToString());
    }

    public static Task<ExtractResult> ExtractAsync(
        string archivePath,
        string destination,
        string? password,
        bool overwrite,
        long totalBytes,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException("La carpeta de destino no es válida.");
            Directory.CreateDirectory(destination);

            var root = Path.GetFullPath(destination);
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var options = new ReaderOptions { LookForHeader = true };
            if (!string.IsNullOrEmpty(password)) options.Password = password;

            var extracted = 0;
            var skipped = 0;
            long done = 0;
            var stopwatch = Stopwatch.StartNew();
            long lastReport = -ReportIntervalMs;

            void Report(string current, bool force = false)
            {
                if (!force && stopwatch.ElapsedMilliseconds - lastReport < ReportIntervalMs) return;
                lastReport = stopwatch.ElapsedMilliseconds;
                var percent = totalBytes > 0 ? Math.Clamp(done * 100.0 / totalBytes, 0, 100) : 0;
                var message = totalBytes > 0
                    ? $"Extrayendo {current} · {FileItem.FormatSize(done)} de {FileItem.FormatSize(totalBytes)}"
                    : $"Extrayendo {current} · {FileItem.FormatSize(done)}";
                progress?.Report(new ProgressInfo(percent, message));
            }

            void CopyEntry(Stream input, string target, string key)
            {
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    Report(key);
                }
            }

            Report(Path.GetFileName(archivePath), true);

            if (IsSevenZip(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath, options);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = NormalizeKey(entry.Key);
                    if (key.Length == 0) continue;

                    var target = ResolveTarget(root, rootPrefix, key);
                    var isDirectory = entry.IsDirectory || key.EndsWith('/');
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(entry.LinkTarget) || (!overwrite && File.Exists(target)))
                    {
                        skipped++;
                        continue;
                    }

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    using (var input = entry.OpenEntryStream()) CopyEntry(input, target, key);

                    if (entry.LastModifiedTime.HasValue)
                    {
                        try { File.SetLastWriteTime(target, entry.LastModifiedTime.Value); }
                        catch { }
                    }

                    extracted++;
                }
            }
            else
            {
                using var reader = ReaderFactory.OpenReader(archivePath, options);
                while (reader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = reader.Entry;
                    if (entry is null) continue;

                    var key = NormalizeKey(entry.Key);
                    if (key.Length == 0) continue;

                    var target = ResolveTarget(root, rootPrefix, key);
                    var isDirectory = entry.IsDirectory || key.EndsWith('/');
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    // Los enlaces simbólicos de un archivo no deben poder escribir fuera del destino.
                    if (!string.IsNullOrEmpty(entry.LinkTarget))
                    {
                        using (var drain = reader.OpenEntryStream()) drain.CopyTo(Stream.Null);
                        skipped++;
                        continue;
                    }

                    if (!overwrite && File.Exists(target))
                    {
                        using (var drain = reader.OpenEntryStream()) drain.CopyTo(Stream.Null);
                        skipped++;
                        continue;
                    }

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    using (var input = reader.OpenEntryStream()) CopyEntry(input, target, key);

                    if (entry.LastModifiedTime.HasValue)
                    {
                        try { File.SetLastWriteTime(target, entry.LastModifiedTime.Value); }
                        catch { }
                    }

                    extracted++;
                }
            }

            progress?.Report(new ProgressInfo(100, $"Extraídos {extracted} archivo(s)."));
            return new ExtractResult(extracted, skipped);
        }, cancellationToken);
    }

    private static bool IsSevenZip(string path) =>
        string.Equals(Path.GetExtension(path), ".7z", StringComparison.OrdinalIgnoreCase);

    private readonly record struct FileJob(string SourcePath, string EntryPath, long Length, DateTime LastWrite);

    private static List<FileJob> CollectFiles(IEnumerable<FileItem> items, string outputPath)
    {
        var jobs = new List<FileJob>();
        foreach (var item in items)
        {
            if (File.Exists(item.SourcePath))
            {
                if (!SamePath(item.SourcePath, outputPath))
                    jobs.Add(MakeJob(item.SourcePath, item.EntryPath));
            }
            else if (Directory.Exists(item.SourcePath))
            {
                foreach (var file in EnumerateFilesSafely(item.SourcePath))
                {
                    if (SamePath(file, outputPath)) continue;
                    var relative = Path.GetRelativePath(item.SourcePath, file).Replace('\\', '/');
                    jobs.Add(MakeJob(file, $"{item.EntryPath}/{relative}"));
                }
            }
        }
        return jobs;
    }

    private static IEnumerable<string> CollectEmptyDirectories(IEnumerable<FileItem> items)
    {
        foreach (var item in items)
        {
            if (!Directory.Exists(item.SourcePath)) continue;

            var pending = new Stack<string>();
            pending.Push(item.SourcePath);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                string[] files;
                string[] directories;
                try
                {
                    files = Directory.GetFiles(directory);
                    directories = Directory.GetDirectories(directory);
                }
                catch
                {
                    continue;
                }

                var realDirectories = new List<string>();
                foreach (var child in directories)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    realDirectories.Add(child);
                    pending.Push(child);
                }

                if (files.Length == 0 && realDirectories.Count == 0)
                {
                    var relative = Path.GetRelativePath(item.SourcePath, directory);
                    var entryName = relative == "." ? item.EntryPath : $"{item.EntryPath}/{relative.Replace('\\', '/')}";
                    yield return entryName;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files) yield return file;

            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static FileJob MakeJob(string path, string entryPath)
    {
        long length = 0;
        var lastWrite = DateTime.Now;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            lastWrite = info.LastWriteTime;
        }
        catch
        {
            // El archivo puede haber cambiado entre la enumeración y la lectura.
        }
        return new FileJob(path, entryPath.Replace('\\', '/'), length, lastWrite);
    }

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var normalized = key.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    private static string ResolveTarget(string root, string rootPrefix, string key)
    {
        if (key.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidDataException($"Entrada insegura rechazada: {key}");

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, key));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Entrada no válida: {key}");
        }

        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Entrada fuera de la carpeta de destino: {key}");

        return full;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DateTimeOffset ClampZipTime(DateTime value)
    {
        if (value.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (value.Year > 2107) return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return new DateTimeOffset(value);
    }
}
