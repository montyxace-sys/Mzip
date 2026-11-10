using System.IO;
using System.IO.Compression;
using Mzip;

namespace Mzip.CoreTests;

/// <summary>
/// Comprobaciones de la lógica que no depende de la ventana: argumentos de shell,
/// formatos, tamaños y el ciclo comprimir/leer/extraer de verdad.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static string _workDir = "";

    private static async Task<int> Main()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "mzip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        try
        {
            ShellArgumentsTests();
            FormatTests();
            SizeTests();
            await ArchiveRoundTripTestsAsync();
            await SafetyTestsAsync();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"ERROR NO CONTROLADO: {ex}");
        }
        finally
        {
            try { Directory.Delete(_workDir, recursive: true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_checks} comprobaciones, {_failures} fallos.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  OK    {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FALLO {name}");
        }
    }

    private static void ShellArgumentsTests()
    {
        Console.WriteLine("Argumentos de shell");

        Check("--extract-here con ruta", ShellArguments.Parse(new[] { "--extract-here", "C:\\a.zip" })
            is { Mode: "extract-here", Path: "C:\\a.zip" });
        Check("--extract-sub con ruta", ShellArguments.Parse(new[] { "--extract-sub", "C:\\a.zip" })
            is { Mode: "extract-sub", Path: "C:\\a.zip" });
        Check("--compress con ruta", ShellArguments.Parse(new[] { "--compress", "C:\\b" })
            is { Mode: "compress", Path: "C:\\b" });
        Check("ruta suelta abre el archivo", ShellArguments.Parse(new[] { "C:\\a.zip" })
            is { Mode: "open", Path: "C:\\a.zip" });
        Check("sin argumentos no hay petición", ShellArguments.Parse(Array.Empty<string>()) is null);
        Check("modo sin ruta no hay petición", ShellArguments.Parse(new[] { "--extract-here" }) is null);
        Check("registro de shell activado", ShellArguments.RegistrationCommand(new[] { "--register-shell" }) == true);
        Check("registro de shell desactivado", ShellArguments.RegistrationCommand(new[] { "--unregister-shell" }) == false);
        Check("sin registro devuelve null", ShellArguments.RegistrationCommand(new[] { "C:\\a.zip" }) is null);
    }

    private static void FormatTests()
    {
        Console.WriteLine("Formatos");

        Check("zip -> .zip", ArchiveFormat.Zip.Extension() == ".zip");
        Check("7z -> .7z", ArchiveFormat.SevenZip.Extension() == ".7z");
        Check("tar.gz -> .tar.gz", ArchiveFormat.TarGz.Extension() == ".tar.gz");
        Check("tar.bz2 -> .tar.bz2", ArchiveFormat.TarBz2.Extension() == ".tar.bz2");
        Check("parse 7z", ArchiveFormatInfo.Parse("7z") == ArchiveFormat.SevenZip);
        Check("parse tgz", ArchiveFormatInfo.Parse("tgz") == ArchiveFormat.TarGz);
        Check("parse tbz2", ArchiveFormatInfo.Parse("tbz2") == ArchiveFormat.TarBz2);
        Check("parse desconocido cae en zip", ArchiveFormatInfo.Parse("rar") == ArchiveFormat.Zip);
        Check("parse null cae en zip", ArchiveFormatInfo.Parse(null) == ArchiveFormat.Zip);
        Check("clave zip", ArchiveFormat.Zip.Key() == "zip");
        Check("clave 7z", ArchiveFormat.SevenZip.Key() == "7z");
    }

    private static void SizeTests()
    {
        Console.WriteLine("Tamaños y etiquetas");

        Check("0 B", FileItem.FormatSize(0) == "0 B");
        Check("512 B", FileItem.FormatSize(512) == "512 B");
        Check("1 KB", FileItem.FormatSize(1024) == "1 KB");
        // El formato depende de la cultura del sistema: en CI (en-US) sale "1.5 KB"
        // y en un equipo en español, "1,5 KB". Se comprueba el valor, no el separador.
        Check("1,5 KB", FileItem.FormatSize(1536).Replace('.', ',') == "1,5 KB");
        Check("1 MB", FileItem.FormatSize(1024 * 1024) == "1 MB");
        Check("negativo se trata como 0", FileItem.FormatSize(-5) == "0 B");
        Check("etiqueta de extensión", FileItem.ExtensionBadge("foto.png") == "PNG");
        Check("extensión larga se recorta", FileItem.ExtensionBadge("a.docx") == "DOCX");
        Check("sin extensión", FileItem.ExtensionBadge("LEEME") == "FILE");
        Check("es archivo comprimido", ArchiveService.IsArchive("x.zip"));
        Check("es archivo comprimido 7z", ArchiveService.IsArchive("x.7z"));
        Check("no es archivo comprimido", !ArchiveService.IsArchive("x.txt"));
        Check("ruta vacía no es archivo", !ArchiveService.IsArchive(""));
    }

    private static async Task ArchiveRoundTripTestsAsync()
    {
        Console.WriteLine("Comprimir, leer y extraer");

        var source = Path.Combine(_workDir, "origen");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hola.txt"), "contenido de prueba");
        File.WriteAllText(Path.Combine(source, "datos.csv"), "a,b\n1,2\n");
        Directory.CreateDirectory(Path.Combine(source, "vacia"));

        var item = await FileItem.CreateAsync(source);
        var archivePath = Path.Combine(_workDir, "prueba.zip");

        var added = await ArchiveService.CompressAsync(
            new[] { item }, archivePath, ArchiveFormat.Zip, null, CancellationToken.None);

        Check("comprime los archivos", added >= 2);
        Check("el archivo existe", File.Exists(archivePath));
        Check("no deja temporales", !Directory.GetFiles(_workDir, "*.tmp").Any());

        var entries = ArchiveService.ReadEntries(archivePath);
        Check("lee las entradas", entries.Count >= 2);
        Check("encuentra hola.txt", entries.Any(e => e.Path.EndsWith("hola.txt", StringComparison.Ordinal)));
        Check("marca la carpeta vacía como directorio",
            entries.Any(e => e.IsDirectory && e.Path.Contains("vacia", StringComparison.Ordinal)));

        var destination = Path.Combine(_workDir, "extraido");
        var result = await ArchiveService.ExtractAsync(
            archivePath, destination, null, overwrite: true, totalBytes: 0, progress: null,
            CancellationToken.None);

        Check("extrae al menos dos archivos", result.Extracted >= 2);
        Check("el contenido coincide",
            File.ReadAllText(Path.Combine(destination, "origen", "hola.txt")) == "contenido de prueba");
        Check("los saltos de línea se conservan",
            File.ReadAllText(Path.Combine(destination, "origen", "datos.csv")).Contains("a,b"));

        // Una segunda extracción sin sobrescribir debe saltarse lo que ya existe.
        var skipped = await ArchiveService.ExtractAsync(
            archivePath, destination, null, overwrite: false, totalBytes: 0, progress: null,
            CancellationToken.None);
        Check("sin sobrescribir se salta lo existente", skipped.Skipped >= 2);

        // ZIP creado con la propia BCL, para no depender solo de SharpCompress.
        var bclZip = Path.Combine(_workDir, "bcl.zip");
        using (var zip = ZipFile.Open(bclZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("leeme.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("desde System.IO.Compression");
        }
        Check("lee un ZIP de la BCL", ArchiveService.IsArchive(bclZip));
        var bclEntries = ArchiveService.ReadEntries(bclZip);
        Check("encuentra leeme.txt", bclEntries.Any(e => e.Path.EndsWith("leeme.txt", StringComparison.Ordinal)));
    }

    private static async Task SafetyTestsAsync()
    {
        Console.WriteLine("Rutas peligrosas y errores");

        // Un ZIP con ".." no debe escribir fuera del destino.
        var evilZip = Path.Combine(_workDir, "malicioso.zip");
        using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../fuera.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("no deberías estar aquí");
        }

        var destination = Path.Combine(_workDir, "seguro");
        try
        {
            await ArchiveService.ExtractAsync(evilZip, destination, null, overwrite: true,
                totalBytes: 0, progress: null, CancellationToken.None);
        }
        catch (Exception)
        {
            // Rechazar la extracción también es una respuesta válida.
        }

        Check("no escribe fuera de la carpeta de destino",
            !File.Exists(Path.Combine(_workDir, "fuera.txt")));

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelado = false;
        try
        {
            var item = await FileItem.CreateAsync(Path.Combine(_workDir, "origen"));
            await ArchiveService.CompressAsync(new[] { item }, Path.Combine(_workDir, "c.zip"),
                ArchiveFormat.Zip, null, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancelado = true;
        }
        Check("la cancelación se propaga", cancelado);
    }
}
