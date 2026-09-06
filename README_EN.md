# QingSnap

[<kbd>简体中文</kbd>](README.md)　[<kbd>English</kbd>](README_EN.md)　[<kbd>繁體中文</kbd>](README_ZH_TW.md)　[<kbd>日本語</kbd>](README_JA.md)　[<kbd>한국어</kbd>](README_KO.md)

> Capture precisely, stitch beyond the screen, and keep references pinned. Install offline OCR only when you need it.

QingSnap is a lightweight screenshot utility for Windows 10 and 11. It combines smart capture, pixel-precise selection, editable annotations, scrolling screenshots, desktop pins, offline recognition, and full-history search in one workflow.

![QingSnap v1.1.1 seven-step first-run guide](docs/images/qingsnap-first-run-tutorial.png)

[![Release](https://img.shields.io/github/v/release/za5132-web/QingSnap?label=Release&color=67d9ea)](https://github.com/za5132-web/QingSnap/releases/latest)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-1674d1)
![.NET](https://img.shields.io/badge/.NET-8-6f42c1)
![Architecture](https://img.shields.io/badge/Architecture-x64-34495e)

## Download

- [Download QingSnap v1.1.1](https://github.com/za5132-web/QingSnap/releases/download/v1.1.1/QingSnap-v1.1.1.zip)
- [v1.1.1 release notes](docs/RELEASE_NOTES_v1.1.1.md)
- [Complete feature list (Chinese)](docs/QINGSNAP_COMPLETE_FEATURE_LIST.md)

Extract the ZIP and run `QingSnap.exe`. The portable package requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

> Releases are self-signed. Windows may report an unknown publisher or an untrusted certificate chain. Download only from this repository and verify the SHA-256 file included with each release.

## Learn QingSnap in one minute

The seven-step animated guide opens on first launch and can be replayed from Settings.

| Workflow | What it shows |
| --- | --- |
| 1. Start capture | Press `F1` to select a window or drag any region |
| 2. Refine selection | Adjust X/Y/W/H, ratio, fixed size, and nudge by 1 px |
| 3. Annotate and edit | Add arrows, shapes, and text, then use multi-select and Undo/Redo |
| 4. Capture a long page | Stitch automatically or switch to manual frame capture |
| 5. Pin to desktop | Press `F3` to pin recent images and `M` to stash them at an edge |
| 6. Extract information | Select image text like a document or interact with QR hotspots |
| 7. Search and reuse | Find captures by text, tag, or source, then pin or recapture them |

## Highlights

### Smart, pixel-precise capture

- Detect windows and standard controls under the pointer, or draw a free region.
- Refine all edges and corners, enter physical X/Y/W/H values, and inspect pixels with the magnifier.
- Lock `1:1`, `4:3`, `3:2`, `16:9`, `9:16`, the current ratio, or an exact fixed size.
- Hold `Shift` for ratio constraints and `Alt` for center-based scaling.
- Nudge with `W/A/S/D`; press `R` repeatedly to cycle through the five latest regions.
- Supports Per-Monitor V2, mixed-DPI monitors, and negative virtual-screen coordinates.

### Annotations that remain editable

- Pen, line, single/double arrows, rectangle, ellipse, text, numbered marker, mosaic, highlight, and blur.
- Full Undo/Redo for drawing, deletion, movement, resizing, endpoints, content, styles, layers, paste, and clear-all.
- Press `F2` for selection; Shift-click or marquee-select multiple objects for batch operations.
- Re-edit text and numbered markers, adjust line/arrow endpoints, and change parameters with the wheel.

### Automatic and manual scrolling capture

- Automatic scrolling, stable-frame sampling, overlap matching, bottom detection, and retry.
- Detects fixed headers and bottom bars so they appear only once in the result.
- Manual frame-by-frame fallback with the ability to undo the last captured frame.
- Long-image reader with wheel navigation, progress, jump-to-top, and overview.

### Desktop pins that stay out of the way

- Press `F3` repeatedly to cycle through five recent captures and external clipboard images.
- Captures reopen at their original position; external images appear near the pointer.
- Drag, smoothly zoom, view at `1:1`, fit to screen, copy, OCR, or scan QR codes.
- Press `M` to stash a pin as an edge thumbnail; hover to preview and click to restore it in place.
- Throw a pin toward the left or right screen edge to stash it with an animation.

### Optional offline OCR and built-in QR recognition

- The base package has no OCR runtime; capture, annotations, pins, history, and QR remain available.
- Install the OCR runtime in one click and choose PP-OCRv6 Tiny or Small when recognition is first needed.
- Select text directly inside normal and long-image pins, then copy it with `Ctrl+C`.
- QR recognition is fully offline and independent of OCR; QR Code, Data Matrix, Aztec, and PDF417 are supported.
- Results appear as hotspots over the original code. URLs open only after an explicit user click.

### Complete, searchable history

- Images remain ordinary PNG/JPG/BMP files; SQLite stores metadata and indexes only.
- Search the full history by file name, dimensions, OCR text, tags, source process, or window title.
- 80-item incremental pages, prefetch, WPF virtualization, 280px on-demand thumbnails, and a 150-item LRU.
- List/grid layouts, multi-select, batch favorite/tag/path copy, and recycle-bin deletion.
- Re-pin a saved image or recapture its original physical screen region.
- Rebuild metadata by scanning the image directory if the database is damaged.

## What is new in v1.1.1

- Reworked onboarding into a seven-page, one-minute core workflow.
- Added self-contained animations for capture, precision, annotation, scrolling capture, pins, OCR/QR, and history reuse.
- Unified keycaps, icon layers, corners, and action hints; fixed clipping around the stashed pin, QR hotspot, and `Ctrl+C` prompt.
- Supports the Windows reduce-motion setting with understandable static key states.
- Hardened animation teardown and first-run state. A 50-window stress run with 20 seven-page round trips per window showed no linear resource growth.

## Common shortcuts

| Action | Shortcut |
| --- | --- |
| Region capture | `F1` |
| Pin / cycle recent images | `F3` |
| Repeat latest region | `Shift+F1` |
| Cycle five recent regions in capture | `R` |
| Nudge capture crosshair | `W` `A` `S` `D` |
| Enter selection/edit mode | `F2` |
| Undo / Redo | `Ctrl+Z` / `Ctrl+Y` |
| Confirm / Cancel | `Enter` / `Esc` |
| Stash / restore a pin | `M` |

Automatic/manual scrolling capture, OCR latest capture, History, and pause/resume hotkeys can be configured in Settings by pressing the desired key combination directly.

## Data and privacy

- History defaults to `%LOCALAPPDATA%\QingSnap\History`.
- OCR runtimes and models are stored under `%LOCALAPPDATA%\QingSnap\Ocr`.
- Captures, OCR images, recognized text, and QR contents are never uploaded to third parties.
- Network access is used only for user-initiated OCR downloads, update checks, and release downloads.
- `portable.flag` keeps configuration, the metadata database, and related data beside the portable app.

## Build from source

```powershell
dotnet restore QingSnap.sln
dotnet build QingSnap.sln -c Release
dotnet test tests\QingSnap.Tests\QingSnap.Tests.csproj -c Release
dotnet run --project src\QingSnap.App\QingSnap.App.csproj
```

Main technologies: C#, .NET 8, WPF, native Windows capture/clipboard/hotkey/DPI APIs, Microsoft.Data.Sqlite, ZXing.Net, and optional RapidOCR.Net / PaddleOCR.

## Documentation and feedback

- [Changelog](CHANGELOG.md)
- [OCR module](docs/OCR_MODULE.md)
- [History metadata](docs/HISTORY_METADATA.md)
- [First-run guide final report](docs/QINGSNAP_V1.1_FIRST_RUN_TUTORIAL_REPORT.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [GitHub Issues](https://github.com/za5132-web/QingSnap/issues)

## Project status and license

QingSnap is actively developed with assistance from Codex.

No open-source license is currently granted. The source is available for viewing only; permission is required for use, modification, or redistribution. Third-party components retain their respective licenses.

