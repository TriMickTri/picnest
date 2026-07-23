using System.Security.Cryptography;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using PicNest.Models;
using SkiaSharp;

namespace PicNest.Services;

/// <summary>Incremental indexer: derived thumbnails are cached once and regenerated only when source content changes.</summary>
public sealed class LibraryIndexer(LibraryDatabase database)
{
    private const int DatabaseBatchSize = 250;
    private const int VideoThumbnailWidth = 360;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mts", ".m2ts", ".3gp" };
    private static readonly Lazy<LibVLC> ThumbnailVlc = new(() => new LibVLC("--no-audio", "--no-video-title-show"));

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
                if (!IsSupportedMedia(path)) continue;
                progress?.Report(Path.GetFileName(path));
                try
                {
                    batch.Add(await CreateMediaRecordAsync(path, cancellationToken));
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
        await DiagnosticLog.InformationAsync($"Completed scan of {folder}. Indexed {count:n0} media item(s).");
        return count;
    }

    public static bool IsSupportedImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupportedVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsSupportedMedia(string path) => IsSupportedImage(path) || IsSupportedVideo(path);

    public async Task IndexImageAsync(string path, CancellationToken cancellationToken = default)
    {
        await IndexMediaAsync(path, cancellationToken);
    }

    public async Task IndexMediaAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !IsSupportedMedia(path)) return;
        await database.UpsertAsync(await CreateMediaRecordAsync(path, cancellationToken));
    }

    private static async Task<PhotoRecord> CreateMediaRecordAsync(string path, CancellationToken cancellationToken) =>
        IsSupportedVideo(path)
            ? await CreateVideoRecordAsync(path, cancellationToken)
            : await CreateImageRecordAsync(path, cancellationToken);

    private static async Task<PhotoRecord> CreateImageRecordAsync(string path, CancellationToken cancellationToken)
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
        return new PhotoRecord(0, path, Path.GetDirectoryName(path)!, date, hash, source.Width, source.Height, thumbPath, false, MediaKind.Image);
    }

    private static async Task<PhotoRecord> CreateVideoRecordAsync(string path, CancellationToken cancellationToken)
    {
        var hash = await HashFileAsync(path, cancellationToken);
        // Version the cache filename so the original generic video placeholders are naturally replaced on refresh.
        var thumbPath = Path.Combine(LibraryPaths.Thumbnails, $"{hash}-video-v3.jpg");
        if (!File.Exists(thumbPath))
        {
            try
            {
                await CreateVideoThumbnailAsync(path, thumbPath, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await DiagnosticLog.WarningAsync($"Could not generate a video thumbnail for {path}: {error.Message}");
                await CreateVideoPlaceholderThumbnailAsync(thumbPath);
            }
        }
        var date = File.GetLastWriteTime(path);
        return new PhotoRecord(0, path, Path.GetDirectoryName(path)!, date, hash, 1920, 1080, thumbPath, false, MediaKind.Video);
    }

    /// <summary>Captures an early frame with an off-screen LibVLC buffer, never a visible VLC video window.</summary>
    private static async Task CreateVideoThumbnailAsync(string sourcePath, string thumbnailPath, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(thumbnailPath)!,
            $"{Path.GetFileNameWithoutExtension(thumbnailPath)}-{Guid.NewGuid():N}.tmp.jpg");

        try
        {
            using var frame = new VideoFrameBuffer(VideoThumbnailWidth, 203);
            using var media = new Media(ThumbnailVlc.Value, new Uri(sourcePath));
            using var player = new MediaPlayer(ThumbnailVlc.Value) { Media = media };
            player.SetVideoFormat("RV32", VideoThumbnailWidth, 203, VideoThumbnailWidth * 4);
            player.SetVideoCallbacks(frame.LockCallback, frame.UnlockCallback, frame.DisplayCallback);
            player.Play();

            // Move beyond title cards and black first frames when the duration becomes available.
            await Task.Delay(300, cancellationToken);
            if (player.Length > 5_000) player.Time = Math.Min(1_500, player.Length / 10);
            await frame.WaitForFrameAsync(cancellationToken);
            player.Stop();
            frame.SaveJpeg(temporaryPath);

            File.Move(temporaryPath, thumbnailPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { /* A failed temporary thumbnail is safe to leave for Windows to clean up. */ }
        }
    }

    private static async Task CreateVideoPlaceholderThumbnailAsync(string thumbnailPath)
    {
        var info = new SKImageInfo(360, 203);
        using var surface = SKSurface.Create(info) ?? throw new InvalidDataException("PicNest could not create a video thumbnail surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(31, 48, 57));
        using var panelPaint = new SKPaint { Color = new SKColor(22, 115, 151) };
        canvas.DrawRoundRect(new SKRect(132, 53, 228, 149), 12, 12, panelPaint);
        using var playPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var play = new SKPath();
        play.MoveTo(168, 77);
        play.LineTo(168, 125);
        play.LineTo(207, 101);
        play.Close();
        canvas.DrawPath(play, playPaint);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        await using var output = File.Create(thumbnailPath);
        encoded.SaveTo(output);
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

    /// <summary>Owns the native pixel buffer passed to LibVLC for a one-frame thumbnail capture.</summary>
    private sealed class VideoFrameBuffer : IDisposable
    {
        private readonly byte[] _decodeBuffer;
        private readonly byte[] _capturedFrame;
        private readonly GCHandle _decodeBufferHandle;
        private readonly TaskCompletionSource _frameReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _captureLock = new();
        private bool _hasFrame;

        public VideoFrameBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            _decodeBuffer = new byte[width * height * 4];
            _capturedFrame = new byte[_decodeBuffer.Length];
            _decodeBufferHandle = GCHandle.Alloc(_decodeBuffer, GCHandleType.Pinned);
            LockCallback = LockFrame;
            UnlockCallback = UnlockFrame;
            DisplayCallback = DisplayFrame;
        }

        public int Width { get; }
        public int Height { get; }
        public MediaPlayer.LibVLCVideoLockCb LockCallback { get; }
        public MediaPlayer.LibVLCVideoUnlockCb UnlockCallback { get; }
        public MediaPlayer.LibVLCVideoDisplayCb DisplayCallback { get; }

        public async Task WaitForFrameAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await _frameReady.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidDataException("VLC did not decode a video frame within five seconds.");
            }
        }

        public void SaveJpeg(string path)
        {
            lock (_captureLock)
            {
                if (!_hasFrame) throw new InvalidDataException("No decoded video frame is available.");
                using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                Marshal.Copy(_capturedFrame, 0, bitmap.GetPixels(), _capturedFrame.Length);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                using var output = File.Create(path);
                encoded.SaveTo(output);
            }
        }

        private IntPtr LockFrame(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _decodeBufferHandle.AddrOfPinnedObject());
            return IntPtr.Zero;
        }

        private static void UnlockFrame(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
        }

        private void DisplayFrame(IntPtr opaque, IntPtr picture)
        {
            lock (_captureLock)
            {
                if (_hasFrame) return;
                Buffer.BlockCopy(_decodeBuffer, 0, _capturedFrame, 0, _decodeBuffer.Length);
                _hasFrame = true;
            }
            _frameReady.TrySetResult();
        }

        public void Dispose()
        {
            if (_decodeBufferHandle.IsAllocated) _decodeBufferHandle.Free();
        }
    }
}
