using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PicNest.Models;
using PicNest.ViewModels;

namespace PicNest.Controls;

public enum ThumbnailDisplaySize
{
    Large,
    Medium,
    Small
}

public enum PhotoContextAction
{
    ShowInFileExplorer,
    ToggleFavorite,
    Copy,
    Move,
    Delete
}

public sealed class PhotoContextActionEventArgs(PhotoContextAction action, PhotoRecord photo, IReadOnlyList<PhotoRecord> selectedPhotos)
    : EventArgs
{
    public PhotoContextAction Action { get; } = action;
    public PhotoRecord Photo { get; } = photo;
    public IReadOnlyList<PhotoRecord> SelectedPhotos { get; } = selectedPhotos;
}

/// <summary>
/// Draws only the portion of the photo timeline that is in (or just beside) the viewport.
/// It owns a small LRU bitmap cache, so a large library does not create one image control or
/// decoded bitmap for every catalog record.
/// </summary>
public sealed class VirtualizingPhotoTimeline : Control
{
    private const double SidePadding = 28;
    private const double TopPadding = 22;
    private const double HeaderHeight = 58;
    private const double GroupGap = 16;
    private const int ThumbnailCacheLimit = 160;

    private static readonly IBrush LightTitleBrush = new SolidColorBrush(Color.Parse("#26333A"));
    private static readonly IBrush LightMutedBrush = new SolidColorBrush(Color.Parse("#738287"));
    private static readonly IBrush LightTileBrush = new SolidColorBrush(Color.Parse("#DEE6E7"));
    private static readonly IBrush DarkTitleBrush = new SolidColorBrush(Color.Parse("#E5EEF0"));
    private static readonly IBrush DarkMutedBrush = new SolidColorBrush(Color.Parse("#A6B5BA"));
    private static readonly IBrush DarkTileBrush = new SolidColorBrush(Color.Parse("#2A3A40"));
    private static readonly IBrush VideoBadgeBrush = new SolidColorBrush(Color.Parse("#167397"));
    private static readonly IBrush SelectionFillBrush = new SolidColorBrush(Color.Parse("#33167397"));
    private static readonly Pen SelectionPen = new(new SolidColorBrush(Color.Parse("#167397")), 3);
    private static readonly Typeface TextTypeface = new("Segoe UI");

    public static readonly StyledProperty<IList<DateGroup>?> ItemsSourceProperty =
        AvaloniaProperty.Register<VirtualizingPhotoTimeline, IList<DateGroup>?>(nameof(ItemsSource));
    public static readonly StyledProperty<ThumbnailDisplaySize> ThumbnailSizeProperty =
        AvaloniaProperty.Register<VirtualizingPhotoTimeline, ThumbnailDisplaySize>(nameof(ThumbnailSize), ThumbnailDisplaySize.Large);

    private readonly List<GroupLayout> _layouts = [];
    private readonly Dictionary<string, CacheItem> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailLru = [];
    private readonly HashSet<string> _unreadableThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContextMenu _contextMenu;
    private readonly MenuItem _favoriteMenuItem;
    private INotifyCollectionChanged? _collectionNotifier;
    private ScrollViewer? _scrollViewer;
    private string? _selectionAnchorPath;
    private PhotoRecord? _contextPhoto;
    private double _contentHeight;
    private double _layoutWidth = -1;
    private bool _darkMode;

    private IBrush TitleBrush => _darkMode ? DarkTitleBrush : LightTitleBrush;
    private IBrush MutedBrush => _darkMode ? DarkMutedBrush : LightMutedBrush;
    private IBrush TileBrush => _darkMode ? DarkTileBrush : LightTileBrush;

    public IList<DateGroup>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Changes the physical grid metrics without rebuilding the photo catalog.</summary>
    public ThumbnailDisplaySize ThumbnailSize
    {
        get => GetValue(ThumbnailSizeProperty);
        set => SetValue(ThumbnailSizeProperty, value);
    }

    /// <summary>Raised when the user double-clicks a visible photo thumbnail.</summary>
    public event EventHandler<PhotoRecord>? PhotoDoubleClicked;

    /// <summary>Raised whenever the Explorer-style multi-selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the visible star on a tile is clicked.</summary>
    public event EventHandler<PhotoRecord>? FavoriteToggleRequested;

    /// <summary>Raised when the slideshow action in a date header is clicked.</summary>
    public event EventHandler<DateGroup>? SlideshowRequested;

