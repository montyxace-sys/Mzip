# Mzip

App para comprimir y extraer ficheros, documentos, etc. Rápido, eficaz y open source.

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
- Arranque sin UAC: solo se piden permisos de administrador si el destino los necesita,
  mediante un proceso auxiliar que ejecuta esa operación concreta.

## Requisitos

- Windows 10 u 11 de 64 bits.
- Para compilar: [SDK de .NET 10](https://dotnet.microsoft.com/download).

Mzip arranca con permisos normales, sin aviso de UAC, y mantiene el arrastrar y soltar desde el
Explorador. Si eliges un destino protegido (por ejemplo `Program Files` o la raíz de una unidad),
la aplicación pregunta y reintenta esa operación con permisos de administrador sin cerrarse.

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

[MIT](LICENSE) © 2026 montyxace-sys · [msoft.com.es](https://msoft.com.es)
