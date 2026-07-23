using Avalonia.Controls;
using Avalonia.Interactivity;
using PicNest.Models;
using PicNest.ViewModels;

namespace PicNest;

public partial class PeopleWindow : Window
{
    private readonly LibraryViewModel _library;
    private CancellationTokenSource? _scanCancellation;

    public PeopleWindow() : this(new LibraryViewModel())
    {
    }

    public PeopleWindow(LibraryViewModel library)
    {
        _library = library;
        InitializeComponent();
        DataContext = _library;
        DialogStatus.Text = library.People.Count == 0
            ? "No faces have been scanned yet. Scan your indexed pictures to create local people albums."
            : "Select a group to name it or open its people album.";
        Closing += (_, _) => _scanCancellation?.Cancel();
    }

    private void PersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is FacePerson person) PersonName.Text = person.Name ?? "";
    }

    private async void SaveName(object? sender, RoutedEventArgs e)
    {
        if (PeopleList.SelectedItem is not FacePerson person)
        {
            DialogStatus.Text = "Select a people group first.";
            return;
        }

        try
        {
            await _library.RenameFacePersonAsync(person, PersonName.Text);
            PeopleList.SelectedItem = _library.People.FirstOrDefault(item => item.Id == person.Id);
            DialogStatus.Text = _library.Status;
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not save the name: {error.Message}";
        }
    }

    private void ViewPerson(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FacePerson fromRow })
        {
            Close(fromRow);
            return;
        }
        if (PeopleList.SelectedItem is FacePerson person) Close(person);
        else DialogStatus.Text = "Select a people group first.";
    }

    private async void ScanAllPictures(object? sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        _scanCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ScanProgress.IsVisible = true;
        DialogStatus.Text = "Preparing local face recognition…";
        var progress = new Progress<FaceScanProgress>(current =>
        {
            ScanProgress.Maximum = Math.Max(1, current.Total);
            ScanProgress.Value = current.Completed;
            DialogStatus.Text = current.Total == 0
                ? current.CurrentFile
                : $"{current.Completed:n0} of {current.Total:n0}: {current.CurrentFile}";
        });

        try
        {
            var result = await _library.ScanFacesAsync(progress, _scanCancellation.Token);
            DialogStatus.Text = result.WasAlreadyCurrent
                ? _library.Status
                : $"{_library.Status} {result.Failures:n0} picture(s) could not be scanned.";
        }
        catch (OperationCanceledException)
        {
            DialogStatus.Text = "Face scan cancelled. Already-scanned pictures were kept.";
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not start the local face scan: {error.Message}";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = true;
        }
    }

    private void CloseDialog(object? sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        Close();
    }
}
