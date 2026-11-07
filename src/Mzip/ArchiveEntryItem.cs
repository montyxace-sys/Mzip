namespace Mzip;

public sealed class ArchiveEntryItem
{
    public ArchiveEntryItem(string path, long size, bool isDirectory)
    {
        Path = path;
        Size = size;
        IsDirectory = isDirectory;
    }

    public string Path { get; }
    public long Size { get; }
    public bool IsDirectory { get; }

    public string Badge => IsDirectory ? "DIR" : FileItem.ExtensionBadge(Path);
    public string SizeText => IsDirectory ? "Carpeta" : FileItem.FormatSize(Size);
}
