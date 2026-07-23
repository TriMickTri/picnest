using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PicNest.Models;

namespace PicNest;

/// <summary>Displays full-resolution originals and supports fit, zoom, and in-context navigation.</summary>
public partial class PhotoViewerWindow : Window
{
    private readonly IReadOnlyList<PhotoRecord> _photos = [];
    private Bitmap? _original;
    private int _currentIndex;
    private double _zoom = 1;
    private bool _fitToWindow = true;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;
    private Vector _pendingPanOffset;
    private bool _panUpdateScheduled;

    public PhotoViewerWindow()
    {
        InitializeComponent();
        KeyDown += ViewerKeyDown;
        Closed += (_, _) => DisposeOriginal();
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
        _fitToWindow = true;
        _zoom = 1;
        ImageScroll.Offset = default;
        var photo = _photos[_currentIndex];

        Title = $"PicNest - {Path.GetFileName(photo.Path)}";
        PhotoName.Text = Path.GetFileName(photo.Path);
        SourcePath.Text = photo.Path;
        LoadError.IsVisible = false;
        PhotoImage.Source = null;

        try
        {
            _original = new Bitmap(photo.Path);
            PhotoImage.Source = _original;
            PhotoDetails.Text = $"Original image  |  {_original.PixelSize.Width:n0} x {_original.PixelSize.Height:n0} pixels";
        }
        catch (Exception error)
        {
            LoadErrorText.Text = $"PicNest could not open the original photo.\n\n{error.Message}";
            LoadError.IsVisible = true;
            PhotoDetails.Text = "Original image unavailable";
        }

        UpdateNavigation();
        Dispatcher.UIThread.Post(UpdateImageLayout, DispatcherPriority.Loaded);
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
}
