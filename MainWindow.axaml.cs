using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PicNest.ViewModels;

namespace PicNest;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
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

    private async void ImportFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a photo folder", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        StatusText.Text = "Scanning photos and generating thumbnails…";
        try
        {
            await _viewModel.ScanAsync(path, new Progress<string>(name => StatusText.Text = $"Indexing {name}"));
            StatusText.Text = _viewModel.Status;
        }
        catch (Exception error) { StatusText.Text = $"Could not scan folder: {error.Message}"; }
    }

    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (SearchBox is not null && SearchBox.Text is not null) await _viewModel.RefreshAsync(SearchBox.Text);
    }

    private async void RefreshFolders(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Refreshing folders and thumbnails…";
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
}
