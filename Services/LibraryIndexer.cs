using System.Security.Cryptography;
using PicNest.Models;
using SkiaSharp;

namespace PicNest.Services;

/// <summary>Incremental indexer: derived thumbnails are cached once and regenerated only when source content changes.</summary>
public sealed class LibraryIndexer(LibraryDatabase database)
{
    private const int DatabaseBatchSize = 250;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    public async Task<int> IndexFolderAsync(string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            await DiagnosticLog.WarningAsync($"Scan skipped because directory does not exist: {folder}");
            return 0;
        }

        var count = 0;
        var batch = new List<PhotoRecord>(DatabaseBatchSize);
        foreach (var directory in EnumerateDirectories(folder))
        {
            await DiagnosticLog.InformationAsync($"Scanning directory: {directory}");
            string[] paths;
            try { paths = Directory.GetFiles(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await DiagnosticLog.WarningAsync($"Could not enumerate files in {directory}: {error.Message}");
                continue;
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSupportedImage(path)) continue;
                progress?.Report(Path.GetFileName(path));
                try
                {
                    batch.Add(await CreatePhotoRecordAsync(path, cancellationToken));
                    count++;
                    if (batch.Count == DatabaseBatchSize)
                    {
                        await database.UpsertBatchAsync(batch, cancellationToken);
                        batch.Clear();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    await DiagnosticLog.WarningAsync($"Could not index {path}: {error.Message}");
                }
            }
        }
        await database.UpsertBatchAsync(batch, cancellationToken);
        await DiagnosticLog.InformationAsync($"Completed scan of {folder}. Indexed {count:n0} photos.");
        return count;
    }

    public static bool IsSupportedImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public async Task IndexImageAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !IsSupportedImage(path)) return;
        await database.UpsertAsync(await CreatePhotoRecordAsync(path, cancellationToken));
    }

    private static async Task<PhotoRecord> CreatePhotoRecordAsync(string path, CancellationToken cancellationToken)
    {
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
        return new PhotoRecord(0, path, Path.GetDirectoryName(path)!, date, hash, source.Width, source.Height, thumbPath, false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children) pending.Push(child);
        }
    }
}
