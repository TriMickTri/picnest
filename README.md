# PicNest — Picasa for 2026

PicNest is a deliberately local-first, folder-first photo library. The catalog is SQLite; the original images stay exactly where you put them. Its first usable flow is: pick an ordinary folder, index images, cache responsive thumbnails, and browse a day-by-day timeline.

## Current foundation

- Avalonia native desktop shell (Windows, macOS and Linux capable)
- SQLite schema for photos, tags and people
- SHA-256 source hashing and cache-at-once thumbnails
- Viewport-virtualized photo timeline with a bounded decoded-thumbnail cache (ready for very large libraries)
- Small, medium, and large thumbnail layouts
- SQLite scan writes grouped into 250-photo transactions
- Double-click any thumbnail to open its original image in a viewer with zoom, reset, and next/previous navigation
- Local video scanning for MP4, MOV, MKV, AVI, M4V, WMV, WebM, MTS/M2TS, and 3GP; embedded LibVLC playback with play/pause controls
- File-date grouping (EXIF/XMP extraction is the next metadata milestone)
- Persistent folder roots, hierarchical folder tree, direct folder counts, and click-to-filter timeline
- **Manage folders** dialog for listing imports, adding another folder, or removing a folder from the PicNest catalog without affecting its original files
- Manual **Refresh folders** action that discovers new and empty subfolders, as well as rescanning photos
- File-system watching for added, changed, renamed, and deleted image files
- Date-grouped photo timeline and filename/caption search

The database and thumbnail cache live at `%LOCALAPPDATA%\PicNest`. Only derived thumbnail JPEGs and catalog metadata go there; no original is imported, renamed or altered.

Diagnostics are written locally to `%LOCALAPPDATA%\PicNest\diag\picnest.log`. Each entry records an ISO timestamp and level, the source function and line number, then the diagnostic message. Initial events cover directory scans and new folders/photos reported by the watcher.

## Run it

With the .NET 9 SDK installed, run the following in this directory:

```powershell
dotnet restore
dotnet run
```

## Create a Windows installer

PicNest has a per-user Windows x64 installer. It is self-contained, so the recipient does not need the .NET SDK or runtime. Install [Inno Setup](https://jrsoftware.org/isinfo.php), then run:

```powershell
.\installer\Build-Installer.ps1 -Version 0.1.0
```

The result is `artifacts\installer\PicNest-Setup-0.1.0-win-x64.exe`. The installer adds Start Menu and optional desktop shortcuts, and its uninstall leaves `%LOCALAPPDATA%\PicNest` intact so a user's local catalog and preferences are not deleted.

## Deliberately next

1. A full-screen viewer plus ratings, captions and tags.
2. EXIF/XMP date extraction.
3. Real video-frame thumbnails, duration metadata, and scrub previews.
4. Local face grouping, semantic AI embeddings, and perceptual duplicate clusters—all optional, all on-device.

SQLite is retained as the source of truth; an embedding extension or small sidecar index can be added later without disrupting the library.
