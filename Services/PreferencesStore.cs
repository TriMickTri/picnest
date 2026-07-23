using System.Text.Json;

namespace PicNest.Services;

public enum AppThemePreference
{
    Light,
    Dark
}

public enum SlideshowTransition
{
    Fade,
    Swipe,
    Mix
}

/// <summary>Preferences that belong to PicNest itself, not to an imported photo folder.</summary>
public sealed class AppPreferences
{
    public AppThemePreference Theme { get; init; } = AppThemePreference.Light;
    public string DiagnosticDirectory { get; init; } = LibraryPaths.DefaultDiagnostics;
    public string ThumbnailDirectory { get; init; } = LibraryPaths.DefaultThumbnails;
    public double SlideshowSeconds { get; init; } = 3;
    public SlideshowTransition SlideshowTransition { get; init; } = SlideshowTransition.Mix;
    public bool SlideshowIncludeVideos { get; init; } = true;
    public string? ProtectedPasswordSalt { get; init; }
    public string? ProtectedPasswordHash { get; init; }
}

/// <summary>
/// Keeps a small, human-readable preferences file next to PicNest's SQLite catalog.
/// The media library and original photos are never stored in this file.
/// </summary>
public static class PreferencesStore
{
    private static readonly SemaphoreSlim SaveLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static AppPreferences _current = Load();

    public static AppPreferences Current => _current;

    public static AppPreferences Defaults() => new();

    public static async Task SaveAsync(AppPreferences preferences)
    {
        var normalized = Normalize(preferences);
        Directory.CreateDirectory(LibraryPaths.Root);
        Directory.CreateDirectory(normalized.DiagnosticDirectory);
        Directory.CreateDirectory(normalized.ThumbnailDirectory);

        await SaveLock.WaitAsync();
        var temporaryPath = $"{LibraryPaths.SettingsFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, LibraryPaths.SettingsFile, overwrite: true);
            _current = normalized;
        }
        finally
        {
            SaveLock.Release();
            try { File.Delete(temporaryPath); }
            catch (IOException) { /* The completed move has already made the preference durable. */ }
        }
    }

    private static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(LibraryPaths.SettingsFile)) return Defaults();
            var saved = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(LibraryPaths.SettingsFile));
            return saved is null ? Defaults() : Normalize(saved);
        }
        catch
        {
            // A damaged preference file should never stop the photo library from opening.
            return Defaults();
        }
    }

    private static AppPreferences Normalize(AppPreferences preferences) => new()
    {
        Theme = preferences.Theme,
        DiagnosticDirectory = NormalizeDirectory(preferences.DiagnosticDirectory, LibraryPaths.DefaultDiagnostics),
        ThumbnailDirectory = NormalizeDirectory(preferences.ThumbnailDirectory, LibraryPaths.DefaultThumbnails),
        SlideshowSeconds = NormalizeSlideshowSeconds(preferences.SlideshowSeconds),
        SlideshowTransition = Enum.IsDefined(preferences.SlideshowTransition)
            ? preferences.SlideshowTransition
            : SlideshowTransition.Mix,
        SlideshowIncludeVideos = preferences.SlideshowIncludeVideos,
        ProtectedPasswordSalt = preferences.ProtectedPasswordSalt,
        ProtectedPasswordHash = preferences.ProtectedPasswordHash
    };

    private static string NormalizeDirectory(string? directory, string fallback)
    {
        var resolved = string.IsNullOrWhiteSpace(directory) ? fallback : directory.Trim();
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        if (File.Exists(fullPath)) throw new InvalidOperationException($"The selected location is a file: {fullPath}");
        return fullPath;
    }

    private static double NormalizeSlideshowSeconds(double seconds) =>
        Math.Clamp(Math.Round(seconds * 2, MidpointRounding.AwayFromZero) / 2, 0.5, 60);
}
