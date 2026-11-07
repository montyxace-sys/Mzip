using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mzip;

public partial class MainWindow : Window
{
    private const int MaxDisplayedEntries = 3000;

    private readonly ObservableCollection<FileItem> _files = new();
    private readonly ObservableCollection<ArchiveEntryItem> _entries = new();

    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _shellInitializing;
    private string? _archivePath;
    private long _archiveUncompressed;
    private int _archiveEntriesCount;
    private string? _lastFolder;

    public MainWindow()
    {
        InitializeComponent();

        FilesList.ItemsSource = _files;
        EntriesList.ItemsSource = _entries;
        _files.CollectionChanged += (_, _) => UpdateFilesState();

        DestinationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mzip");
        ArchiveNameBox.Text = $"mzip-{DateTime.Now:yyyyMMdd-HHmm}";
        ExtractDestBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        UpdateAdminState();
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        VersionLabel.Text = $"v{version} · MSoft";
        UpdateFilesState();
        UpdateOutputHint();
        UpdateShellIntegrationState();
        SetStatus("Listo. Añade archivos o abre un comprimido.");
    }

    // ---------- Interfaz general ----------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (CompressPanel is null || ExtractPanel is null) return;
        var compress = ReferenceEquals(sender, CompressTab);
        CompressPanel.Visibility = compress ? Visibility.Visible : Visibility.Collapsed;
        ExtractPanel.Visibility = compress ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Options_TextChanged(object sender, TextChangedEventArgs e) => UpdateOutputHint();

    private void Options_Checked(object sender, RoutedEventArgs e) => UpdateOutputHint();

    private void SetStatus(string message) => StatusText.Text = message;

    private void UpdateFilesState()
    {
        if (FilesList is null) return;
        var count = _files.Count;
        FileCountText.Text = count == 1 ? "1 elemento" : $"{count} elementos";
        FilesEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesTotalText.Text = FileItem.FormatSize(_files.Sum(item => item.Size));
        UpdateBusyState();
    }

    private void UpdateBusyState()
    {
        if (CompressButton is null) return;

        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        CompressButton.IsEnabled = !_busy && _files.Count > 0;
        AddFilesButton.IsEnabled = !_busy;
        AddFolderButton.IsEnabled = !_busy;
        ClearButton.IsEnabled = !_busy && _files.Count > 0;
        CompressToolsPanel.IsEnabled = !_busy;
        CompressOptionsPanel.IsEnabled = !_busy;
        ExtractOptionsPanel.IsEnabled = !_busy;
        BrowseArchiveButton.IsEnabled = !_busy;
        ExtractButton.IsEnabled = !_busy && _archivePath is not null;
    }

    private void BeginOperation(string status)
    {
        _busy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Progress.Value = 0;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        SetStatus(status);
        UpdateBusyState();
    }

    private void EndOperation()
    {
        _busy = false;
        _cts?.Dispose();
        _cts = null;
        UpdateBusyState();
    }

