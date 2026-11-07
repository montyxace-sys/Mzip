# Mzip

App para comprimir y extraer ficheros, documentos, etc. Rápido, eficaz y open source.

Compresor y extractor de archivos para Windows con interfaz oscura, escrito en WPF sobre .NET 10.

Mzip se distribuye de dos formas: un único `Mzip.exe` portable que no necesita instalación, y
`MzipSetup.exe`, que instala la aplicación en `Program Files`, crea accesos directos y registra
la desinstalación en Windows.

## Características

- Comprime en **ZIP, 7Z, TAR, TAR.GZ y TAR.BZ2** por streaming, sin cargar los archivos en memoria.
  7Z usa LZMA2 sólido y es el formato con mayor tasa de compresión.
- Extrae **ZIP, TAR, TAR.GZ/TGZ, TAR.BZ2/TBZ2, TAR.XZ/TXZ, 7Z, RAR, GZ, BZ2 y XZ**,
  incluidos los 7Z sólidos y cifrados.
- Menú contextual del Explorador: «Extraer aquí», «Extraer en una subcarpeta» y
  «Comprimir con Mzip» al pulsar con el botón derecho.
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

## Menú contextual

El instalador añade las entradas al menú contextual del usuario actual (`HKCU`, sin administrador):

- **Extraer aquí con Mzip**: extrae el comprimido en su propia carpeta.
- **Extraer en una subcarpeta**: crea una carpeta con el nombre del archivo y extrae dentro.
- **Abrir con Mzip**: abre el comprimido en la ventana de la aplicación.
- **Comprimir con Mzip**: abre la aplicación con el archivo o la carpeta ya añadidos.

En Windows 11 estas entradas aparecen en «Mostrar más opciones». Se pueden quitar desde la propia
aplicación (casilla «Añadir al menú contextual de Windows») o al desinstalar.

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
src/Mzip            Aplicación WPF (interfaz, motor de archivos, tema, motor 7-Zip)
src/MzipSetupExe    Instalador WPF que incrusta Mzip.exe como recurso
LICENSES            Licencias de componentes de terceros
.github/workflows   Compilación y publicación de releases al etiquetar v*
```

## Licencias de terceros

- [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT): lectura de formatos y
  escritura de TAR, TAR.GZ y TAR.BZ2.
- [7-Zip](https://www.7-zip.org/) (LGPL + restricción unRAR): se incluye `7zr.exe` como ejecutable
  independiente para crear archivos 7Z con compresión sólida. El texto completo está en
  [LICENSES/7-Zip-LICENSE.txt](LICENSES/7-Zip-LICENSE.txt); el código fuente está disponible en
  <https://www.7-zip.org/source.html>.

## Licencia

[MIT](LICENSE) © 2026 montyxace-sys · [msoft.com.es](https://msoft.com.es)
