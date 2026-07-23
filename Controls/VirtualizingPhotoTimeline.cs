using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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

    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#26333A"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#738287"));
    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#DEE6E7"));
    private static readonly IBrush VideoBadgeBrush = new SolidColorBrush(Color.Parse("#167397"));
    private static readonly Typeface TextTypeface = new("Segoe UI");

    public static readonly StyledProperty<IList<DateGroup>?> ItemsSourceProperty =
        AvaloniaProperty.Register<VirtualizingPhotoTimeline, IList<DateGroup>?>(nameof(ItemsSource));
    public static readonly StyledProperty<ThumbnailDisplaySize> ThumbnailSizeProperty =
        AvaloniaProperty.Register<VirtualizingPhotoTimeline, ThumbnailDisplaySize>(nameof(ThumbnailSize), ThumbnailDisplaySize.Large);

    private readonly List<GroupLayout> _layouts = [];
    private readonly Dictionary<string, CacheItem> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailLru = [];
    private readonly HashSet<string> _unreadableThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private INotifyCollectionChanged? _collectionNotifier;
    private ScrollViewer? _scrollViewer;
    private double _contentHeight;
    private double _layoutWidth = -1;

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

    public VirtualizingPhotoTimeline()
    {
        ClipToBounds = true;
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
        var visibleTop = Math.Max(0, viewportTop - RowHeight);
        var visibleBottom = viewportTop + viewportHeight + RowHeight;

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
        context.DrawText(slideshow, new Point(Math.Max(SidePadding, _layoutWidth - SidePadding - slideshow.Width), layout.Top + 15));
    }

    private void RenderVisibleTiles(DrawingContext context, GroupLayout layout, double visibleTop, double visibleBottom)
    {
        var photos = layout.Group.Photos;
        var firstRow = Math.Max(0, (int)Math.Floor((visibleTop - layout.FirstRowTop) / RowHeight));
        var lastRow = Math.Min(layout.RowCount - 1, (int)Math.Floor((visibleBottom - layout.FirstRowTop) / RowHeight));
        if (lastRow < firstRow) return;

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < layout.Columns; column++)
            {
                var index = row * layout.Columns + column;
                if (index >= photos.Count) return;
                var x = SidePadding + column * (TileWidth + ColumnGap);
                var y = layout.FirstRowTop + row * RowHeight;
                RenderTile(context, photos[index], x, y);
            }
        }
    }

    private void RenderTile(DrawingContext context, PhotoTile tile, double x, double y)
    {
        var imageBounds = new Rect(x, y, TileWidth, ThumbnailHeight);
        context.DrawRectangle(TileBrush, null, imageBounds, 3, 3);
        var bitmap = GetThumbnail(tile.Photo.ThumbnailPath);
        if (bitmap is not null)
        {
            using (context.PushClip(imageBounds))
            {
                var source = CoverSourceRect(bitmap.Size, imageBounds.Size);
                context.DrawImage(bitmap, source, imageBounds);
            }
        }

        if (tile.IsVideo)
        {
            var badge = new Rect(x + 7, y + 7, 50, 22);
            context.DrawRectangle(VideoBadgeBrush, null, badge, 3, 3);
            var video = new FormattedText("PLAY", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                TextTypeface, 10, Brushes.White);
            context.DrawText(video, new Point(badge.X + 9, badge.Y + 5));
        }

        var filename = Shorten(Path.GetFileName(tile.Photo.Path), MaximumLabelLength);
        var label = new FormattedText(filename, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            TextTypeface, LabelFontSize, TitleBrush);
        context.DrawText(label, new Point(x, y + ThumbnailHeight + LabelOffset));
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
            var rows = (int)Math.Ceiling(group.Photos.Count / (double)columns);
            var height = HeaderHeight + (rows * RowHeight) + GroupGap;
            _layouts.Add(new GroupLayout(group, top, columns, rows, top + HeaderHeight, height));
            top += height;
        }
        _contentHeight = Math.Max(TopPadding, top);
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _unreadableThumbnails.Clear();
        _layoutWidth = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ScrollViewerScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private void TimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!TryGetPhotoAt(e.GetPosition(this), out var photo)) return;
        PhotoDoubleClicked?.Invoke(this, photo);
        e.Handled = true;
    }

    private bool TryGetPhotoAt(Point point, out PhotoRecord photo)
    {
        foreach (var layout in _layouts)
        {
            if (point.Y < layout.FirstRowTop || point.Y >= layout.FirstRowTop + (layout.RowCount * RowHeight)) continue;
            var column = (int)Math.Floor((point.X - SidePadding) / (TileWidth + ColumnGap));
            if (column < 0 || column >= layout.Columns) continue;
            var tileLeft = SidePadding + column * (TileWidth + ColumnGap);
            var row = (int)Math.Floor((point.Y - layout.FirstRowTop) / RowHeight);
            var tileTop = layout.FirstRowTop + row * RowHeight;
            if (point.X > tileLeft + TileWidth || point.Y > tileTop + ThumbnailHeight) continue;

            var index = row * layout.Columns + column;
            if (index >= layout.Group.Photos.Count) continue;
            photo = layout.Group.Photos[index].Photo;
            return true;
        }

        photo = default!;
        return false;
    }

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

    private static Rect CoverSourceRect(Size source, Size destination)
    {
        var sourceRatio = source.Width / source.Height;
        var destinationRatio = destination.Width / destination.Height;
        if (sourceRatio > destinationRatio)
        {
            var width = source.Height * destinationRatio;
            return new Rect((source.Width - width) / 2, 0, width, source.Height);
        }
        var height = source.Width / destinationRatio;
        return new Rect(0, (source.Height - height) / 2, source.Width, height);
    }

    private double TileWidth => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 74,
        ThumbnailDisplaySize.Medium => 104,
        _ => 134
    };

    private double ThumbnailHeight => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 80,
        ThumbnailDisplaySize.Medium => 112,
        _ => 145
    };

    private double ColumnGap => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 10,
        ThumbnailDisplaySize.Medium => 12,
        _ => 16
    };

    private double RowHeight => ThumbnailSize switch
    {
        ThumbnailDisplaySize.Small => 108,
        ThumbnailDisplaySize.Medium => 143,
        _ => 181
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

    private sealed record GroupLayout(DateGroup Group, double Top, int Columns, int RowCount, double FirstRowTop, double Height)
    {
        public double Bottom => Top + Height;
    }

    private sealed record CacheItem(Bitmap Bitmap, LinkedListNode<string> Node);
}
