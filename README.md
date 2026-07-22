# PicNest — Picasa for 2026

PicNest is a deliberately local-first, folder-first photo library. The catalog is SQLite; the original images stay exactly where you put them. Its first usable flow is: pick an ordinary folder, index images, cache responsive thumbnails, and browse a day-by-day timeline.

## Current foundation

- Avalonia native desktop shell (Windows, macOS and Linux capable)
- SQLite schema for photos, tags and people
- SHA-256 source hashing and cache-at-once thumbnails
- File-date grouping (EXIF/XMP extraction is the next metadata milestone)
- Persistent folder roots, hierarchical folder tree, direct folder counts, and click-to-filter timeline
- Manual **Refresh folders** action that discovers new and empty subfolders, as well as rescanning photos
- File-system watching for added, changed, renamed, and deleted image files
- Date-grouped photo timeline and filename/caption search

The database and thumbnail cache live at `%LOCALAPPDATA%\PicNest`. Only derived thumbnail JPEGs and catalog metadata go there; no original is imported, renamed or altered.

## Run it

This computer currently has the .NET runtime but no SDK. Install the .NET 9 SDK, then run the following in this directory:

```powershell
dotnet restore
dotnet run
```

## Deliberately next

1. A full-screen viewer plus ratings, captions and tags.
2. EXIF/XMP date extraction.
3. Video frames and scrub previews via FFmpeg.
4. Local face grouping, semantic AI embeddings, and perceptual duplicate clusters—all optional, all on-device.

SQLite is retained as the source of truth; an embedding extension or small sidecar index can be added later without disrupting the library.
