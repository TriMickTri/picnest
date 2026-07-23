namespace PicNest.Services;

/// <summary>All PicNest-owned data stays under LocalApplicationData; original photos are never moved or copied.</summary>
public static class LibraryPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicNest");
    public static readonly string Database = Path.Combine(Root, "library.db");
    public static readonly string SettingsFile = Path.Combine(Root, "preferences.json");
    public static readonly string DefaultThumbnails = Path.Combine(Root, "thumbnails");
    public static readonly string DefaultDiagnostics = Path.Combine(Root, "diag");
    public static readonly string FaceModels = Path.Combine(Root, "models");
    public static readonly string FaceThumbnails = Path.Combine(Root, "faces");

    // These are deliberately evaluated when used so a newly saved preference takes effect
    // immediately for new log entries and newly generated thumbnail files.
    public static string Thumbnails => PreferencesStore.Current.ThumbnailDirectory;
    public static string Diagnostics => PreferencesStore.Current.DiagnosticDirectory;
    public static string DiagnosticLogFile => Path.Combine(Diagnostics, "picnest.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(Diagnostics);
        Directory.CreateDirectory(FaceModels);
        Directory.CreateDirectory(FaceThumbnails);
    }
}
