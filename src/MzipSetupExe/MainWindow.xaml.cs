using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MzipSetupExe;

public partial class MainWindow : Window
{
    private const string AppName = "Mzip";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Mzip";
    private const string Publisher = "MSoft";
    private const string Website = "https://msoft.com.es";

    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private bool _busy;
    private bool _installed;

    public MainWindow()
    {
        InitializeComponent();

        SubtitleText.Text = $"Instalador · versión {AppVersion}";
        VersionText.Text = $"v{AppVersion} · {Publisher}";
        DestBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        UpdateAdminState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.ApplyDark(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void UpdateAdminState()
    {
        bool isAdmin;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            isAdmin = false;
        }

        if (isAdmin)
        {
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x2A, 0x22));
            AdminBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x50, 0x3A));
            AdminDot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
            AdminText.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
            AdminText.Text = "Administrador";
            HintText.Text = "El instalador copiará Mzip en Program Files, creará los accesos directos y registrará la desinstalación en Windows.";
        }
        else
        {
            AdminBadge.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x21, 0x14));
            AdminBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x3C, 0x1B));
            AdminDot.Fill = new SolidColorBrush(Color.FromRgb(0xF6, 0x82, 0x1F));
            AdminText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x6B));
            AdminText.Text = "Sin privilegios";
            HintText.Text = "Este instalador necesita permisos de administrador. Cierra y vuelve a abrirlo con «Ejecutar como administrador».";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dlg = new OpenFolderDialog
        {
            Title = "Elige la carpeta de instalación",
            InitialDirectory = Directory.Exists(DestBox.Text)
                ? DestBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Multiselect = false
        };

        if (dlg.ShowDialog(this) == true) DestBox.Text = dlg.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_installed)
        {
            Close();
            return;
        }

        var target = DestBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
        {
            MessageBox.Show(this, "Indica una ruta de instalación válida (por ejemplo C:\\Program Files\\Mzip).",
                "Ruta no válida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(Path.GetPathRoot(target)?.TrimEnd('\\'), target.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "No se puede instalar directamente en la raíz de una unidad.",
                "Ruta no válida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exePath = Path.Combine(target, AppName + ".exe");
        if (File.Exists(exePath))
        {
            var answer = MessageBox.Show(this,
                $"Ya existe una instalación de {AppName} en:\n{target}\n\n¿Deseas sobrescribirla?",
                "Instalación existente", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        SetBusy(true);
        Progress.Value = 0;

        try
        {
            KillRunningInstances();

            StatusText.Text = "Preparando carpeta de instalación...";
            Directory.CreateDirectory(target);

            long payloadSize;
            using (var payload = OpenPayload())
            {
                payloadSize = payload?.Length ?? 0;
                if (payload is null) throw new InvalidOperationException("No se encontró Mzip.exe dentro del instalador.");

                var last = 0;
                var progress = new Progress<int>(p =>
                {
                    var mapped = (int)(p * 0.72);
                    if (mapped < last) mapped = last;
                    if (mapped == last) return;
                    last = mapped;
                    Progress.Value = mapped;
                    StatusText.Text = $"Copiando Mzip.exe... {mapped}%";
                });
                await CopyPayloadAsync(target, payload, progress);
            }

            Progress.Value = 75;
            StatusText.Text = "Creando desinstalador...";
            WriteUninstaller(target, payloadSize);

            var warnings = new List<string>();

            Progress.Value = 82;
            StatusText.Text = "Creando accesos directos...";
            try
            {
                CreateShortcuts(target,
                    DesktopCheck.IsChecked == true,
                    StartMenuCheck.IsChecked == true);
            }
            catch (Exception ex)
            {
                warnings.Add("Accesos directos: " + ex.Message);
            }

            Progress.Value = 92;
            StatusText.Text = "Registrando en Windows...";
            try
            {
                RegisterUninstall(target, payloadSize);
                RegisterShellIntegration(target, ShellCheck.IsChecked == true);
            }
            catch (Exception ex)
            {
                warnings.Add("Registro de Windows: " + ex.Message);
            }

            Progress.Value = 100;
            _installed = true;
            InstallButton.Content = "Cerrar";
            DestBox.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            DesktopCheck.IsEnabled = false;
            StartMenuCheck.IsEnabled = false;
            ShellCheck.IsEnabled = false;

            StatusText.Text = warnings.Count == 0
                ? $"{AppName} instalado correctamente en {target}"
                : $"Instalado en {target}. Avisos: {string.Join(" · ", warnings)}";

            if (LaunchCheck.IsChecked == true)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    StatusText.Text += $" (No se pudo abrir: {ex.Message})";
                }
            }
        }
        catch (Exception ex)
        {
            Progress.Value = 0;
            StatusText.Text = "La instalación no se completó.";
            MessageBox.Show(this, $"No se pudo instalar {AppName}:\n\n{ex.Message}",
                "Error de instalación", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        var controls = new FrameworkElement[]
        {
            DestBox, BrowseButton, DesktopCheck, StartMenuCheck, ShellCheck, LaunchCheck, CloseButton
        };
        foreach (var c in controls) c.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;

        if (busy)
        {
            InstallButton.Content = "Instalando...";
        }
        else if (!_installed)
        {
            InstallButton.Content = "Instalar";
        }

        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private static Stream OpenPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Mzip.exe", StringComparison.OrdinalIgnoreCase));

        return name is null
            ? Stream.Null
            : assembly.GetManifestResourceStream(name)!;
    }

    private static async Task CopyPayloadAsync(string targetDir, Stream payload, IProgress<int> progress)
    {
        var target = Path.Combine(targetDir, AppName + ".exe");
        var temp = target + ".new";
        var total = payload.Length;
        var buffer = new byte[65536];
        long copied = 0;

        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                var lastReport = 0;
                while ((read = await payload.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    copied += read;
                    if (total <= 0) continue;

                    var percent = (int)(copied * 100L / total);
                    if (percent <= lastReport) continue;
                    lastReport = percent;
                    progress.Report(percent);
                }
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void WriteUninstaller(string targetDir, long payloadSize)
    {
        var cmdPath = Path.Combine(targetDir, "uninstall.cmd");
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            "if exist \"%~dp0Mzip.exe\" \"%~dp0Mzip.exe\" --unregister-shell >nul 2>&1",
            "taskkill /IM Mzip.exe /F >nul 2>&1",
            $"reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{AppName}\" /f >nul 2>&1",
            "del /f /q \"%PUBLIC%\\Desktop\\Mzip.lnk\" >nul 2>&1",
            "del /f /q \"%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs\\Mzip.lnk\" >nul 2>&1",
            "cd /d \"%TEMP%\"",
            "start \"\" /b powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command " +
            "\"Start-Sleep -Seconds 2; Remove-Item -LiteralPath '%~dp0' -Recurse -Force -ErrorAction SilentlyContinue\"",
            "exit /b 0",
            ""
        };
        File.WriteAllLines(cmdPath, lines);
    }

    private static void RegisterShellIntegration(string targetDir, bool enabled)
    {
        if (!enabled) return;

        var exePath = Path.Combine(targetDir, AppName + ".exe");
        using var process = Process.Start(new ProcessStartInfo(exePath, "--register-shell")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        process?.WaitForExit(15000);
    }

    private static void CreateShortcuts(string targetDir, bool desktop, bool startMenu)
    {
        if (!desktop && !startMenu) return;

        var exePath = Path.Combine(targetDir, AppName + ".exe");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell no está disponible.");
        dynamic shell = Activator.CreateInstance(shellType)!;

        try
        {
            if (desktop)
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (string.IsNullOrEmpty(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateLink(shell, Path.Combine(dir, AppName + ".lnk"), exePath, targetDir);
            }

            if (startMenu)
            {
                var programs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs");
                Directory.CreateDirectory(programs);
                CreateLink(shell, Path.Combine(programs, AppName + ".lnk"), exePath, targetDir);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static void CreateLink(dynamic shell, string linkPath, string exePath, string targetDir)
    {
        dynamic link = shell.CreateShortcut(linkPath);
        try
        {
            link.TargetPath = exePath;
            link.WorkingDirectory = targetDir;
            link.IconLocation = exePath + ",0";
            link.Description = "Compresor y extractor de archivos";
            link.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private static void RegisterUninstall(string targetDir, long payloadSize)
    {
        var exePath = Path.Combine(targetDir, AppName + ".exe");
        var uninstallCmd = Path.Combine(targetDir, "uninstall.cmd");
        var uninstallString =
            $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process -FilePath '{uninstallCmd}' -Verb RunAs\"";

        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, writable: true)
                        ?? throw new InvalidOperationException("No se pudo crear la clave de desinstalación.");

        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", Publisher);
        key.SetValue("URLInfoAbout", Website);
        key.SetValue("DisplayIcon", exePath + ",0");
        key.SetValue("InstallLocation", targetDir);
        key.SetValue("UninstallString", uninstallString);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Max(1, payloadSize / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void KillRunningInstances()
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            try
            {
                process.Kill();
                process.WaitForExit(4000);
            }
            catch
            {
                // El proceso puede haber terminado o pertenecer a otro usuario.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

internal static class TitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDark(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var enabled = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

        var rounded = 2;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
    }
}
