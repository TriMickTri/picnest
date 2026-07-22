using Microsoft.Data.Sqlite;
using PicNest.Models;

namespace PicNest.Services;

/// <summary>Small, inspectable SQLite catalog. It stores references and metadata, never photo binaries.</summary>
public sealed class LibraryDatabase
{
    private readonly string _connectionString;

    public LibraryDatabase()
    {
        LibraryPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder { DataSource = LibraryPaths.Database, ForeignKeys = true }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Photos (
              Id INTEGER PRIMARY KEY,
              Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
              FolderPath TEXT NOT NULL,
              DateTaken TEXT NOT NULL,
              Hash TEXT NOT NULL,
              Width INTEGER NOT NULL,
              Height INTEGER NOT NULL,
              ThumbnailPath TEXT NOT NULL,
              IsFavorite INTEGER NOT NULL DEFAULT 0,
              Caption TEXT NOT NULL DEFAULT '',
              ImportedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE INDEX IF NOT EXISTS IX_Photos_DateTaken ON Photos(DateTaken DESC);
            CREATE INDEX IF NOT EXISTS IX_Photos_FolderPath ON Photos(FolderPath);
            CREATE TABLE IF NOT EXISTS LibraryRoots (
              Path TEXT PRIMARY KEY COLLATE NOCASE,
              DisplayName TEXT NOT NULL,
              AddedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS Tags (PhotoId INTEGER NOT NULL REFERENCES Photos(Id) ON DELETE CASCADE, Tag TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(PhotoId, Tag));
            CREATE TABLE IF NOT EXISTS People (PhotoId INTEGER NOT NULL REFERENCES Photos(Id) ON DELETE CASCADE, PersonName TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(PhotoId, PersonName));
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddRootAsync(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var displayName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = fullPath;
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO LibraryRoots(Path, DisplayName) VALUES($path,$name) ON CONFLICT(Path) DO NOTHING;";
        cmd.Parameters.AddWithValue("$path", fullPath); cmd.Parameters.AddWithValue("$name", displayName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<LibraryRoot>> GetRootsAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Path, DisplayName FROM LibraryRoots ORDER BY DisplayName COLLATE NOCASE;";
        var results = new List<LibraryRoot>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(new LibraryRoot(rows.GetString(0), rows.GetString(1)));
        return results;
    }

    public async Task<IReadOnlyList<FolderSummary>> GetFolderSummariesAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand(); cmd.CommandText = "SELECT FolderPath, COUNT(*) FROM Photos GROUP BY FolderPath ORDER BY FolderPath COLLATE NOCASE;";
        var results = new List<FolderSummary>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(new FolderSummary(rows.GetString(0), rows.GetInt32(1)));
        return results;
    }

    public async Task DeleteByPathAsync(string path)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM Photos WHERE Path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<string>> GetPhotoPathsUnderRootAsync(string rootPath)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT Path FROM Photos
          WHERE FolderPath=$root OR FolderPath LIKE $rootPrefix;
          """;
        cmd.Parameters.AddWithValue("$root", rootPath);
        cmd.Parameters.AddWithValue("$rootPrefix", (Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar) + "%");
        var results = new List<string>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(rows.GetString(0));
        return results;
    }

    public async Task UpsertAsync(PhotoRecord photo)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Photos(Path, FolderPath, DateTaken, Hash, Width, Height, ThumbnailPath, IsFavorite)
            VALUES($path,$folder,$date,$hash,$width,$height,$thumb,$favorite)
            ON CONFLICT(Path) DO UPDATE SET FolderPath=excluded.FolderPath, DateTaken=excluded.DateTaken,
              Hash=excluded.Hash, Width=excluded.Width, Height=excluded.Height, ThumbnailPath=excluded.ThumbnailPath;
            """;
        cmd.Parameters.AddWithValue("$path", photo.Path); cmd.Parameters.AddWithValue("$folder", photo.FolderPath);
        cmd.Parameters.AddWithValue("$date", photo.DateTaken.ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$hash", photo.Hash);
        cmd.Parameters.AddWithValue("$width", photo.Width); cmd.Parameters.AddWithValue("$height", photo.Height);
        cmd.Parameters.AddWithValue("$thumb", photo.ThumbnailPath); cmd.Parameters.AddWithValue("$favorite", photo.IsFavorite ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<PhotoRecord>> SearchAsync(string query = "", string? folderPath = null)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT Id,Path,FolderPath,DateTaken,Hash,Width,Height,ThumbnailPath,IsFavorite FROM Photos
          WHERE ($query='' OR Path LIKE '%' || $query || '%' OR Caption LIKE '%' || $query || '%')
            AND ($folder='' OR FolderPath=$folder OR FolderPath LIKE $folderPrefix)
          ORDER BY DateTaken DESC, Path;
          """;
        cmd.Parameters.AddWithValue("$query", query);
        var folder = folderPath ?? "";
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$folderPrefix", folder.Length == 0 ? "" : (Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar) + "%");
        var results = new List<PhotoRecord>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(new PhotoRecord(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), DateTime.Parse(rows.GetString(3)), rows.GetString(4), rows.GetInt32(5), rows.GetInt32(6), rows.GetString(7), rows.GetBoolean(8)));
        return results;
    }
}
