using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PicNest.Controls;
using PicNest.Models;
using PicNest.Services;
using PicNest.ViewModels;

namespace PicNest;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        UpdateThumbnailButtonState();
        Opened += InitializeLibrary;
        Closed += (_, _) => _viewModel.Dispose();
        _viewModel.LibraryChanged += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            await _viewModel.RefreshFolderTreeAsync();
            await _viewModel.RefreshAsync(SearchBox.Text ?? "");
            StatusText.Text = "Library updated.";
        });
    }

    private async void InitializeLibrary(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
        StatusText.Text = _viewModel.Status;
    }

    private async void ManageFolders(object? sender, RoutedEventArgs e)
    {
        var dialog = new ManageFoldersWindow(_viewModel);
        await dialog.ShowDialog(this);
        StatusText.Text = _viewModel.Status;
    }

    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (SearchBox is not null && SearchBox.Text is not null)
            await _viewModel.RefreshAsync(SearchBox.Text);
    }

    private async void RefreshFolders(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Refreshing folders and thumbnails...";
        try
        {
            await _viewModel.RefreshLibraryAsync(new Progress<string>(message => StatusText.Text = message));
            StatusText.Text = _viewModel.Status;
        }
        catch (Exception error) { StatusText.Text = $"Could not refresh folders: {error.Message}"; }
    }

    private async void ShowAllPhotos(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SelectFolderAsync(null);
        FolderTree.SelectedItem = null;
        StatusText.Text = "Showing all photos.";
    }

    private async void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode folder) return;
        await _viewModel.SelectFolderAsync(folder.Path);
        StatusText.Text = $"Showing {folder.DisplayName}.";
    }

    private async void OpenPhotoViewer(object? sender, PhotoRecord photo)
    {
        var mediaType = photo.MediaKind == MediaKind.Video ? "video" : "image";
        await DiagnosticLog.InformationAsync($"Opening {mediaType} viewer: {photo.Path}");
        PhotoViewerWindow? viewer = null;

        try
        {
            viewer = new PhotoViewerWindow(_viewModel.GetVisiblePhotos(), photo);
            await viewer.ShowDialog(this);
            await DiagnosticLog.InformationAsync($"Closed {mediaType} viewer: {photo.Path}");
        }
        catch (Exception error)
        {
            viewer?.ReleaseMediaResources();
            await DiagnosticLog.ErrorAsync($"Could not open {mediaType} viewer for {photo.Path}: {error}");
            StatusText.Text = $"Could not open viewer: {error.Message}";
        }
    }

    private void SetSmallThumbnails(object? sender, RoutedEventArgs e) =>
        SetThumbnailSize(ThumbnailDisplaySize.Small);

    private void SetMediumThumbnails(object? sender, RoutedEventArgs e) =>
        SetThumbnailSize(ThumbnailDisplaySize.Medium);

    private void SetLargeThumbnails(object? sender, RoutedEventArgs e) =>
        SetThumbnailSize(ThumbnailDisplaySize.Large);

    private void SetThumbnailSize(ThumbnailDisplaySize size)
    {
        PhotoTimeline.ThumbnailSize = size;
        UpdateThumbnailButtonState();
        StatusText.Text = $"Thumbnail size set to {size.ToString().ToLowerInvariant()}.";
    }

    private void UpdateThumbnailButtonState()
    {
        SetThumbnailButtonState(SmallThumbnailBorder, SmallThumbnailText,
            PhotoTimeline.ThumbnailSize == ThumbnailDisplaySize.Small);
        SetThumbnailButtonState(MediumThumbnailBorder, MediumThumbnailText,
            PhotoTimeline.ThumbnailSize == ThumbnailDisplaySize.Medium);
        SetThumbnailButtonState(LargeThumbnailBorder, LargeThumbnailText,
            PhotoTimeline.ThumbnailSize == ThumbnailDisplaySize.Large);
    }

    private static void SetThumbnailButtonState(Border border, TextBlock text, bool isSelected)
    {
        border.Background = new SolidColorBrush(Color.Parse(isSelected ? "#167397" : "#FFFFFF"));
        border.BorderBrush = new SolidColorBrush(Color.Parse(isSelected ? "#0F6382" : "#5D98AF"));
        text.Foreground = new SolidColorBrush(Color.Parse(isSelected ? "#FFFFFF" : "#0F6382"));
    }
}
