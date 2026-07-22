using System.Security.Cryptography;
using PicNest.Models;
using SkiaSharp;

namespace PicNest.Services;

/// <summary>Incremental indexer: derived thumbnails are cached once and regenerated only when source content changes.</summary>
public sealed class LibraryIndexer(LibraryDatabase database)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    public async Task<int> IndexFolderAsync(string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSupportedImage(path)) continue;
            progress?.Report(Path.GetFileName(path));
            try { await IndexImageAsync(path, cancellationToken); count++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return count;
    }

    public static bool IsSupportedImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public async Task IndexImageAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !IsSupportedImage(path)) return;
        var hash = await HashFileAsync(path, cancellationToken);
        var thumbPath = Path.Combine(LibraryPaths.Thumbnails, $"{hash}.jpg");
        using var source = SKBitmap.Decode(path) ?? throw new InvalidDataException($"PicNest could not decode {path}.");
        if (!File.Exists(thumbPath))
        {
            var scale = Math.Min(1d, 360d / Math.Max(source.Width, source.Height));
            var size = new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using var thumbnail = source.Resize(size, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) ?? throw new InvalidDataException($"PicNest could not resize {path}.");
            using var encoded = SKImage.FromBitmap(thumbnail).Encode(SKEncodedImageFormat.Jpeg, 85);
            await using var output = File.Create(thumbPath);
            encoded.SaveTo(output);
        }
        // This fallback keeps indexing fast and reliable; EXIF/XMP extraction will replace it in the metadata milestone.
        var date = File.GetLastWriteTime(path);
        await database.UpsertAsync(new PhotoRecord(0, path, Path.GetDirectoryName(path)!, date, hash, source.Width, source.Height, thumbPath, false));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
