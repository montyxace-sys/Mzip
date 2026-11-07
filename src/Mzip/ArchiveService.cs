using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Readers;

namespace Mzip;

public enum ArchiveFormat
{
    Zip,
    Tar
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
        return Task.Run(() =>
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

                if (format == ArchiveFormat.Zip)
                {
                    using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
                    foreach (var dir in emptyDirectories)
                        zip.CreateEntry(dir + "/", CompressionLevel.NoCompression);

                    foreach (var job in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(job.EntryPath, CompressionLevel.Optimal);
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
                    using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                    using var writer = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true);
                    foreach (var dir in emptyDirectories)
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dir + "/")
                        {
                            ModificationTime = DateTimeOffset.Now
                        });

                    foreach (var job in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var raw = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                        using var tracked = new ProgressStream(raw, bytes =>
                        {
                            done += bytes;
                            Report(job.EntryPath);
                        });
                        var entry = new PaxTarEntry(TarEntryType.RegularFile, job.EntryPath)
                        {
                            DataStream = tracked,
                            ModificationTime = new DateTimeOffset(job.LastWrite)
                        };
                        writer.WriteEntry(entry);
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

            Report(Path.GetFileName(archivePath), true);

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

                using (var input = reader.OpenEntryStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
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

                if (entry.LastModifiedTime.HasValue)
                {
                    try { File.SetLastWriteTime(target, entry.LastModifiedTime.Value); }
                    catch { }
                }

                extracted++;
            }

            progress?.Report(new ProgressInfo(100, $"Extraídos {extracted} archivo(s)."));
            return new ExtractResult(extracted, skipped);
        }, cancellationToken);
    }

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
