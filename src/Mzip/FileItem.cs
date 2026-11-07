using System.IO;

namespace Mzip;

public sealed class FileItem
{
    private FileItem(string sourcePath, string name, bool isFolder, long size)
    {
        SourcePath = sourcePath;
        Name = name;
        IsFolder = isFolder;
        Size = size;
    }

    public string SourcePath { get; }
    public string Name { get; }
    public bool IsFolder { get; }
    public long Size { get; }

    public string EntryPath => Name;
    public string Badge => IsFolder ? "DIR" : ExtensionBadge(Name);
    public string TypeText => IsFolder ? "Carpeta" : "Archivo";
    public string SizeText => FormatSize(Size);
    public string Location => SourcePath;

    public static async Task<FileItem> CreateAsync(string path)
    {
        var clean = Path.TrimEndingDirectorySeparator(path);
        var isFolder = Directory.Exists(clean);
        var name = Path.GetFileName(clean);
        if (string.IsNullOrEmpty(name)) name = clean;
        var size = await Task.Run(() => isFolder ? GetFolderSize(clean) : GetFileSize(clean));
        return new FileItem(clean, name, isFolder, size);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{bytes} B" : $"{value:0.#} {units[index]}";
    }

    public static string ExtensionBadge(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return "FILE";
        ext = ext.ToUpperInvariant();
        return ext.Length > 4 ? ext[..4] : ext;
    }

    private static long GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static long GetFolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch
        {
            // Carpetas sin permiso: se usa el tamaño parcial.
        }
        return total;
    }
}
