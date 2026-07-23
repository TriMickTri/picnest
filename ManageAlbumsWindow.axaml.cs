using Avalonia.Controls;
using Avalonia.Interactivity;
using PicNest.Models;
using PicNest.ViewModels;

namespace PicNest;

public partial class ManageAlbumsWindow : Window
{
    private readonly LibraryViewModel _library;
    private readonly IReadOnlyList<PhotoRecord> _selection;

    public ManageAlbumsWindow() : this(new LibraryViewModel(), [])
    {
    }

    public ManageAlbumsWindow(LibraryViewModel library, IReadOnlyList<PhotoRecord> selection)
    {
        _library = library;
        _selection = selection;
        InitializeComponent();
        DataContext = _library;
        SelectionHint.Text = selection.Count == 0
            ? "Create albums such as Trip to Italy. To add photos, select them in the library first."
            : $"{selection.Count:n0} selected item(s) can be added to an existing album, or to the new album you create.";
    }

    private async void CreateAlbum(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AlbumName.Text))
        {
            DialogStatus.Text = "Enter a name for the new album.";
            return;
        }

        try
        {
            var album = await _library.CreateAlbumAsync(AlbumName.Text, _selection);
            AlbumName.Text = "";
            AlbumList.SelectedItem = album;
            DialogStatus.Text = _library.Status;
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not create album: {error.Message}";
        }
    }

    private async void AddSelection(object? sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0)
        {
            DialogStatus.Text = "Select photos in the library before opening Albums.";
            return;
        }
        if (AlbumList.SelectedItem is not Album album)
        {
            DialogStatus.Text = "Select an album first.";
            return;
        }

        try
        {
            await _library.AddToAlbumAsync(album, _selection);
            DialogStatus.Text = _library.Status;
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not add to album: {error.Message}";
        }
    }

    private async void RemoveAlbum(object? sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is not Album album)
        {
            DialogStatus.Text = "Select an album to remove it.";
            return;
        }

        try
        {
            await _library.DeleteAlbumAsync(album);
            DialogStatus.Text = _library.Status;
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not remove album: {error.Message}";
        }
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
