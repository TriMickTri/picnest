namespace PicNest.Services;

/// <summary>All PicNest-owned data stays under LocalApplicationData; original photos are never moved or copied.</summary>
public static class LibraryPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicNest");
    public static readonly string Database = Path.Combine(Root, "library.db");
    public static readonly string Thumbnails = Path.Combine(Root, "thumbnails");
    public static readonly string Diagnostics = Path.Combine(Root, "diag");
    public static readonly string DiagnosticLogFile = Path.Combine(Diagnostics, "picnest.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(Diagnostics);
    }
}
