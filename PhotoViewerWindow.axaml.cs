using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PicNest.Models;
using PicNest.Services;
using SkiaSharp;

namespace PicNest;

/// <summary>Displays full-resolution originals and supports fit, zoom, and in-context navigation.</summary>
public partial class PhotoViewerWindow : Window
{
    private readonly List<PhotoRecord> _photos = [];
    private readonly Func<PhotoRecord, int, Task<PhotoRecord>>? _saveRotationAsync;
    private readonly bool _isSlideshow;
    private Bitmap? _original;
    private MemoryStream? _rotatedImageStream;
    private LibVLC? _libVlc;
    private MediaPlayer? _videoPlayer;
    private Media? _videoMedia;
    private PhotoRecord? _pendingVideo;
    private int _currentIndex;
    private double _zoom = 1;
    private bool _fitToWindow = true;
    private bool _isPanning;
    private bool _isUpdatingVideoPosition;
    private bool _isClosing;
    private bool _windowOpened;
    private Point _panStart;
    private Vector _panStartOffset;
    private Vector _pendingPanOffset;
    private bool _panUpdateScheduled;
    private readonly DispatcherTimer _videoControlsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _slideshowTimer = new();
    private DispatcherTimer? _transitionTimer;

    public PhotoViewerWindow()
    {
        InitializeComponent();
        KeyDown += ViewerKeyDown;
        Opened += (_, _) =>
        {
            _windowOpened = true;
            Dispatcher.UIThread.Post(StartPendingVideo, DispatcherPriority.Loaded);
            if (_isSlideshow)
            {
                SlideshowPauseButton.IsVisible = true;
                StartSlideshowTimer();
            }
        };
        Closing += (_, _) => CloseMediaViewer();
        _videoControlsTimer.Tick += (_, _) => UpdateVideoControls();
        _slideshowTimer.Tick += (_, _) => AdvanceSlideshow();
        ImageHost.PointerPressed += ImageHostPointerPressed;
        ImageHost.PointerMoved += ImageHostPointerMoved;
        ImageHost.PointerReleased += ImageHostPointerReleased;
        ImageHost.PointerWheelChanged += ImageHostPointerWheelChanged;
    }

    public PhotoViewerWindow(PhotoRecord photo) : this([photo], photo)
    {
    }

    public PhotoViewerWindow(IReadOnlyList<PhotoRecord> photos, PhotoRecord selected) : this(photos, selected, null)
    {
    }

    public PhotoViewerWindow(IReadOnlyList<PhotoRecord> photos, PhotoRecord selected,
        Func<PhotoRecord, int, Task<PhotoRecord>>? saveRotationAsync, bool slideshow = false) : this()
    {
        _saveRotationAsync = saveRotationAsync;
        _isSlideshow = slideshow;
        _photos.AddRange(photos.Count == 0 ? [selected] : photos);
        _currentIndex = FindSelectedIndex(selected);
        LoadCurrentPhoto();
    }

    private void LoadCurrentPhoto()
    {
        StopVisualTransition();
        PhotoImage.Opacity = 1;
        PhotoImage.RenderTransform = null;
        LoadCurrentPhotoCore();
    }

    private void LoadCurrentPhotoCore()
    {
        DisposeOriginal();
        DisposeVideoPlayer();
        _fitToWindow = true;
        _zoom = 1;
        ImageScroll.Offset = default;
        var photo = _photos[_currentIndex];

        Title = _isSlideshow
            ? $"PicNest slideshow - {Path.GetFileName(photo.Path)}"
            : $"PicNest - {Path.GetFileName(photo.Path)}";
        PhotoName.Text = Path.GetFileName(photo.Path);
        SourcePath.Text = photo.Path;
        LoadError.IsVisible = false;
        PhotoImage.Source = null;
        SetViewerMode(photo.MediaKind);

        if (photo.MediaKind == MediaKind.Video)
        {
            _pendingVideo = photo;
            StartPendingVideo();
        }
        else
        {
            _pendingVideo = null;
            LoadImage(photo);
            Dispatcher.UIThread.Post(UpdateImageLayout, DispatcherPriority.Loaded);
        }

        UpdateNavigation();
    }

    /// <summary>VideoView must be attached before VLC starts, or VLC creates its own top-level window.</summary>
    private void StartPendingVideo()
    {
        if (!_windowOpened || _isClosing || _pendingVideo is not { } video) return;
        _pendingVideo = null;
        if (CurrentPhoto?.MediaKind != MediaKind.Video ||
            !string.Equals(CurrentPhoto.Path, video.Path, StringComparison.OrdinalIgnoreCase)) return;
        LoadVideo(video);
    }

