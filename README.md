# Typeless

Right-click a file in Explorer, pick **Convert with Typeless**, and click a format on the radial menu. The result lands next to the original as `<name>_<ext>.<ext>` (for example `report.pdf` becomes `report_docx.docx`). Existing files are never overwritten.

Select several PDFs and images to get a **Merge** slice (one PDF, in file name order). A single PDF gets a **Pages** slice (a thumbnail grid to pick, reorder, and rotate pages, saved as `<name>_pages.pdf`) and, with more than one page, a **Split** slice (one PDF per page). The **Compress** slice shows what Light, Balanced, and Strong would save before you pick one, then saves `<name>_compressed.pdf`.

A video with sound gets an **Audio** slice: it saves the audio track as is, with no re-encode, as `<name>_audio.<ext>` (m4a, mp3, opus, ogg, flac, wav, ac3, eac3 or mka, by codec).

A single image or video gets a **Clean** slice: it removes location, camera, date and software info without touching the picture or sound, as `<name>_clean.<ext>`. A photo keeps its orientation and color profile.

A single image or video also gets a **Rotate** slice: a small window to rotate left or right and flip horizontally or vertically (keys `[` `]` or Left/Right, `H`, `V`), saved as `<name>_rotated.<ext>`. mp4/mov videos stay lossless; other videos are re-encoded.

A single video gets a **Resize** slice: a small window to lower the resolution (2160p down to 240p) and/or the frame rate (60 down to 15 fps), offering only values below the source. Enter re-encodes the video (audio is kept as is) with progress and time left (Esc cancels), saved as `<name>_720p.<ext>`, `<name>_30fps.<ext>` or `<name>_720p30.<ext>` in the same format. Portrait phone clips are sized as they play.

**3D models** (stl, 3mf, obj, ply, glb) convert to each other, built in, with no other software needed. Geometry is exact. Parts, names and colors are kept where the target format can hold them; textures, UVs and slicer settings are dropped. Every output is read back and checked (triangle count and bounding box) before it is saved. glb uses meters per its spec, so sizes stay physically correct. Bambu and Prusa project 3MFs are flattened to their build layout.

When a file has more than 9 options, the menu shows the 8 most used plus **More**, which flips to the rest (with **Back** to return). Keys: 1-9 pick on the current page, 0 or Tab switches pages, Backspace goes back, Esc closes.

## Requirements

- Windows 10 (19041) or Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the .NET 8 SDK to build)
- Optional backends, used when installed:
  - Microsoft Word: PDF to docx/txt and document conversions
  - LibreOffice: documents, spreadsheets, and slides
  - ffmpeg: audio, video, and webp

Formats whose backend is missing are hidden from the menu.

YouTube downloading lives in a separate project: https://github.com/Fahadxx/typeless-youtube

## Build and install

```powershell
./build.ps1               # publish to %LOCALAPPDATA%\Typeless\app and register the menu
./build.ps1 -Bump patch   # bump VERSION (patch|minor|major), add a CHANGELOG entry, then build
./build.ps1 -NoInstall    # publish only
./build.ps1 -NoPackage    # install with the classic menu only (no sparse package)
./uninstall.ps1           # remove the package, menu entries, and the app folder
./uninstall.ps1 -RemoveCert  # also remove the Typeless Dev certificate (admin prompt)
```

## CLI

```
Typeless <file> [more files]     Show the radial menu
Typeless --convert <file> <ext>  Convert headlessly, print output paths
Typeless --audio <file>          Save a video's audio track as is (no re-encode), print the output path
Typeless --clean <file>          Remove an image's or video's metadata (location, camera, dates), print the output path
Typeless --rotate <file> <deg> [h] [v]  Flip, then rotate an image or video clockwise (multiple of 90; mp4/mov stay lossless), print the output path
Typeless --resize <file> <height|orig> [fps|orig]  Lower a video's resolution (short side) and/or frame rate, print the path and size change
Typeless --resize-info <file>    Print a video's displayed size, fps, and the smaller heights and frame rates on offer
Typeless --model-info <file>     Print a 3D model's parts, triangles, vertices, bbox (mm), per-part color and what the reader dropped
Typeless --merge <file> <file> [...]  Merge PDFs and images (given order) into one PDF
Typeless --split <file>          Split a PDF into one PDF per page
Typeless --pages <file> <spec>   Pick, reorder and rotate pages, e.g. 3,1-2,5r90,6r180
Typeless --compress <file> [light|balanced|strong]  Shrink a PDF's images (default balanced), print the path and size saved
Typeless --targets <file>        List available target formats
Typeless --list-extensions       List the context menu extensions
Typeless --preview <file> <out.png> [idle|hover:N|busy|done|error|page2] [more files]  Render the menu to PNG (dev; page2 = MORE page past 9 items)
Typeless --pages-preview <file> <out.png> [idle|select|done|error]  Render the pages window to PNG (dev)
Typeless --compress-preview <file> <out.png> [idle|running|ready|done|error]  Render the compress window to PNG (dev)
Typeless --rotate-preview <file> <out.png> [idle|changed|done|error]  Render the rotate window to PNG (dev)
Typeless --resize-preview <file> <out.png> [idle|changed|running|done|error]  Render the resize window to PNG (dev)
Typeless --install               Register the Explorer context menu
Typeless --uninstall             Remove the Explorer context menu
Typeless --uninstall-verbs       Remove only the classic context menu verbs
Typeless --version               Print the version
```

Typeless is a GUI app, so redirect output to see it from a console, for example `Typeless.exe --version | Out-String`.

Dev hook: `TYPELESS_AUTOPICK=<n>` clicks radial item n (0-based, counting across both pages; an item on page 2 flips there first). `TYPELESS_AUTOPAGES=<spec>` (same spec as `--pages`) makes the pages window apply the spec and save once the thumbnails load. `TYPELESS_AUTOCOMPRESS=light|balanced|strong` makes the compress window select that level and save when it is ready. `TYPELESS_AUTOROTATE=<deg>[,h][,v]` makes the rotate window flip, then turn deg clockwise, and save (same result as `--rotate`). `TYPELESS_AUTORESIZE=<height|orig>[,<fps|orig>]` makes the resize window select those values and save (same result as `--resize`).

## Windows 11

`build.ps1` builds the native shell extension (`shell\build.cmd`), packs a sparse MSIX package, signs it with a self-signed certificate (`CN=Typeless Dev`, created in `CurrentUser\My` on first build), and registers it so **Convert with Typeless** appears in the top-level Windows 11 context menu. Requires the Windows 10/11 SDK (makeappx, signtool) and Visual Studio 2022 C++ tools.

Windows only installs the package if the certificate is trusted, so the first build shows a one-time admin (UAC) prompt that imports the public `TypelessDev.cer` into `LocalMachine\TrustedPeople`. If you decline, or packaging fails, the build keeps the classic entry instead: click **Show more options**, or Shift+right-click the file. Use `-NoPackage` to skip packaging entirely. When the package registers, the classic entry is removed to avoid duplicates.

`uninstall.ps1` removes the package. Add `-RemoveCert` to also delete the certificate from `CurrentUser\My` and (with an admin prompt) from `LocalMachine\TrustedPeople`.

Logs: `%LOCALAPPDATA%\Typeless\typeless.log`.
