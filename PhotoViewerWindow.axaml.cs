using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PicNest.Models;
using PicNest.Services;

namespace PicNest;

/// <summary>Displays full-resolution originals and supports fit, zoom, and in-context navigation.</summary>
public partial class PhotoViewerWindow : Window
{
    private readonly IReadOnlyList<PhotoRecord> _photos = [];
    private Bitmap? _original;
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

    public PhotoViewerWindow()
    {
        InitializeComponent();
        KeyDown += ViewerKeyDown;
        Opened += (_, _) =>
        {
            _windowOpened = true;
            Dispatcher.UIThread.Post(StartPendingVideo, DispatcherPriority.Loaded);
        };
        Closing += (_, _) => CloseMediaViewer();
        _videoControlsTimer.Tick += (_, _) => UpdateVideoControls();
        ImageHost.PointerPressed += ImageHostPointerPressed;
        ImageHost.PointerMoved += ImageHostPointerMoved;
        ImageHost.PointerReleased += ImageHostPointerReleased;
        ImageHost.PointerWheelChanged += ImageHostPointerWheelChanged;
    }

    public PhotoViewerWindow(PhotoRecord photo) : this([photo], photo)
    {
    }

    public PhotoViewerWindow(IReadOnlyList<PhotoRecord> photos, PhotoRecord selected) : this()
    {
        _photos = photos.Count == 0 ? [selected] : photos.ToArray();
        _currentIndex = FindSelectedIndex(selected);
        LoadCurrentPhoto();
    }

    private void LoadCurrentPhoto()
    {
        DisposeOriginal();
        DisposeVideoPlayer();
        _fitToWindow = true;
        _zoom = 1;
        ImageScroll.Offset = default;
        var photo = _photos[_currentIndex];

        Title = $"PicNest - {Path.GetFileName(photo.Path)}";
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
            _original = new Bitmap(photo.Path);
            PhotoImage.Source = _original;
            PhotoDetails.Text = $"Original image  |  {_original.PixelSize.Width:n0} x {_original.PixelSize.Height:n0} pixels";
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

    private void ZoomIn(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale() * 1.25);

    private void ZoomOut(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale() / 1.25);

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

        var scale = _fitToWindow ? FitScale(available.Width, available.Height) : _zoom;
        var imageWidth = _original.Size.Width * scale;
        var imageHeight = _original.Size.Height * scale;
        ImageHost.Width = Math.Max(available.Width, imageWidth);
        ImageHost.Height = Math.Max(available.Height, imageHeight);
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
        return Math.Min(1, Math.Min(availableWidth / _original.Size.Width, availableHeight / _original.Size.Height));
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
        PreviousButton.IsEnabled = _currentIndex > 0;
        NextButton.IsEnabled = _currentIndex < _photos.Count - 1;
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
        var mediaType = CurrentPhoto?.MediaKind == MediaKind.Video ? "video" : "image";
        var path = CurrentPhoto?.Path ?? "unknown media";
        _ = DiagnosticLog.InformationAsync($"Closing {mediaType} viewer: {path}");
        DisposeMediaResources();
        _ = DiagnosticLog.InformationAsync($"Closed {mediaType} viewer: {path}");
    }

    public void ReleaseMediaResources() => DisposeMediaResources();

    private void DisposeMediaResources()
    {
        DisposeOriginal();
        DisposeVideoPlayer();
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
