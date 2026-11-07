# Mzip

Compresor y extractor de archivos para Windows con interfaz oscura, escrito en WPF sobre .NET 10.

Mzip se distribuye de dos formas: un único `Mzip.exe` portable que no necesita instalación, y
`MzipSetup.exe`, que instala la aplicación en `Program Files`, crea accesos directos y registra
la desinstalación en Windows.

## Características

- Comprime en **ZIP** y **TAR** por streaming, sin cargar los archivos en memoria.
- Extrae **ZIP, TAR, TAR.GZ/TGZ, TAR.BZ2/TBZ2, TAR.XZ/TXZ, 7Z, RAR, GZ, BZ2 y XZ**.
- Contraseña para archivos cifrados y mensaje claro cuando es incorrecta.
- Arrastrar y soltar, o selección mediante diálogos de archivo y carpeta.
- Conserva las carpetas vacías y la fecha de modificación de cada archivo.
- Protección contra *zip-slip*: rechaza entradas con `..`, rutas absolutas o fuera del destino.
- Sobrescritura opcional, omisión del resto y cancelación en cualquier momento.
- Progreso real medido en bytes y estado detallado.
- Instalador con instalación por máquina, accesos directos, desinstalador y registro en
  «Aplicaciones instaladas».

## Requisitos

- Windows 10 u 11 de 64 bits.
- Para compilar: [SDK de .NET 10](https://dotnet.microsoft.com/download).

Mzip pide elevación (UAC) al iniciarse para poder comprimir y extraer en cualquier carpeta,
incluidas las protegidas del sistema. Como consecuencia, Windows bloquea el arrastrar y soltar
desde el Explorador hacia la ventana; usa los botones de la barra superior en su lugar.

## Descarga

Los ejecutables se publican en la sección [Releases](../../releases):

- `Mzip.exe`: versión portable.
- `MzipSetup.exe`: instalador completo.
- `SHA256SUMS.txt`: huellas para verificar las descargas.

## Compilar

Publicar la aplicación portable (genera `src/Mzip/bin/publish/portable/Mzip.exe`):

```bash
dotnet publish src/Mzip/Mzip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o src/Mzip/bin/publish/portable
```

Publicar el instalador, que incrusta el ejecutable anterior:

```bash
dotnet publish src/MzipSetupExe/MzipSetupExe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o out/setup
```

Si `Mzip.exe` no existe, la compilación del instalador en Release falla con un mensaje que indica
el comando que hay que ejecutar antes.

## Estructura

```
src/Mzip            Aplicación WPF (interfaz, motor de archivos, tema)
src/MzipSetupExe    Instalador WPF que incrusta Mzip.exe como recurso
.github/workflows   Compilación y publicación de releases al etiquetar v*
```

## Licencia

[MIT](LICENSE) © 2026 MSoft · [msoft.com.es](https://msoft.com.es)