    private void LoadImage(PhotoRecord photo)
    {
        _ = DiagnosticLog.InformationAsync($"Image viewer started: {photo.Path}");
        try
        {
            _original = photo.RotationDegrees == 0
                ? new Bitmap(photo.Path)
                : LoadRotatedDisplayBitmap(photo.Path, photo.RotationDegrees);
            PhotoImage.Source = _original;
            var rotation = photo.RotationDegrees == 0 ? "" : $"  |  PicNest rotation {photo.RotationDegrees}°";
            PhotoDetails.Text = $"Original image  |  {_original.PixelSize.Width:n0} x {_original.PixelSize.Height:n0} pixels{rotation}";
        }
        catch (Exception error)
        {
            _ = DiagnosticLog.ErrorAsync($"Could not load image in viewer: {photo.Path}. {error}");
            LoadErrorText.Text = $"PicNest could not open the original photo.\n\n{error.Message}";
            LoadError.IsVisible = true;
            PhotoDetails.Text = "Original image unavailable";
        }
    }

    private void LoadVideo(PhotoRecord video)
    {
        try
        {
            _libVlc ??= new LibVLC("--no-video-title-show");
            _videoPlayer = new MediaPlayer(_libVlc);
            _videoMedia = new Media(_libVlc, new Uri(video.Path));
            VideoView.MediaPlayer = _videoPlayer;
            _videoPlayer.Media = _videoMedia;
            _videoPlayer.Play();
            _ = DiagnosticLog.InformationAsync($"Embedded video player started: {video.Path}");
            PlayPauseButton.Content = "Pause";
            VolumeSlider.Value = Math.Clamp(_videoPlayer.Volume, 0, 100);
            UpdateMuteButton();
            _videoControlsTimer.Start();
            UpdateVideoControls();
            PhotoDetails.Text = "Video  |  Embedded VLC playback";
        }
        catch (Exception error)
        {
            _ = DiagnosticLog.ErrorAsync($"Could not start embedded video player: {video.Path}. {error}");
            LoadErrorText.Text = $"PicNest could not open this video.\n\n{error.Message}";
            LoadError.IsVisible = true;
            PhotoDetails.Text = "Video unavailable";
        }
    }

    private void PreviousPhoto(object? sender, RoutedEventArgs e)
    {
        ShowPreviousPhoto();
    }

    private void ShowPreviousPhoto()
    {
        if (_currentIndex == 0) return;
        _currentIndex--;
        LoadCurrentPhoto();
    }

    private void NextPhoto(object? sender, RoutedEventArgs e)
    {
        ShowNextPhoto();
    }

    private void ShowNextPhoto()
    {
        if (_currentIndex >= _photos.Count - 1) return;
        _currentIndex++;
        LoadCurrentPhoto();
    }

    private void StartSlideshowTimer()
    {
        if (!_isSlideshow || _photos.Count <= 1) return;
        _slideshowTimer.Interval = TimeSpan.FromSeconds(PreferencesStore.Current.SlideshowSeconds);
        _slideshowTimer.Start();
        SlideshowPauseButton.Content = "Pause slideshow";
    }

    private void ToggleSlideshow(object? sender, RoutedEventArgs e)
    {
        if (!_isSlideshow) return;
        if (_slideshowTimer.IsEnabled)
        {
            _slideshowTimer.Stop();
            SlideshowPauseButton.Content = "Resume slideshow";
        }
        else
        {
            StartSlideshowTimer();
        }
    }

    private void AdvanceSlideshow()
    {
        if (_isClosing || _photos.Count <= 1 || _transitionTimer is not null) return;
        var nextIndex = (_currentIndex + 1) % _photos.Count;
        switch (ResolveSlideshowTransition())
        {
            case SlideshowTransition.Fade:
                FadeToSlide(nextIndex);
                break;
            case SlideshowTransition.Swipe:
                SwipeToSlide(nextIndex);
                break;
        }
    }

    private SlideshowTransition ResolveSlideshowTransition()
    {
        var configured = PreferencesStore.Current.SlideshowTransition;
        return configured == SlideshowTransition.Mix
            ? (Random.Shared.Next(2) == 0 ? SlideshowTransition.Fade : SlideshowTransition.Swipe)
            : configured;
    }

    private void FadeToSlide(int nextIndex)
    {
        if (PhotoImage.Source is null)
        {
            _currentIndex = nextIndex;
            LoadCurrentPhotoCore();
            return;
        }

        AnimatePhotoVisual(160, progress => PhotoImage.Opacity = 1 - progress, () =>
        {
            _currentIndex = nextIndex;
            LoadCurrentPhotoCore();
            PhotoImage.Opacity = 0;
            AnimatePhotoVisual(230, progress => PhotoImage.Opacity = progress, null);
        });
    }

