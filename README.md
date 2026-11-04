# SDS_Convert

SDS_Convert is a lightweight native Windows desktop app for local-first image, text, and PDF tools. It is built with .NET WPF and does not embed Chromium or Node.js.

## Project layout

- `App.xaml` — WPF application entry point and shared cream/light-brown theme.
- `Views/` — main converter, on-demand file preview, and searchable in-app help.
- `Models/` — conversion queue, history, and help data models.
- `Services/` — local conversion, image transforms, and PDF operations.
- `Assets/Icons/` — scalable SVG marks and the Windows application icon.
- `Assets/Licenses/Lucide-LICENSE.txt` — attribution and license for the Lucide icon artwork.
- `LICENSE` — MIT license for the SDS_Convert project source.
- `SDS_Convert.csproj` — Windows desktop project and dependencies.

## Run and build

- Requires the .NET 10 Windows Desktop Runtime.
- Building from source requires the .NET 10 SDK.
- Open `SDS_Convert.csproj` in Visual Studio with the .NET desktop workload, or run:

```powershell
dotnet run --project .\SDS_Convert.csproj
dotnet publish .\SDS_Convert.csproj -c Release -r win-x64 --self-contained false
```

The interface uses a warm cream palette with tan and brown accents and a document-conversion mark based on Lucide's `file-output` and `repeat-2` SVGs. Original SVG assets and the required Lucide license notice are included under `Assets/`.

The app runs conversions sequentially by default. It does not scan folders in the background. Dropped folders are scanned only when requested, long lists are virtualized, and output collisions receive a numbered filename instead of overwriting existing files.

The window opens sized to fit the available screen, can be moved by dragging its title bar, and resized from its edges. Use the **Full screen** button or **F11** to toggle full screen; press **Esc** to exit.

## Local conversion support

Support is based on actual local engines and installed Windows codecs. The app does not promise every format can be converted losslessly to every other format.

| Input | Local support in this version | Notes |
|---|---|---|
| PNG, JPEG, GIF, BMP, TIFF | Convert between these formats using Windows Imaging Component | Source and resized output frames are limited to 50 megapixels; GIF frame handling depends on the system codec; metadata is not guaranteed after transformation |
| TXT, Markdown, HTML, RTF | Basic text/markup conversion among these formats | Complex HTML/CSS and document layout are simplified |
| PDF | Merge multiple PDFs or split a PDF into one file per page | Up to 500 MB per merge/split operation; PDF-to-image/office export is not included in this local build |
| DOC, DOCX, ODT, PPTX, XLSX | Convert through LibreOffice when installed or placed in the portable folder below | The app never uploads these files |
| WebP, HEIC, SVG | Not included in the bundled local codec set | Requires an appropriate local Windows codec/integration; no online provider is configured |

Image width/height resize preserves aspect ratio, JPEG quality controls lossy compression, and rotation/flip applies to supported raster inputs. Source and resized output frames are limited to 50 megapixels to constrain memory use. Text/markup inputs are limited to 64 MB. Files are read from their original locations and outputs go to the selected destination. A per-file folder override is available. Double-click a queue row to load an on-demand preview; previews are not preloaded.

This version does not bundle an online conversion service. Office conversion requires LibreOffice to be installed or its portable folder to be provided separately. SDS_Convert checks its folder and up to five parent folders for `LibreOfficePortable\App\libreoffice\program\soffice.com`, and runs conversions headlessly with a temporary profile. The portable suite is excluded from the source repository by `.gitignore` and is not copied into the app's publish output; provide it separately alongside the app when moving it. WebP/HEIC/SVG conversions are not currently implemented by the bundled codecs.

## Privacy

All implemented conversions run locally. Online fallback is intentionally unavailable until an online provider is deliberately selected and configured. No file is sent to a conversion service by this app.

Recent conversion paths are stored locally under `%LOCALAPPDATA%\SDS_Convert\recent.json`.

## License

The SDS_Convert source authored for this project is released under the MIT License; see [`LICENSE`](./LICENSE). Third-party dependencies and the Lucide logo artwork retain their own licenses; the Lucide license and attribution are under `Assets/Licenses/`.

## Help

Use the in-app searchable Help tab or press **F1**. Shortcuts: **Ctrl+O** add files, **Ctrl+Shift+O** choose a folder, **Ctrl+Enter** convert, **Ctrl+Shift+R** apply the batch naming pattern, **Delete** remove selected queue rows, **F11** toggle full screen, and **Esc** exit full screen.
