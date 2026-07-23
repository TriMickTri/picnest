using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PicNest.Services;

namespace PicNest;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        LoadPreferences(PreferencesStore.Current);
    }

    private void LoadPreferences(AppPreferences preferences)
    {
        LightTheme.IsChecked = preferences.Theme == AppThemePreference.Light;
        DarkTheme.IsChecked = preferences.Theme == AppThemePreference.Dark;
        DiagnosticPath.Text = preferences.DiagnosticDirectory;
        ThumbnailPath.Text = preferences.ThumbnailDirectory;
        SlideDuration.Value = Convert.ToDecimal(preferences.SlideshowSeconds);
        TransitionStyle.SelectedIndex = (int)preferences.SlideshowTransition;
        IncludeSlideshowVideos.IsChecked = preferences.SlideshowIncludeVideos;
    }

    private async void BrowseDiagnostics(object? sender, RoutedEventArgs e) =>
        await BrowseForFolderAsync(DiagnosticPath, "Choose a folder for PicNest diagnostics");

    private async void BrowseThumbnails(object? sender, RoutedEventArgs e) =>
        await BrowseForFolderAsync(ThumbnailPath, "Choose a folder for PicNest thumbnails");

    private async Task BrowseForFolderAsync(TextBox target, string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) target.Text = path;
    }

    private void ResetDefaults(object? sender, RoutedEventArgs e)
    {
        var defaults = PreferencesStore.Defaults();
        var current = PreferencesStore.Current;
        LoadPreferences(new AppPreferences
        {
            Theme = defaults.Theme,
            DiagnosticDirectory = defaults.DiagnosticDirectory,
            ThumbnailDirectory = defaults.ThumbnailDirectory,
            SlideshowSeconds = defaults.SlideshowSeconds,
            SlideshowTransition = defaults.SlideshowTransition,
            SlideshowIncludeVideos = defaults.SlideshowIncludeVideos,
            ProtectedPasswordSalt = current.ProtectedPasswordSalt,
            ProtectedPasswordHash = current.ProtectedPasswordHash
        });
        DialogStatus.Text = "Defaults restored. Save changes to keep them.";
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void Save(object? sender, RoutedEventArgs e)
    {
        DialogStatus.Text = "Saving preferences...";
        try
        {
            var current = PreferencesStore.Current;
            var preferences = new AppPreferences
            {
                Theme = DarkTheme.IsChecked == true ? AppThemePreference.Dark : AppThemePreference.Light,
                DiagnosticDirectory = DiagnosticPath.Text ?? "",
                ThumbnailDirectory = ThumbnailPath.Text ?? "",
                SlideshowSeconds = Convert.ToDouble(SlideDuration.Value ?? 3),
                SlideshowTransition = Enum.IsDefined(typeof(SlideshowTransition), TransitionStyle.SelectedIndex)
                    ? (SlideshowTransition)TransitionStyle.SelectedIndex
                    : SlideshowTransition.Mix,
                SlideshowIncludeVideos = IncludeSlideshowVideos.IsChecked == true,
                ProtectedPasswordSalt = current.ProtectedPasswordSalt,
                ProtectedPasswordHash = current.ProtectedPasswordHash
            };
            await PreferencesStore.SaveAsync(preferences);
            if (Application.Current is App app) app.ApplyTheme(preferences.Theme);
            Close(true);
        }
        catch (Exception error)
        {
            DialogStatus.Text = $"Could not save preferences: {error.Message}";
        }
    }
}