    private void SwipeToSlide(int nextIndex)
    {
        _currentIndex = nextIndex;
        var translate = new TranslateTransform(56, 0);
        PhotoImage.RenderTransform = translate;
        PhotoImage.Opacity = 0;
        LoadCurrentPhotoCore();
        AnimatePhotoVisual(250, progress =>
        {
            PhotoImage.Opacity = progress;
            translate.X = 56 * (1 - progress);
        }, () => PhotoImage.RenderTransform = null);
    }

    private void AnimatePhotoVisual(int durationMilliseconds, Action<double> update, Action? completed)
    {
        StopVisualTransition();
        var started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _transitionTimer = timer;
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((Environment.TickCount64 - started) / (double)durationMilliseconds, 0, 1);
            update(progress);
            if (progress < 1) return;
            timer.Stop();
            if (ReferenceEquals(_transitionTimer, timer)) _transitionTimer = null;
            completed?.Invoke();
        };
        timer.Start();
    }

    private void StopVisualTransition()
    {
        _transitionTimer?.Stop();
        _transitionTimer = null;
    }

    private void ZoomIn(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale() * 1.25);

    private void ZoomOut(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale() / 1.25);

    private async void RotateLeft(object? sender, RoutedEventArgs e) => await RotateCurrentImageAsync(-90);

    private async void RotateRight(object? sender, RoutedEventArgs e) => await RotateCurrentImageAsync(90);

    private async Task RotateCurrentImageAsync(int change)
    {
        if (CurrentPhoto is not { MediaKind: MediaKind.Image } photo || _saveRotationAsync is null) return;

        var originalIndex = _currentIndex;
        var rotation = NormalizeRotation(photo.RotationDegrees + change);
        RotateLeftButton.IsEnabled = false;
        RotateRightButton.IsEnabled = false;
        try
        {
            var updated = await _saveRotationAsync(photo, rotation);
            if (_currentIndex != originalIndex || !string.Equals(CurrentPhoto?.Path, photo.Path, StringComparison.OrdinalIgnoreCase)) return;
            _photos[originalIndex] = updated;
            LoadCurrentPhoto();
        }
        catch (Exception error)
        {
            _ = DiagnosticLog.ErrorAsync($"Could not rotate image in viewer: {photo.Path}. {error}");
            PhotoDetails.Text = $"Could not rotate image: {error.Message}";
        }
        finally
        {
            var canRotate = CurrentPhoto?.MediaKind == MediaKind.Image && _saveRotationAsync is not null;
            RotateLeftButton.IsEnabled = canRotate;
            RotateRightButton.IsEnabled = canRotate;
        }
    }

    private void ResetView(object? sender, RoutedEventArgs e)
    {
        _fitToWindow = true;
        ImageScroll.Offset = default;
        UpdateImageLayout();
    }

    private void CloseViewer(object? sender, RoutedEventArgs e) => Close();

    private void TogglePlayback(object? sender, RoutedEventArgs e)
    {
        if (_videoPlayer is null) return;
        if (_videoPlayer.IsPlaying)
        {
            _videoPlayer.Pause();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            _videoPlayer.Play();
            PlayPauseButton.Content = "Pause";
        }
    }

    private void StopVideo(object? sender, RoutedEventArgs e)
    {
        if (_videoPlayer is null) return;
        try { _videoPlayer.Stop(); }
        catch { /* Disposal will make a stopped player safe. */ }
        PlayPauseButton.Content = "Play";
        UpdateVideoControls();
    }

    private void VideoPositionChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingVideoPosition || _videoPlayer is null) return;
        try { _videoPlayer.Time = (long)e.NewValue; }
        catch { /* The player can be released while the window is closing. */ }
    }

    private void VolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_videoPlayer is null) return;
        try
        {
            _videoPlayer.Volume = (int)Math.Round(e.NewValue);
            if (e.NewValue > 0 && _videoPlayer.Mute) _videoPlayer.Mute = false;
            UpdateMuteButton();
        }
        catch { /* The player can be released while the window is closing. */ }
    }

    private void ToggleMute(object? sender, RoutedEventArgs e)
    {
        if (_videoPlayer is null) return;
        try
        {
            _videoPlayer.Mute = !_videoPlayer.Mute;
            UpdateMuteButton();
        }
        catch { /* The player can be released while the window is closing. */ }
    }

    private void ViewerViewportChanged(object? sender, SizeChangedEventArgs e) => UpdateImageLayout();

    private void ViewerKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                ShowPreviousPhoto();
                break;
            case Key.Right:
                ShowNextPhoto();
                break;
            case Key.Up:
                SetZoom(CurrentScale() * 1.25);
                break;
            case Key.Down:
                SetZoom(CurrentScale() / 1.25);
                break;
            case Key.Space when _videoPlayer is not null:
                TogglePlayback(this, new RoutedEventArgs());
                break;
            case Key.Space when _isSlideshow:
                ToggleSlideshow(this, new RoutedEventArgs());
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ImageHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanPan() || !e.GetCurrentPoint(ImageHost).Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _panStart = e.GetPosition(ImageHost);
        _panStartOffset = ImageScroll.Offset;
        e.Pointer.Capture(ImageHost);
        e.Handled = true;
    }

    private void ImageHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var difference = e.GetPosition(ImageHost) - _panStart;
        var maximumX = Math.Max(0, ImageHost.Width - ImageScroll.Bounds.Width);
        var maximumY = Math.Max(0, ImageHost.Height - ImageScroll.Bounds.Height);
        QueuePan(new Vector(
            Math.Clamp(_panStartOffset.X - difference.X, 0, maximumX),
            Math.Clamp(_panStartOffset.Y - difference.Y, 0, maximumY)));
        e.Handled = true;
    }

    private void ImageHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        ApplyPendingPan();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ImageHostPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_original is null || e.Delta.Y == 0) return;
        SetZoom(CurrentScale() * (e.Delta.Y > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    private void SetZoom(double value)
    {
        if (_original is null) return;
        _fitToWindow = false;
        _zoom = Math.Clamp(value, 0.1, 8);
        UpdateImageLayout();
    }

    private void UpdateImageLayout()
    {
        if (_original is null) return;
        var available = ImageScroll.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0) return;

        var displaySize = DisplayImageSize();
        var scale = _fitToWindow ? FitScale(available.Width, available.Height) : _zoom;
        var imageWidth = _original.Size.Width * scale;
        var imageHeight = _original.Size.Height * scale;
        ImageHost.Width = Math.Max(available.Width, displaySize.Width * scale);
        ImageHost.Height = Math.Max(available.Height, displaySize.Height * scale);
        PhotoImage.Width = imageWidth;
        PhotoImage.Height = imageHeight;
        ZoomText.Text = _fitToWindow ? $"Fit ({scale:P0})" : $"{scale:P0}";
    }

    private double CurrentScale() => _original is null
        ? 1
        : _fitToWindow ? FitScale(ImageScroll.Bounds.Width, ImageScroll.Bounds.Height) : _zoom;

    private double FitScale(double availableWidth, double availableHeight)
    {
        if (_original is null || availableWidth <= 0 || availableHeight <= 0) return 1;
        var displaySize = DisplayImageSize();
        return Math.Min(1, Math.Min(availableWidth / displaySize.Width, availableHeight / displaySize.Height));
    }

    private Size DisplayImageSize()
    {
        if (_original is null) return default;
        return _original.Size;
    }

    private bool CanPan() => _original is not null &&
        (ImageHost.Width > ImageScroll.Bounds.Width || ImageHost.Height > ImageScroll.Bounds.Height);

    /// <summary>Keeps high-frequency pointer events from forcing a layout/scroll update each time.</summary>
    private void QueuePan(Vector offset)
    {
        _pendingPanOffset = offset;
        if (_panUpdateScheduled) return;
        _panUpdateScheduled = true;
        Dispatcher.UIThread.Post(ApplyPendingPan, DispatcherPriority.Render);
    }

    private void ApplyPendingPan()
    {
        if (!_panUpdateScheduled) return;
        ImageScroll.Offset = _pendingPanOffset;
        _panUpdateScheduled = false;
    }

    private void UpdateNavigation()
    {
        PreviousButton.IsEnabled = _isSlideshow ? _photos.Count > 1 : _currentIndex > 0;
        NextButton.IsEnabled = _isSlideshow ? _photos.Count > 1 : _currentIndex < _photos.Count - 1;
        PositionText.Text = _photos.Count > 1 ? $"{_currentIndex + 1:n0} of {_photos.Count:n0}" : "1 photo";
    }

    private int FindSelectedIndex(PhotoRecord selected)
    {
        for (var index = 0; index < _photos.Count; index++)
            if (string.Equals(_photos[index].Path, selected.Path, StringComparison.OrdinalIgnoreCase)) return index;
        return 0;
    }

    private void DisposeOriginal()
    {
        _isPanning = false;
        _panUpdateScheduled = false;
        PhotoImage.Source = null;
        _original?.Dispose();
        _original = null;
        _rotatedImageStream?.Dispose();
        _rotatedImageStream = null;
    }

    /// <summary>
    /// Creates a full-resolution display bitmap in the selected orientation. This is only used
    /// by the viewer; it never replaces the original source file or the thumbnail cache.
    /// </summary>
    private Bitmap LoadRotatedDisplayBitmap(string path, int rotationDegrees)
    {
        using var source = SKBitmap.Decode(path) ?? throw new InvalidDataException($"PicNest could not decode {path}.");
        var sideways = rotationDegrees is 90 or 270;
        var width = sideways ? source.Height : source.Width;
        var height = sideways ? source.Width : source.Height;
        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidDataException($"PicNest could not rotate {path}.");

        var canvas = surface.Canvas;
        switch (rotationDegrees)
        {
            case 90:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case 180:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case 270:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;
        }
        canvas.DrawBitmap(source, 0, 0);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        _rotatedImageStream = new MemoryStream(encoded.ToArray());
        return new Bitmap(_rotatedImageStream);
    }

    private void SetViewerMode(MediaKind mediaKind)
    {
        var isVideo = mediaKind == MediaKind.Video;
        ImageScroll.IsVisible = !isVideo;
        VideoView.IsVisible = isVideo;
        VideoControls.IsVisible = isVideo;
        PlayPauseButton.IsVisible = isVideo;
        ZoomOutButton.IsEnabled = !isVideo;
        ZoomInButton.IsEnabled = !isVideo;
        ResetViewButton.IsEnabled = !isVideo;
        RotateLeftButton.IsEnabled = !isVideo && _saveRotationAsync is not null;
        RotateRightButton.IsEnabled = !isVideo && _saveRotationAsync is not null;
        if (isVideo) ZoomText.Text = "Video";
        else _videoControlsTimer.Stop();
    }

    private void DisposeVideoPlayer()
    {
        _videoControlsTimer.Stop();
        if (_videoPlayer is null) return;
        var path = CurrentPhoto?.Path ?? "unknown video";
        _ = DiagnosticLog.InformationAsync($"Stopping embedded video player: {path}");
        VideoView.MediaPlayer = null;
        try { _videoPlayer.Stop(); }
        catch { /* Player disposal still releases the native resources. */ }
        _videoPlayer.Dispose();
        _videoPlayer = null;
        _videoMedia?.Dispose();
        _videoMedia = null;
        _ = DiagnosticLog.InformationAsync($"Embedded video player stopped and disposed: {path}");
    }

    private PhotoRecord? CurrentPhoto => _photos.Count == 0 ? null : _photos[_currentIndex];

    private static int NormalizeRotation(int rotationDegrees)
    {
        var normalized = rotationDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private void UpdateVideoControls()
    {
        if (_videoPlayer is null) return;

        try
        {
            var length = Math.Max(0, _videoPlayer.Length);
            var time = Math.Clamp(_videoPlayer.Time, 0, length);
            _isUpdatingVideoPosition = true;
            VideoPositionSlider.Maximum = Math.Max(1, length);
            VideoPositionSlider.Value = time;
            _isUpdatingVideoPosition = false;
            VideoTimeText.Text = $"{FormatDuration(time)} / {FormatDuration(length)}";
            PlayPauseButton.Content = _videoPlayer.IsPlaying ? "Pause" : "Play";
            UpdateMuteButton();
        }
        catch
        {
            _isUpdatingVideoPosition = false;
            // The player can be released while the window is closing.
        }
    }

    private void UpdateMuteButton()
    {
        if (_videoPlayer is null) return;
        MuteButton.Content = _videoPlayer.Mute || _videoPlayer.Volume == 0 ? "Unmute" : "Mute";
    }

    private static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    private void CloseMediaViewer()
    {
        if (_isClosing) return;
        _isClosing = true;
        _slideshowTimer.Stop();
        StopVisualTransition();
        var mediaType = CurrentPhoto?.MediaKind == MediaKind.Video ? "video" : "image";
        var path = CurrentPhoto?.Path ?? "unknown media";
        _ = DiagnosticLog.InformationAsync($"Closing {mediaType} viewer: {path}");
        DisposeMediaResources();
        _ = DiagnosticLog.InformationAsync($"Closed {mediaType} viewer: {path}");
    }

    public void ReleaseMediaResources() => DisposeMediaResources();

    private void DisposeMediaResources()
    {
        _slideshowTimer.Stop();
        StopVisualTransition();
        DisposeOriginal();
        DisposeVideoPlayer();
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