    /// <summary>Raised for an action chosen from a thumbnail's right-click menu.</summary>
    public event EventHandler<PhotoContextActionEventArgs>? ContextActionRequested;

    public int SelectedCount => _selectedPaths.Count;

    /// <summary>Refreshes the custom-drawn labels and placeholders when the app theme changes.</summary>
    public void SetDarkMode(bool isDarkMode)
    {
        if (_darkMode == isDarkMode) return;
        _darkMode = isDarkMode;
        InvalidateVisual();
    }

    public IReadOnlyList<PhotoRecord> GetSelectedPhotos() =>
        (ItemsSource ?? []).SelectMany(group => group.Photos).Select(tile => tile.Photo)
            .Where(photo => _selectedPaths.Contains(photo.Path)).ToArray();

    public void ClearSelection()
    {
        if (_selectedPaths.Count == 0) return;
        _selectedPaths.Clear();
        _selectionAnchorPath = null;
        NotifySelectionChanged();
    }

    public void SelectPaths(IEnumerable<string> paths)
    {
        var available = (ItemsSource ?? []).SelectMany(group => group.Photos).Select(tile => tile.Photo.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedPaths.Clear();
        foreach (var path in paths)
            if (available.Contains(path)) _selectedPaths.Add(path);
        _selectionAnchorPath = _selectedPaths.FirstOrDefault();
        NotifySelectionChanged();
    }

    public VirtualizingPhotoTimeline()
    {
        ClipToBounds = true;
        _favoriteMenuItem = new MenuItem();
        _favoriteMenuItem.Click += (_, _) => RaiseContextAction(PhotoContextAction.ToggleFavorite);
        _contextMenu = new ContextMenu
        {
            Placement = PlacementMode.Pointer,
            ItemsSource = new object[]
            {
                CreateContextMenuItem("Show in File Explorer", PhotoContextAction.ShowInFileExplorer),
                new Separator(),
                _favoriteMenuItem,
                new Separator(),
                CreateContextMenuItem("Copy to…", PhotoContextAction.Copy),
                CreateContextMenuItem("Move to…", PhotoContextAction.Move),
                CreateContextMenuItem("Delete to Recycle Bin", PhotoContextAction.Delete)
            }
        };
        PointerPressed += TimelinePointerPressed;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailSizeProperty)
        {
            _layoutWidth = -1;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        if (change.Property != ItemsSourceProperty) return;

        if (_collectionNotifier is not null) _collectionNotifier.CollectionChanged -= ItemsChanged;
        _collectionNotifier = change.GetNewValue<IList<DateGroup>?>() as INotifyCollectionChanged;
        if (_collectionNotifier is not null) _collectionNotifier.CollectionChanged += ItemsChanged;
        _unreadableThumbnails.Clear();
        RemoveUnavailableSelections();
        RebuildLayout(Bounds.Width);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer is not null) _scrollViewer.ScrollChanged += ScrollViewerScrolled;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scrollViewer is not null) _scrollViewer.ScrollChanged -= ScrollViewerScrolled;
        _scrollViewer = null;
        ClearThumbnailCache();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? Bounds.Width : availableSize.Width;
        RebuildLayout(width);
        return new Size(Math.Max(0, width), _contentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        RebuildLayout(finalSize.Width);
        return new Size(finalSize.Width, _contentHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_layouts.Count == 0) return;

        var viewportTop = _scrollViewer?.Offset.Y ?? 0;
        var viewportHeight = _scrollViewer?.Viewport.Height ?? Bounds.Height;
        var visibleTop = Math.Max(0, viewportTop - MaximumThumbnailHeight);
        var visibleBottom = viewportTop + viewportHeight + MaximumThumbnailHeight;

        foreach (var layout in _layouts)
        {
            if (layout.Bottom < visibleTop || layout.Top > visibleBottom) continue;
            RenderGroupHeader(context, layout);
            RenderVisibleTiles(context, layout, visibleTop, visibleBottom);
        }
    }

    private void RenderGroupHeader(DrawingContext context, GroupLayout layout)
    {
        var title = new FormattedText(layout.Group.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TextTypeface, 19, TitleBrush);
        context.DrawText(title, new Point(SidePadding, layout.Top));

        var count = new FormattedText($"{layout.Group.Photos.Count:n0} items", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TextTypeface, 12, MutedBrush);
        context.DrawText(count, new Point(SidePadding, layout.Top + 25));

        var slideshow = new FormattedText("▶  Slideshow", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TextTypeface, 12, MutedBrush);
        var slideshowBounds = SlideshowBounds(layout, slideshow);
        context.DrawText(slideshow, new Point(slideshowBounds.X + 6, layout.Top + 15));
    }