    private void UpdateAdminState()
    {
        var accent = (Brush)FindResource(Elevation.IsElevated ? "SuccessBrush" : "MutedBrush");

        if (Elevation.IsElevated)
        {
            AdminText.Text = "Administrador";
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x2A, 0x22));
            AdminBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x50, 0x3A));
            AdminBadge.ToolTip = "Mzip se ejecuta con permisos de administrador.";
        }
        else
        {
            AdminText.Text = "Modo estándar";
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1E, 0x29));
            AdminBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x31, 0x3E));
            AdminBadge.ToolTip = "Se pedirán permisos de administrador solo si el destino lo requiere.";
        }

        AdminText.Foreground = accent;
        AdminDot.Fill = accent;
    }

    // ---------- Selección de archivos ----------

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await PickFilesAsync();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Añadir carpeta", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FolderNames);
    }

    private async void CompressDrop_Click(object sender, MouseButtonEventArgs e) => await PickFilesAsync();

    private async Task PickFilesAsync()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Añadir archivos",
            Multiselect = true,
            Filter = "Todos los archivos|*.*"
        };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) return;

        var added = 0;
        var skipped = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string clean;
            try
            {
                clean = Path.TrimEndingDirectorySeparator(path);
                if (!File.Exists(clean) && !Directory.Exists(clean)) continue;
            }
            catch
            {
                continue;
            }

            if (_files.Any(item => string.Equals(item.SourcePath, clean, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var item = await FileItem.CreateAsync(clean);
            _files.Add(item);
            added++;
        }

        if (added > 0) SetStatus($"Añadidos {added} elemento(s) · {_files.Count} en la lista.");
        else if (skipped > 0) SetStatus("Los elementos ya estaban en la lista.");
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileItem item }) _files.Remove(item);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _files.Clear();
        SetStatus("Lista vaciada.");
    }

    private void FilesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _busy) return;
        foreach (var item in FilesList.SelectedItems.OfType<FileItem>().ToList()) _files.Remove(item);
        e.Handled = true;
    }

    // ---------- Compresión ----------

    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var missing = _files
            .Where(item => !File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
            .ToList();
        foreach (var item in missing) _files.Remove(item);
        if (_files.Count == 0)
        {
            SetStatus("No hay archivos para comprimir.");
            return;
        }

        var destination = DestinationBox.Text.Trim();
        if (destination.Length == 0 || !Path.IsPathRooted(destination))
        {
            ShowWarning("Elige una carpeta de destino válida (ruta completa).");
            return;
        }

        var format = SelectedFormat;
        var name = BuildArchiveName(format);
        string output;
        try
        {
            output = Path.Combine(destination, name);
        }
        catch (Exception ex)
        {
            ShowError("La ruta de destino no es válida", ex);
            return;
        }

        try
        {
            if (_files.Any(item => string.Equals(
                    Path.GetFullPath(item.SourcePath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
            {
                ShowWarning("El archivo de salida no puede ser uno de los archivos que se van a comprimir.");
                return;
            }
        }
        catch
        {
            // Rutas sin formato válido: el error real aparecerá al comprimir.
        }

        if (File.Exists(output))
        {
            var answer = MessageBox.Show(this, $"Ya existe «{name}».\n\n¿Quieres reemplazarlo?", "Mzip",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        BeginOperation($"Comprimiendo {name}…");
        var progress = new Progress<ProgressInfo>(info =>
        {
            Progress.Value = info.Percent;
            SetStatus(info.Message);
        });

        try
        {
            var token = _cts!.Token;
            var count = await ArchiveService.CompressAsync(_files.ToList(), output, format, progress, token);
            var size = new FileInfo(output).Length;

            _lastFolder = destination;
            OpenFolderButton.Visibility = Visibility.Visible;
            Progress.Value = 100;
            SetStatus($"Creado «{name}» · {count} archivo(s) · {FileItem.FormatSize(size)}");
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            SetStatus("Compresión cancelada.");
        }
        catch (Exception ex) when (IsAccessDenied(ex) && !Elevation.IsElevated)
        {
            EndOperation();
            if (ConfirmElevation(destination))
            {
                await CompressElevatedAsync(new JobRequest
                {
                    Type = "compress",
                    ArchivePath = output,
                    Format = format.Key(),
                    Files = _files.Select(item => item.SourcePath).ToList()
                }, destination, name);
            }
            else
            {
                SetStatus("Operación cancelada: permisos insuficientes.");
            }
        }
        catch (Exception ex)
        {
            ShowError("No se pudo comprimir", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private ArchiveFormat SelectedFormat =>
        SevenZipFormatTab?.IsChecked == true ? ArchiveFormat.SevenZip :
        TarGzFormatTab?.IsChecked == true ? ArchiveFormat.TarGz :
        TarBz2FormatTab?.IsChecked == true ? ArchiveFormat.TarBz2 :
        TarFormatTab?.IsChecked == true ? ArchiveFormat.Tar :
        ArchiveFormat.Zip;

    private string BuildArchiveName(ArchiveFormat format)
    {
        var name = SanitizeFileName(ArchiveNameBox?.Text ?? string.Empty);
        foreach (var known in new[] { ".tar.gz", ".tar.bz2", ".tgz", ".tbz2", ".zip", ".tar", ".7z" })
        {
            if (!name.EndsWith(known, StringComparison.OrdinalIgnoreCase)) continue;
            name = name[..^known.Length];
            break;
        }

        name = name.Trim();
        if (name.Length == 0) name = $"mzip-{DateTime.Now:yyyyMMdd-HHmm}";
        return name + format.Extension();
    }

    private void UpdateOutputHint()
    {
        if (OutputHintText is null || DestinationBox is null) return;
        try
        {
            var name = BuildArchiveName(SelectedFormat);
            OutputHintText.Text = name;
            OutputHintText.ToolTip = Path.Combine(DestinationBox.Text.Trim(), name);
        }
        catch
        {
            // La vista previa es informativa; no debe impedir escribir la ruta.
        }
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Carpeta de destino", InitialDirectory = DestinationBox.Text };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    // ---------- Extracción ----------

    private async void BrowseArchive_Click(object sender, RoutedEventArgs e) => await PickArchiveAsync();

    private async void ExtractDrop_Click(object sender, MouseButtonEventArgs e) => await PickArchiveAsync();

    private async Task PickArchiveAsync()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Abrir archivo comprimido",
            Filter = ArchiveService.FileFilter,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await LoadArchiveAsync(dialog.FileName);
    }

    private async Task LoadArchiveAsync(string path)
    {
        if (_busy) return;
        if (!File.Exists(path))
        {
            ShowWarning("El archivo ya no existe.");
            return;
        }
        if (!ArchiveService.IsArchive(path))
        {
            ShowWarning("Formato no compatible. Usa ZIP, TAR, 7Z, RAR, GZ, BZ2 o XZ.");
            return;
        }

        BeginOperation($"Abriendo {Path.GetFileName(path)}…");
        try
        {
            var token = _cts!.Token;
            IReadOnlyList<ArchiveEntryItem> entries;
            string? readError = null;

            try
            {
                var password = ExtractPassword.Password;
                entries = await Task.Run(() => ArchiveService.ReadEntries(path, password), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entries = Array.Empty<ArchiveEntryItem>();
                readError = Describe(ex);
            }

            _archivePath = path;
            _archiveEntriesCount = entries.Count;
            _archiveUncompressed = entries.Sum(item => item.Size);

            _entries.Clear();
            foreach (var entry in entries.Take(MaxDisplayedEntries)) _entries.Add(entry);

            ArchiveInfo.Visibility = Visibility.Visible;
            ArchiveBadgeText.Text = FileItem.ExtensionBadge(path);
            ArchiveNameText.Text = Path.GetFileName(path);

            if (readError is null)
            {
                ArchiveMetaText.Text = $"{entries.Count:N0} entrada(s) · {FileItem.FormatSize(_archiveUncompressed)}";
                ExtractSummaryText.Text = $"{entries.Count:N0} entrada(s) · {FileItem.FormatSize(_archiveUncompressed)}";
                ExtractEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetStatus($"Abierto «{Path.GetFileName(path)}».");
            }
            else
            {
                ArchiveMetaText.Text = "No se pudo listar el contenido";
                ExtractSummaryText.Text = "Sin listado";
                ExtractEmpty.Visibility = Visibility.Collapsed;
                SetStatus($"Aviso: {readError} Indica la contraseña y pulsa Extraer para intentarlo.");
            }

            try
            {
                var parent = Path.GetDirectoryName(path)
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                ExtractDestBox.Text = Path.Combine(parent, StripArchiveExtension(Path.GetFileName(path)));
            }
            catch
            {
                // Se mantiene la carpeta que el usuario ya tuviera escrita.
            }
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            SetStatus("Lectura cancelada.");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e) => await ExtractCoreAsync();

    private async Task ExtractCoreAsync()
    {
        if (_busy || _archivePath is null) return;

        var destination = ExtractDestBox.Text.Trim();
        if (destination.Length == 0 || !Path.IsPathRooted(destination))
        {
            ShowWarning("Elige una carpeta de destino válida (ruta completa).");
            return;
        }

        BeginOperation($"Extrayendo {Path.GetFileName(_archivePath)}…");
        var progress = new Progress<ProgressInfo>(info =>
        {
            Progress.Value = info.Percent;
            SetStatus(info.Message);
        });

        try
        {
            var token = _cts!.Token;
            var overwrite = OverwriteCheck.IsChecked == true;
            var result = await ArchiveService.ExtractAsync(
                _archivePath, destination, ExtractPassword.Password, overwrite,
                _archiveUncompressed, progress, token);

            _lastFolder = destination;
            OpenFolderButton.Visibility = Visibility.Visible;
            Progress.Value = 100;

            var text = $"Extraídos {result.Extracted} archivo(s)";
            if (result.Skipped > 0) text += $" · {result.Skipped} omitido(s)";
            SetStatus($"{text} en {destination}");
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            SetStatus("Extracción cancelada.");
        }
        catch (Exception ex) when (IsAccessDenied(ex) && !Elevation.IsElevated)
        {
            EndOperation();
            if (ConfirmElevation(destination))
            {
                await ExtractElevatedAsync(new JobRequest
                {
                    Type = "extract",
                    ArchivePath = _archivePath,
                    Destination = destination,
                    Password = ExtractPassword.Password,
                    Overwrite = OverwriteCheck.IsChecked == true
                }, destination);
            }
            else
            {
                SetStatus("Operación cancelada: permisos insuficientes.");
            }
        }
        catch (Exception ex)
        {
            ShowError("No se pudo extraer", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private void BrowseExtractDest_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "Carpeta de destino", InitialDirectory = ExtractDestBox.Text };
        if (dialog.ShowDialog(this) == true) ExtractDestBox.Text = dialog.FolderName;
    }

    // ---------- Arrastrar y soltar ----------

    private void Window_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = GetPaths(e);
        if (ExtractPanel.Visibility == Visibility.Visible) await DropArchiveAsync(paths);
        else await AddPathsAsync(paths);
    }

    private void CompressDrop_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private async void CompressDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await AddPathsAsync(GetPaths(e));
    }

    private void ExtractDrop_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private async void ExtractDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await DropArchiveAsync(GetPaths(e));
    }

    private static void SetDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[] GetPaths(DragEventArgs e)
    {
        return e.Data.GetDataPresent(DataFormats.FileDrop)
            ? (string[])e.Data.GetData(DataFormats.FileDrop)!
            : Array.Empty<string>();
    }

    private async Task DropArchiveAsync(IReadOnlyList<string> paths)
    {
        var archive = paths.FirstOrDefault(ArchiveService.IsArchive);
        if (archive is null)
        {
            ShowWarning("Arrastra un único archivo comprimido compatible (ZIP, TAR, 7Z, RAR, GZ, BZ2 o XZ).");
            return;
        }
        await LoadArchiveAsync(archive);
    }

    // ---------- Cancelar, carpeta y cierre ----------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        _cts?.Cancel();
        SetStatus("Cancelando…");
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _busy)
        {
            _cts?.Cancel();
            SetStatus("Cancelando…");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastFolder) || !Directory.Exists(_lastFolder)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_lastFolder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("No se pudo abrir la carpeta", ex);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            SetStatus("Cancela la operación antes de cerrar.");
        }
        base.OnClosing(e);
    }

    // ---------- Elevación bajo demanda ----------

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException && (ex.HResult & 0xFFFF) == 5);

    private bool ConfirmElevation(string target)
    {
        var answer = MessageBox.Show(this,
            $"Windows no permite escribir en:\n{target}\n\n¿Reintentar la operación con permisos de administrador?",
            "Mzip", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes;
    }

    private async Task CompressElevatedAsync(JobRequest request, string destination, string name)
    {
        BeginOperation("Solicitando permisos de administrador…");
        Progress.IsIndeterminate = true;

        try
        {
            var result = await Elevation.RunElevatedAsync(request, _cts!.Token,
                () => SetStatus("Comprimiendo como administrador…"));
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "La operación elevada no se completó.");

            _lastFolder = destination;
            OpenFolderButton.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 100;

            var size = new FileInfo(request.ArchivePath!).Length;
            SetStatus($"Creado «{name}» · {result.Count} archivo(s) · {FileItem.FormatSize(size)}");
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            SetStatus("Compresión cancelada.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Progress.Value = 0;
            SetStatus("Permisos de administrador no concedidos.");
        }
        catch (Exception ex)
        {
            ShowError("No se pudo comprimir", ex);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            EndOperation();
        }
    }

    private async Task ExtractElevatedAsync(JobRequest request, string destination)
    {
        BeginOperation("Solicitando permisos de administrador…");
        Progress.IsIndeterminate = true;

        try
        {
            var result = await Elevation.RunElevatedAsync(request, _cts!.Token,
                () => SetStatus("Extrayendo como administrador…"));
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "La operación elevada no se completó.");

            _lastFolder = destination;
            OpenFolderButton.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 100;

            var text = $"Extraídos {result.Count} archivo(s)";
            if (result.Skipped > 0) text += $" · {result.Skipped} omitido(s)";
            SetStatus($"{text} en {destination}");
        }
        catch (OperationCanceledException)
        {
            Progress.Value = 0;
            SetStatus("Extracción cancelada.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Progress.Value = 0;
            SetStatus("Permisos de administrador no concedidos.");
        }
        catch (Exception ex)
        {
            ShowError("No se pudo extraer", ex);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            EndOperation();
        }
    }

    // ---------- Menú contextual de Windows ----------

    internal async Task RunShellAsync(ShellRequest request)
    {
        if (request.Mode == "compress")
        {
            CompressTab.IsChecked = true;
            await AddPathsAsync(new[] { request.Path });
            return;
        }

        var archive = request.Path;
        if (!File.Exists(archive))
        {
            ShowWarning($"No se encontró «{archive}».");
            return;
        }
        if (!ArchiveService.IsArchive(archive))
        {
            ShowWarning("El archivo no es un comprimido compatible.");
            return;
        }

        ExtractTab.IsChecked = true;
        await LoadArchiveAsync(archive);
        if (_archivePath is null || request.Mode == "open") return;

        string destination;
        try
        {
            var parent = Path.GetDirectoryName(archive);
            if (string.IsNullOrEmpty(parent))
                parent = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            destination = request.Mode == "extract-sub"
                ? Path.Combine(parent, StripArchiveExtension(Path.GetFileName(archive)))
                : parent;
        }
        catch (Exception ex)
        {
            ShowError("No se pudo preparar la extracción", ex);
            return;
        }

        ExtractDestBox.Text = destination;
        OverwriteCheck.IsChecked = true;
        await ExtractCoreAsync();
    }

    private void UpdateShellIntegrationState()
    {
        if (ShellIntegrationCheck is null) return;
        _shellInitializing = true;
        try
        {
            ShellIntegrationCheck.IsChecked = ShellIntegration.IsRegistered();
        }
        finally
        {
            _shellInitializing = false;
        }
    }

    private void ShellIntegration_Toggled(object sender, RoutedEventArgs e)
    {
        if (_shellInitializing) return;

        var enable = ShellIntegrationCheck.IsChecked == true;
        try
        {
            ShellIntegration.Apply(enable);
            SetStatus(enable
                ? "Mzip añadido al menú contextual de Windows."
                : "Mzip eliminado del menú contextual de Windows.");
        }
        catch (Exception ex)
        {
            _shellInitializing = true;
            ShellIntegrationCheck.IsChecked = !enable;
            _shellInitializing = false;
            ShowError("No se pudo actualizar el menú contextual", ex);
        }
    }

    // ---------- Utilidades ----------

    private static string StripArchiveExtension(string fileName)
    {
        string[] extensions =
        {
            ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz",
            ".zip", ".tar", ".7z", ".rar", ".gz", ".bz2", ".xz"
        };
        foreach (var extension in extensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return fileName[..^extension.Length];
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);
        foreach (var character in input)
            builder.Append(invalid.Contains(character) ? '-' : character);
        return builder.ToString().Trim().TrimEnd('.');
    }

    private static string Describe(Exception ex)
    {
        return ex switch
        {
            CryptographicException => "Contraseña incorrecta o el archivo está protegido.",
            UnauthorizedAccessException => "Permiso denegado. Comprueba la ruta o los permisos.",
            InvalidDataException invalid => invalid.Message,
            PathTooLongException => "La ruta es demasiado larga.",
            IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                => "El archivo está en uso por otro programa.",
            _ => ex.Message
        };
    }

    private void ShowWarning(string message)
    {
        SetStatus(message);
        MessageBox.Show(this, message, "Mzip", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowError(string title, Exception ex)
    {
        var message = Describe(ex);
        SetStatus($"{title}: {message}");
        MessageBox.Show(this, $"{title}.\n\n{message}", "Mzip", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---------- Barra de título oscura ----------

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));

            var rounded = 2;
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        }
        catch
        {
            // En versiones antiguas de Windows la barra queda con el color del sistema.
        }
    }
}
