using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PicNest.Controls;
using PicNest.Models;
using PicNest.Services;
using PicNest.ViewModels;

namespace PicNest;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _viewModel = new();
    private readonly HashSet<string> _collapsedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ApplyCurrentTheme();
        UpdateThumbnailButtonState();
        FolderTree.AddHandler(TreeViewItem.CollapsedEvent, FolderCollapsed);
        FolderTree.AddHandler(TreeViewItem.ExpandedEvent, FolderExpanded);
        Opened += InitializeLibrary;
        Closed += (_, _) => _viewModel.Dispose();
        _viewModel.LibraryChanged += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            await _viewModel.RefreshAlbumsAsync();
            await _viewModel.RefreshPeopleAsync();
            await _viewModel.RefreshFolderTreeAsync();
            await _viewModel.RefreshAsync(SearchBox.Text ?? "");
            StatusText.Text = "Library updated.";
        });
    }

    private async void InitializeLibrary(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
        UpdateProtectedFolderButtonState();
        StatusText.Text = _viewModel.Status;
    }

    private async void ManageFolders(object? sender, RoutedEventArgs e)
    {
        var dialog = new ManageFoldersWindow(_viewModel);
        await dialog.ShowDialog(this);
        UpdateProtectedFolderButtonState();
        StatusText.Text = _viewModel.Status;
    }

    private async void ToggleProtectedFolders(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.AreProtectedFoldersUnlocked)
        {
            await _viewModel.SetProtectedFoldersUnlockedAsync(false);
            FolderTree.SelectedItem = null;
            AlbumList.SelectedItem = null;
            PeopleList.SelectedItem = null;
            UpdateProtectedFolderButtonState();
            StatusText.Text = _viewModel.Status;
            return;
        }

        string? password;
        if (!ProtectedFolderSecurity.HasPassword)
        {
            var setupDialog = new ProtectedPasswordWindow(ProtectedPasswordMode.Set);
            var setup = await setupDialog.ShowDialog<ProtectedPasswordResult?>(this);
            if (setup is null) return;
            try
            {
                await ProtectedFolderSecurity.SetPasswordAsync(setup.Password);
                password = setup.Password;
            }
            catch (Exception error)
            {
                StatusText.Text = $"Could not save the protected-folder password: {error.Message}";
                return;
            }
        }
        else
        {
            var unlockDialog = new ProtectedPasswordWindow(ProtectedPasswordMode.Unlock);
            var unlock = await unlockDialog.ShowDialog<ProtectedPasswordResult?>(this);
            if (unlock is null) return;
            password = unlock.Password;
        }

        if (!ProtectedFolderSecurity.Verify(password))
        {
            StatusText.Text = "That protected-folder password is not correct.";
            return;
        }

        await _viewModel.SetProtectedFoldersUnlockedAsync(true);
        FolderTree.SelectedItem = null;
        AlbumList.SelectedItem = null;
        PeopleList.SelectedItem = null;
        await _viewModel.ShowProtectedFoldersAsync();
        UpdateProtectedFolderButtonState();
        StatusText.Text = _viewModel.Status;
    }

    private void UpdateProtectedFolderButtonState()
    {
        ProtectedFoldersText.Text = _viewModel.AreProtectedFoldersUnlocked
            ? "Lock protected folders"
            : "Protected folders";
    }

    private async void ManageAlbums(object? sender, RoutedEventArgs e)
    {
        var dialog = new ManageAlbumsWindow(_viewModel, PhotoTimeline.GetSelectedPhotos());
        await dialog.ShowDialog(this);
        await _viewModel.RefreshAlbumsAsync();
        StatusText.Text = _viewModel.Status;
    }

    private async void ManagePeople(object? sender, RoutedEventArgs e)
    {
        var dialog = new PeopleWindow(_viewModel);
        var selectedPerson = await dialog.ShowDialog<FacePerson?>(this);
        await _viewModel.RefreshPeopleAsync();
        if (selectedPerson is not { } result) return;

        var person = _viewModel.People.FirstOrDefault(item => item.Id == result.Id);
        if (person is null) return;
        FolderTree.SelectedItem = null;
        AlbumList.SelectedItem = null;
        PeopleList.SelectedItem = person;
        await _viewModel.ShowFacePersonAsync(person);
        StatusText.Text = _viewModel.Status;
    }

    private async void StartSlideshowForGroup(object? sender, DateGroup group)
    {
        var photos = group.Photos.Select(tile => tile.Photo).ToArray();
        if (!PreferencesStore.Current.SlideshowIncludeVideos)
            photos = photos.Where(photo => photo.MediaKind == MediaKind.Image).ToArray();
        if (photos.Length == 0)
        {
            StatusText.Text = "There are no visible photos that match the slideshow settings.";
            return;
        }

        var selected = PhotoTimeline.GetSelectedPhotos().FirstOrDefault(photo =>
            photos.Any(candidate => string.Equals(candidate.Path, photo.Path, StringComparison.OrdinalIgnoreCase))) ?? photos[0];
        await DiagnosticLog.InformationAsync($"Starting slideshow for {group.Title} with {photos.Length:n0} visible item(s).");
        var viewer = new PhotoViewerWindow(photos, selected, _viewModel.RotatePhotoAsync, slideshow: true);
        await viewer.ShowDialog(this);
        await DiagnosticLog.InformationAsync("Slideshow closed.");
    }

    private async void OpenPreferences(object? sender, RoutedEventArgs e)
    {
        var dialog = new PreferencesWindow();
        var saved = await dialog.ShowDialog<bool>(this);
        if (!saved) return;

        ApplyCurrentTheme();
        StatusText.Text = "Preferences saved. Refresh folders to rebuild thumbnails at a new cache location.";
    }

    private void ApplyCurrentTheme() =>
        PhotoTimeline.SetDarkMode(PreferencesStore.Current.Theme == AppThemePreference.Dark);

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
        AlbumList.SelectedItem = null;
        PeopleList.SelectedItem = null;
        StatusText.Text = "Showing all photos.";
    }

    private async void ShowFavorites(object? sender, RoutedEventArgs e)
    {
        FolderTree.SelectedItem = null;
        AlbumList.SelectedItem = null;
        PeopleList.SelectedItem = null;
        await _viewModel.ShowFavoritesAsync();
        StatusText.Text = "Showing favorites.";
    }

    private async void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode folder) return;
        AlbumList.SelectedItem = null;
        PeopleList.SelectedItem = null;
        await _viewModel.SelectFolderAsync(folder.Path, includeSubfolders: !_collapsedFolderPaths.Contains(folder.Path));
        StatusText.Text = $"Showing {folder.DisplayName}.";
    }

    private async void FolderCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: FolderNode collapsed }) return;
        _collapsedFolderPaths.Add(collapsed.Path);
        await _viewModel.SetCollapsedFolderPathsAsync(_collapsedFolderPaths);

        if (FolderTree.SelectedItem is not FolderNode selected || !IsSameOrChildFolder(selected.Path, collapsed.Path)) return;
        if (!string.Equals(selected.Path, collapsed.Path, StringComparison.OrdinalIgnoreCase))
        {
            FolderTree.SelectedItem = collapsed;
            return;
        }

        StatusText.Text = $"Thumbnails in {collapsed.DisplayName} are hidden while it is collapsed.";
    }

    private async void FolderExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: FolderNode expanded }) return;
        _collapsedFolderPaths.Remove(expanded.Path);
        await _viewModel.SetCollapsedFolderPathsAsync(_collapsedFolderPaths);
        if (FolderTree.SelectedItem is not FolderNode selected ||
            !string.Equals(selected.Path, expanded.Path, StringComparison.OrdinalIgnoreCase)) return;

        await _viewModel.SelectFolderAsync(expanded.Path, includeSubfolders: true);
        StatusText.Text = $"Thumbnails in {expanded.DisplayName} are visible again.";
    }

    private static bool IsSameOrChildFolder(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private async void AlbumSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (AlbumList.SelectedItem is not Album album) return;
        FolderTree.SelectedItem = null;
        PeopleList.SelectedItem = null;
        await _viewModel.ShowAlbumAsync(album);
        StatusText.Text = _viewModel.Status;
    }

    private async void PersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not FacePerson person) return;
        FolderTree.SelectedItem = null;
        AlbumList.SelectedItem = null;
        await _viewModel.ShowFacePersonAsync(person);
        StatusText.Text = _viewModel.Status;
    }

    private async void OpenPhotoViewer(object? sender, PhotoRecord photo)
    {
        var mediaType = photo.MediaKind == MediaKind.Video ? "video" : "image";
        await DiagnosticLog.InformationAsync($"Opening {mediaType} viewer: {photo.Path}");
        PhotoViewerWindow? viewer = null;

        try
        {
            viewer = new PhotoViewerWindow(_viewModel.GetVisiblePhotos(), photo, _viewModel.RotatePhotoAsync);
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

    private void PhotoSelectionChanged(object? sender, EventArgs e)
    {
        var selected = PhotoTimeline.GetSelectedPhotos();
        var hasSelection = selected.Count > 0;
        SelectionActions.IsVisible = hasSelection;
        DefaultFooterActions.IsVisible = !hasSelection;
        if (!hasSelection) return;

        SelectedCountText.Text = $"{selected.Count:n0} selected";
        ToggleFavoriteText.Text = selected.All(photo => photo.IsFavorite) ? "Remove favorite" : "Favorite";
    }

    private async void ToggleFavoriteSelected(object? sender, RoutedEventArgs e)
    {
        var selected = PhotoTimeline.GetSelectedPhotos();
        if (selected.Count == 0) return;
        await ToggleFavoritesAsync(selected);
    }

    private async void ToggleFavoriteFromTile(object? sender, PhotoRecord photo)
    {
        await ToggleFavoritesAsync([photo]);
    }

    private async void CopySelected(object? sender, RoutedEventArgs e)
    {
        var destination = await PickDestinationFolderAsync("Copy selected items to");
        if (destination is null) return;
        await TransferSelectedAsync(destination, move: false);
    }

    private async void MoveSelected(object? sender, RoutedEventArgs e)
    {
        var destination = await PickDestinationFolderAsync("Move selected items to");
        if (destination is null) return;
        await TransferSelectedAsync(destination, move: true);
    }

    private async void DeleteSelected(object? sender, RoutedEventArgs e)
    {
        var selected = PhotoTimeline.GetSelectedPhotos();
        if (selected.Count == 0) return;
        await DeletePhotosAsync(selected);
    }

    private void ClearSelection(object? sender, RoutedEventArgs e) => PhotoTimeline.ClearSelection();

    private async Task TransferSelectedAsync(string destination, bool move)
    {
        var selected = PhotoTimeline.GetSelectedPhotos();
        if (selected.Count == 0) return;

        StatusText.Text = $"{(move ? "Moving" : "Copying")} {selected.Count:n0} item(s)...";
        if (move) await _viewModel.MoveMediaAsync(selected, destination);
        else await _viewModel.CopyMediaAsync(selected, destination);
        PhotoTimeline.ClearSelection();
        StatusText.Text = _viewModel.Status;
    }

    private async Task ToggleFavoritesAsync(IReadOnlyList<PhotoRecord> selected)
    {
        var selectedPaths = selected.Select(photo => photo.Path).ToArray();
        var markFavorite = selected.Any(photo => !photo.IsFavorite);
        await _viewModel.SetFavoritesAsync(selected, markFavorite);
        PhotoTimeline.SelectPaths(selectedPaths);
        StatusText.Text = _viewModel.Status;
    }

    private async Task DeletePhotosAsync(IReadOnlyList<PhotoRecord> selected)
    {
        if (!await ConfirmDeleteWindow.ShowAsync(this, selected.Count)) return;

        StatusText.Text = $"Moving {selected.Count:n0} item(s) to the Recycle Bin...";
        await _viewModel.DeleteMediaAsync(selected);
        PhotoTimeline.ClearSelection();
        StatusText.Text = _viewModel.Status;
    }

    private async void HandlePhotoContextAction(object? sender, PhotoContextActionEventArgs e)
    {
        switch (e.Action)
        {
            case PhotoContextAction.ShowInFileExplorer:
                ShowInFileExplorer(e.Photo);
                break;
            case PhotoContextAction.ToggleFavorite:
                await ToggleFavoritesAsync(e.SelectedPhotos);
                break;
            case PhotoContextAction.Copy:
            {
                var destination = await PickDestinationFolderAsync("Copy selected items to");
                if (destination is not null) await TransferSelectedAsync(destination, move: false);
                break;
            }
            case PhotoContextAction.Move:
            {
                var destination = await PickDestinationFolderAsync("Move selected items to");
                if (destination is not null) await TransferSelectedAsync(destination, move: true);
                break;
            }
            case PhotoContextAction.Delete:
                await DeletePhotosAsync(e.SelectedPhotos);
                break;
        }
    }

    private void ShowInFileExplorer(PhotoRecord photo)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{photo.Path}\"",
                UseShellExecute = true
            });
            _ = DiagnosticLog.InformationAsync($"Opened File Explorer for: {photo.Path}");
        }
        catch (Exception error)
        {
            StatusText.Text = $"Could not open File Explorer: {error.Message}";
            _ = DiagnosticLog.ErrorAsync($"Could not open File Explorer for {photo.Path}: {error}");
        }
    }

    private async Task<string?> PickDestinationFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
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