    private void RenderVisibleTiles(DrawingContext context, GroupLayout layout, double visibleTop, double visibleBottom)
    {
        var photos = layout.Group.Photos;
        foreach (var row in layout.Rows)
        {
            if (row.Bottom < visibleTop || row.Top > visibleBottom) continue;
            for (var column = 0; column < row.PhotoCount; column++)
            {
                var index = row.FirstPhotoIndex + column;
                var x = SidePadding + column * (TileWidth + ColumnGap);
                var imageBounds = new Rect(x, row.Top, TileWidth, GetThumbnailHeight(photos[index].Photo));
                RenderTile(context, photos[index], imageBounds);
            }
        }
    }

    private void RenderTile(DrawingContext context, PhotoTile tile, Rect imageBounds)
    {
        context.DrawRectangle(TileBrush, null, imageBounds, 3, 3);
        var bitmap = GetThumbnail(tile.Photo.ThumbnailPath);
        if (bitmap is not null)
        {
            using (context.PushClip(imageBounds))
                context.DrawImage(bitmap, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), imageBounds);
        }

        if (tile.IsVideo)
        {
            var badge = new Rect(imageBounds.X + 7, imageBounds.Y + 7, 50, 22);
            context.DrawRectangle(VideoBadgeBrush, null, badge, 3, 3);
            var video = new FormattedText("PLAY", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                TextTypeface, 10, Brushes.White);
            context.DrawText(video, new Point(badge.X + 9, badge.Y + 5));
        }

        var favoriteBounds = FavoriteBounds(imageBounds);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#EFFFFFFF")), null, favoriteBounds, 10, 10);
        var favorite = new FormattedText(tile.Photo.IsFavorite ? "★" : "☆", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, TextTypeface, 17,
            new SolidColorBrush(Color.Parse(tile.Photo.IsFavorite ? "#D99414" : "#567178")));
        context.DrawText(favorite, new Point(favoriteBounds.X + (favoriteBounds.Width - favorite.Width) / 2,
            favoriteBounds.Y + (favoriteBounds.Height - favorite.Height) / 2 - 1));

        if (_selectedPaths.Contains(tile.Photo.Path))
        {
            var selectionBounds = new Rect(imageBounds.X - 2, imageBounds.Y - 2,
                imageBounds.Width + 4, imageBounds.Height + 4);
            context.DrawRectangle(SelectionFillBrush, null, imageBounds, 3, 3);
            context.DrawRectangle(null, SelectionPen, selectionBounds, 4, 4);
        }

