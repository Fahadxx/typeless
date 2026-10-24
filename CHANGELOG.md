# Changelog

## 0.11.0

- PARTS for 3D models: finds the separate objects in a file (even a merged STL) and shows them from the top. Click objects or drag a box to choose which to keep, then save one file (`<stem>_parts.<ext>`) or each object as its own file (`<stem>_part_1.<ext>`, ...).
- Whole objects only: nothing is cut or moved, and kept geometry is bit-identical to the source. Every output is read back and checked before it is saved.
- CLI `--model-parts`, `--keep-parts` and `--parts-preview`, plus the `TYPELESS_AUTOPARTS` and `TYPELESS_PARTS_SEPARATE` hooks.

## 0.10.0

- 3D models: right-click any `stl`, `3mf`, `obj`, `ply` or `glb` file and convert it to any of the others. Built in, no extra tools.
- Geometry is exact: no remeshing or decimation, and vertex coordinates keep their float32 bits (stl -> 3mf/obj/ply/glb -> stl is byte-identical). Physical size is kept: 3MF units are honored, and glb is written in meters with Y up, as its spec requires.
- Parts, part names and per-part colors carry over wherever the target can hold them. Bambu/Prusa project 3MFs (Production extension, components, plates) are flattened to their build layout.
- What a format cannot hold (textures, UVs, slicer settings, per-triangle colors) is dropped and logged.
- Safe: every output is read back and checked (exact triangle count, bounding box) before it is saved. Damaged files fail with a clear message and nothing is written.
- CLI `--model-info <file>`, plus a cube icon in the menu.

## 0.9.0

- Typeless is now a pure file converter. YouTube downloading moved to its own project, [typeless-youtube](https://github.com/Fahadxx/typeless-youtube).
- `--install` and `--uninstall` no longer touch the `typeless:` link handler. The right-click menu no longer lists `.url` shortcuts.

## 0.8.0

- RESIZE for videos: lower the resolution (tiers below the source's short side) and/or the frame rate (rates below the source, NTSC-aware), down only. Saves `<stem>_720p.<ext>`, `_30fps` or `_720p30` in the same container.
- Phone clips with a rotation flag are sized by what you see and come out upright. Audio is copied untouched, and cover art is skipped.
- Resize window with Original/tier pills, a live before -> after readout, a progress bar with an ETA, Esc to cancel, and the saved size change.
- CLI `--resize`, `--resize-info`, `--resize-preview`, and the `TYPELESS_AUTORESIZE` hook.
- Video radial order: RESIZE sits on page 1 and WEBM moves to the MORE page.

## 0.7.0

- AUDIO (video): saves the original audio track with no re-encode (m4a/mp3/opus/ogg/flac/wav/... by codec).
- ROTATE (image/video): window with rotate left/right and flip horizontal/vertical; lossless for mp4/mov (display matrix), re-encode for other video containers.
- CLEAN (image/video): removes location, camera, dates and other metadata; pixels and streams untouched, photo orientation and color profile kept; video data tracks (GPS) dropped.
- Radial MORE page: menus with more than 9 options show 8 + MORE; page 2 has the rest + BACK (0/Tab/Backspace keys).
- CLI: --audio, --clean, --rotate, --rotate-preview; hook TYPELESS_AUTOROTATE.

## 0.6.0

- CLI: --uninstall-verbs removes only the classic context menu verbs; build.ps1 runs it once the sparse package registers.

## 0.5.0

- PDF toolkit: Merge (2+ PDFs and/or images into one PDF, natural name order), Split (one PDF per page), Pages window (pick, reorder, rotate with thumbnails), Compress window (Light/Balanced/Strong with real size saved shown before saving; built-in, only re-encodes safe images, fonts and text untouched).
- PDF to PowerPoint (picture slides).
- Slides to pdf/pptx/odp through PowerPoint when installed (LibreOffice fallback); never closes a PowerPoint the user has open.
- CLI: --merge, --split, --pages, --compress, --pages-preview, --compress-preview; hooks TYPELESS_AUTOPAGES, TYPELESS_AUTOCOMPRESS, TYPELESS_NO_POWERPOINT.
- PDFsharp pinned to 6.2.4 (last MIT release).

## 0.4.2

- Trim handles: smooth and frame-accurate (no live keyframe snapping), Shift-drag fine mode, Left/Right steps one frame, time bubble while dragging, centisecond readout.
- Accurate cut is the default; Lossless pill keeps stream copy and shows where the keyframe cut really starts.
- Drag preview scrubs the open MediaElement instead of spawning ffmpeg per frame.

## 0.4.1

- Trim window playback: play/pause button and Space, click anywhere on the timeline to seek, playback stops at the end handle and rewinds to the start; ffmpeg proxy for formats Windows cannot play.
- Test hook TYPELESS_AUTOPLAY=1 or a,b.

## 0.4.0

- Lossless video format switch: stream copy first (bit-identical video/audio), fallbacks drop unsupported streams, then visually lossless re-encode keeping resolution, fps and frames.
- Quick trim: TRIM slice for single video/audio files opens a timeline window (filmstrip or waveform, keyframe ticks, frame preview, Exact toggle); output `<stem>_trim.<ext>`.
- Radial supports 9 slices (keys 1-9). Shared Theme.cs for crop and trim windows.
- CLI: `--trim`, `--probe`, `--trim-preview`; test hook TYPELESS_AUTOTRIM.

## 0.3.0

- Quick crop: CROP slice for single images opens a crop window (drag, handles, ratio pills, Enter to save); output `<stem>_crop.<ext>` in the same format.
- CLI: `--crop <file> x y w h`, `--crop-preview`; test hook TYPELESS_AUTOCROP.

## 0.2.0

- Windows 11 top-level context menu: native IExplorerCommand DLL (shell/) plus signed sparse MSIX package.
- build.ps1 builds the DLL, packages, signs with self-signed CN=Typeless Dev, registers; classic verbs remain as fallback.
- `--list-extensions` flag; uninstall.ps1 removes package (`-RemoveCert` removes the cert).

## 0.1.1

- Focus safety net: re-activate after grace period, close if focus never arrives; 30 s idle auto-close.
- Test hook: TYPELESS_AUTOPICK=<n> picks slice n after load (end-to-end testing).

## 0.1.0

- Initial skeleton: WPF code-only project, VERSION-driven versioning, build and uninstall scripts.
- CLI: `--version`, `--targets`, `--convert`, `--install`, `--uninstall`.
- Single format table with alias normalization and backend availability filtering.
- Output naming that never overwrites (`<stem>_<ext>.<ext>`, `(2)` on collision, `_1`, `_2` for pages).
- Backend discovery for ffmpeg, LibreOffice, and Word.
- Images: png, jpg, webp, bmp, gif, tiff, ico (multi-size), pdf; EXIF orientation; ffmpeg decode fallback for heic/avif.
- PDF: page render to png/jpg (Windows.Data.Pdf), pdf to docx/txt via Word COM with LibreOffice fallback.
- Word dialog watchdog (auto-dismisses PDF reflow notice and "serious error" prompt) plus 90 s Word timeout.
- Documents, spreadsheets, slides via Word / LibreOffice (private profile).
- Audio and video via ffmpeg (gif with palette).
- Radial donut menu at cursor: hover, spinner, saved and failed states, keys 1-8, Esc, click outside.
- Explorer context menu (HKCU, 42 extensions) and multi-select batch via mutex + named pipe.
- Dev flag `--preview <file> <out.png> [state]` renders the menu to PNG.
