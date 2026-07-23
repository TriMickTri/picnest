using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PicNest.Models;
using PicNest.ViewModels;

namespace PicNest;

public partial class ManageFoldersWindow : Window
{
    private readonly LibraryViewModel _library;

    public ManageFoldersWindow() : this(new LibraryViewModel())
    {
    }

    public ManageFoldersWindow(LibraryViewModel library)
    {
        _library = library;
        InitializeComponent();
        DataContext = _library;
    }

    private async void AddFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a photo folder to import",
            AllowMultiple = false
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;

        DialogStatus.Text = "Adding folder...";
        try
        {
            await _library.AddFolderAndQueueIndexAsync(path);
            DialogStatus.Text = "Folder added. Indexing is running in the background; you can close this window.";
        }
        catch (Exception error) { DialogStatus.Text = $"Could not import folder: {error.Message}"; }
    }

    private async void RemoveFolder(object? sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not LibraryRoot root)
        {
            DialogStatus.Text = "Select an imported folder to remove it from PicNest.";
            return;
        }

        DialogStatus.Text = $"Removing {root.DisplayName} from PicNest...";
        try
        {
            await _library.RemoveImportedFolderAsync(root);
            DialogStatus.Text = _library.Status;
        }
        catch (Exception error) { DialogStatus.Text = $"Could not remove folder: {error.Message}"; }
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