        var filename = Shorten(Path.GetFileName(tile.Photo.Path), MaximumLabelLength);
        var label = new FormattedText(filename, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TextTypeface, LabelFontSize, TitleBrush);
        context.DrawText(label, new Point(imageBounds.X, imageBounds.Bottom + LabelOffset));
    }

    private Bitmap? GetThumbnail(string thumbnailPath)
    {
        if (_thumbnailCache.TryGetValue(thumbnailPath, out var cached))
        {
            Touch(cached);
            return cached.Bitmap;
        }

        if (_unreadableThumbnails.Contains(thumbnailPath) || !File.Exists(thumbnailPath)) return null;
        try
        {
            var bitmap = new Bitmap(thumbnailPath);
            var node = _thumbnailLru.AddFirst(thumbnailPath);
            _thumbnailCache.Add(thumbnailPath, new CacheItem(bitmap, node));
            TrimThumbnailCache();
            return bitmap;
        }
        catch
        {
            _unreadableThumbnails.Add(thumbnailPath);
            return null;
        }
    }

    private void RebuildLayout(double width)
    {
        if (width <= 0 || (Math.Abs(width - _layoutWidth) < 0.1 && _layouts.Count > 0)) return;
        _layoutWidth = width;
        _layouts.Clear();
        var columns = Math.Max(1, (int)Math.Floor((width - (SidePadding * 2) + ColumnGap) / (TileWidth + ColumnGap)));
        var top = TopPadding;
        foreach (var group in ItemsSource ?? [])
        {
            var rows = new List<RowLayout>();
            var rowTop = top + HeaderHeight;
            for (var firstPhotoIndex = 0; firstPhotoIndex < group.Photos.Count; firstPhotoIndex += columns)
            {
                var photoCount = Math.Min(columns, group.Photos.Count - firstPhotoIndex);
                var thumbnailHeight = 0d;
                for (var column = 0; column < photoCount; column++)
                    thumbnailHeight = Math.Max(thumbnailHeight, GetThumbnailHeight(group.Photos[firstPhotoIndex + column].Photo));

                var rowHeight = thumbnailHeight + LabelOffset + LabelFontSize + RowGap;
                rows.Add(new RowLayout(firstPhotoIndex, photoCount, rowTop, rowHeight));
                rowTop += rowHeight;
            }

            var height = HeaderHeight + rows.Sum(row => row.Height) + GroupGap;
            _layouts.Add(new GroupLayout(group, top, columns, rows, height));
            top += height;
        }
        _contentHeight = Math.Max(TopPadding, top);
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _unreadableThumbnails.Clear();
        RemoveUnavailableSelections();
        _layoutWidth = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ScrollViewerScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private void TimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(this);
        if (!pointer.Properties.IsLeftButtonPressed && !pointer.Properties.IsRightButtonPressed) return;
        if (pointer.Properties.IsLeftButtonPressed && TryGetSlideshowGroupAt(e.GetPosition(this), out var slideshowGroup))
        {
            SlideshowRequested?.Invoke(this, slideshowGroup);
            e.Handled = true;
            return;
        }
        if (!TryGetPhotoAt(e.GetPosition(this), out var photo, out var imageBounds))
        {
            if (pointer.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None) ClearSelection();
            return;
        }

        if (pointer.Properties.IsRightButtonPressed)
        {
            if (!_selectedPaths.Contains(photo.Path)) SelectOnly(photo);
            _contextPhoto = photo;
            _favoriteMenuItem.Header = GetSelectedPhotos().All(item => item.IsFavorite)
                ? "Remove from Favorites"
                : "Add to Favorites";
            _contextMenu.Open(this);
            e.Handled = true;
            return;
        }

        if (FavoriteBounds(imageBounds).Contains(e.GetPosition(this)))
        {
            FavoriteToggleRequested?.Invoke(this, photo);
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            SelectOnly(photo);
            PhotoDoubleClicked?.Invoke(this, photo);
            e.Handled = true;
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (shift && _selectionAnchorPath is not null)
            SelectRange(_selectionAnchorPath, photo.Path, addToExisting: control);
        else if (control)
            ToggleSelection(photo);
        else
            SelectOnly(photo);
        e.Handled = true;
    }

    private void SelectOnly(PhotoRecord photo)
    {
        if (_selectedPaths.Count == 1 && _selectedPaths.Contains(photo.Path)) return;
        _selectedPaths.Clear();
        _selectedPaths.Add(photo.Path);
        _selectionAnchorPath = photo.Path;
        NotifySelectionChanged();
    }

    private void ToggleSelection(PhotoRecord photo)
    {
        if (!_selectedPaths.Add(photo.Path)) _selectedPaths.Remove(photo.Path);
        _selectionAnchorPath = photo.Path;
        NotifySelectionChanged();
    }

    private void SelectRange(string startPath, string endPath, bool addToExisting)
    {
        var photos = (ItemsSource ?? []).SelectMany(group => group.Photos).Select(tile => tile.Photo).ToArray();
        var start = Array.FindIndex(photos, photo => string.Equals(photo.Path, startPath, StringComparison.OrdinalIgnoreCase));
        var end = Array.FindIndex(photos, photo => string.Equals(photo.Path, endPath, StringComparison.OrdinalIgnoreCase));
        if (start < 0 || end < 0) return;
        if (!addToExisting) _selectedPaths.Clear();
        foreach (var photo in photos[Math.Min(start, end)..(Math.Max(start, end) + 1)]) _selectedPaths.Add(photo.Path);
        NotifySelectionChanged();
    }

    private void RemoveUnavailableSelections()
    {
        var available = (ItemsSource ?? []).SelectMany(group => group.Photos).Select(tile => tile.Photo.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_selectedPaths.RemoveWhere(path => !available.Contains(path)) == 0) return;
        if (_selectionAnchorPath is not null && !available.Contains(_selectionAnchorPath)) _selectionAnchorPath = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySelectionChanged()
    {
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private MenuItem CreateContextMenuItem(string label, PhotoContextAction action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => RaiseContextAction(action);
        return item;
    }

    private void RaiseContextAction(PhotoContextAction action)
    {
        if (_contextPhoto is null) return;
        ContextActionRequested?.Invoke(this, new PhotoContextActionEventArgs(action, _contextPhoto, GetSelectedPhotos()));
    }

    private bool TryGetPhotoAt(Point point, out PhotoRecord photo, out Rect imageBounds)
    {
        foreach (var layout in _layouts)
        {
            foreach (var row in layout.Rows)
            {
                if (point.Y < row.Top || point.Y >= row.Bottom) continue;
                var column = (int)Math.Floor((point.X - SidePadding) / (TileWidth + ColumnGap));
                if (column < 0 || column >= row.PhotoCount) continue;

                var index = row.FirstPhotoIndex + column;
                var tileLeft = SidePadding + column * (TileWidth + ColumnGap);
                var tileHeight = GetThumbnailHeight(layout.Group.Photos[index].Photo);
                var tileBounds = new Rect(tileLeft, row.Top, TileWidth, tileHeight);
                if (!tileBounds.Contains(point)) continue;

                photo = layout.Group.Photos[index].Photo;
                imageBounds = tileBounds;
                return true;
            }
        }

        photo = default!;
        imageBounds = default;
        return false;
    }

    private bool TryGetSlideshowGroupAt(Point point, out DateGroup group)
    {
        foreach (var layout in _layouts)
        {
            var slideshow = new FormattedText("▶  Slideshow", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                TextTypeface, 12, MutedBrush);
            if (!SlideshowBounds(layout, slideshow).Contains(point)) continue;
            group = layout.Group;
            return true;
        }

        group = default!;
        return false;
    }

    private Rect SlideshowBounds(GroupLayout layout, FormattedText slideshow) =>
        new(Math.Max(SidePadding, _layoutWidth - SidePadding - slideshow.Width) - 6, layout.Top + 8,
            slideshow.Width + 12, 28);

    private static Rect FavoriteBounds(Rect imageBounds) =>
        new(imageBounds.Right - 28, imageBounds.Top + 5, 23, 23);

    private void Touch(CacheItem item)
    {
        _thumbnailLru.Remove(item.Node);
        _thumbnailLru.AddFirst(item.Node);
    }

    private void TrimThumbnailCache()
    {
        while (_thumbnailCache.Count > ThumbnailCacheLimit)
        {
            var node = _thumbnailLru.Last!;
            _thumbnailLru.RemoveLast();
            if (_thumbnailCache.Remove(node.Value, out var item)) item.Bitmap.Dispose();
        }
    }

    private void ClearThumbnailCache()
    {
        foreach (var item in _thumbnailCache.Values) item.Bitmap.Dispose();
        _thumbnailCache.Clear();
        _thumbnailLru.Clear();
        _unreadableThumbnails.Clear();
    }

    private double TileWidth => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 74,
        ThumbnailDisplaySize.Medium => 104,
        _ => 134
    };

    private double GetThumbnailHeight(PhotoRecord photo) =>
        photo.Width > 0 && photo.Height > 0
            ? TileWidth * DisplayHeight(photo) / DisplayWidth(photo)
            : DefaultThumbnailHeight;

    private static double DisplayWidth(PhotoRecord photo) =>
        photo.RotationDegrees is 90 or 270 ? photo.Height : photo.Width;

    private static double DisplayHeight(PhotoRecord photo) =>
        photo.RotationDegrees is 90 or 270 ? photo.Width : photo.Height;

    private double DefaultThumbnailHeight => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 80,
        ThumbnailDisplaySize.Medium => 112,
        _ => 145
    };

    private double MaximumThumbnailHeight => TileWidth * 4;

    private double ColumnGap => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 10,
        ThumbnailDisplaySize.Medium => 12,
        _ => 16
    };

    private double RowGap => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 15,
        ThumbnailDisplaySize.Medium => 16,
        _ => 19
    };

    private double LabelFontSize => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 9,
        ThumbnailDisplaySize.Medium => 10,
        _ => 11
    };

    private double LabelOffset => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 4,
        ThumbnailDisplaySize.Medium => 5,
        _ => 6
    };

    private int MaximumLabelLength => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 12,
        ThumbnailDisplaySize.Medium => 17,
        _ => 21
    };

    private static string Shorten(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 1)] + "…";

    private sealed record GroupLayout(DateGroup Group, double Top, int Columns, IReadOnlyList<RowLayout> Rows, double Height)
    {
        public double Bottom => Top + Height;
    }

    private sealed record RowLayout(int FirstPhotoIndex, int PhotoCount, double Top, double Height)
    {
        public double Bottom => Top + Height;
    }

    private sealed record CacheItem(Bitmap Bitmap, LinkedListNode<string> Node);
}
