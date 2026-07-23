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
              MediaKind TEXT NOT NULL DEFAULT 'Image',
              DurationMilliseconds INTEGER NULL,
              RotationDegrees INTEGER NOT NULL DEFAULT 0,
              Caption TEXT NOT NULL DEFAULT '',
              ImportedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE INDEX IF NOT EXISTS IX_Photos_DateTaken ON Photos(DateTaken DESC);
            CREATE INDEX IF NOT EXISTS IX_Photos_FolderPath ON Photos(FolderPath);
            CREATE TABLE IF NOT EXISTS LibraryRoots (
              Path TEXT PRIMARY KEY COLLATE NOCASE,
              DisplayName TEXT NOT NULL,
              IsProtected INTEGER NOT NULL DEFAULT 0,
              AddedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS Albums (
              Id INTEGER PRIMARY KEY,
              Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
              CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS AlbumPhotos (
              AlbumId INTEGER NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE,
              PhotoPath TEXT NOT NULL COLLATE NOCASE,
              AddedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
              PRIMARY KEY(AlbumId, PhotoPath));
            CREATE INDEX IF NOT EXISTS IX_AlbumPhotos_Path ON AlbumPhotos(PhotoPath);
            CREATE TABLE IF NOT EXISTS Tags (PhotoId INTEGER NOT NULL REFERENCES Photos(Id) ON DELETE CASCADE, Tag TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(PhotoId, Tag));
            CREATE TABLE IF NOT EXISTS People (PhotoId INTEGER NOT NULL REFERENCES Photos(Id) ON DELETE CASCADE, PersonName TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(PhotoId, PersonName));
            CREATE TABLE IF NOT EXISTS FacePeople (
              Id INTEGER PRIMARY KEY,
              Name TEXT NULL COLLATE NOCASE,
              Embedding BLOB NOT NULL,
              FaceCount INTEGER NOT NULL DEFAULT 0,
              RepresentativeFacePath TEXT NOT NULL,
              CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
              UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_FacePeople_Name ON FacePeople(Name) WHERE Name IS NOT NULL;
            CREATE TABLE IF NOT EXISTS FaceScans (
              PhotoId INTEGER PRIMARY KEY REFERENCES Photos(Id) ON DELETE CASCADE,
              SourceHash TEXT NOT NULL,
              ScannedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE IF NOT EXISTS DetectedFaces (
              Id INTEGER PRIMARY KEY,
              PhotoId INTEGER NOT NULL REFERENCES Photos(Id) ON DELETE CASCADE,
              PersonId INTEGER NOT NULL REFERENCES FacePeople(Id) ON DELETE RESTRICT,
              FaceIndex INTEGER NOT NULL,
              Confidence REAL NOT NULL,
              Embedding BLOB NOT NULL,
              PreviewPath TEXT NOT NULL,
              UNIQUE(PhotoId, FaceIndex));
            CREATE INDEX IF NOT EXISTS IX_DetectedFaces_Person ON DetectedFaces(PersonId);
            CREATE INDEX IF NOT EXISTS IX_DetectedFaces_Photo ON DetectedFaces(PhotoId);
            """;
        await command.ExecuteNonQueryAsync();
        await EnsureColumnAsync(db, "Photos", "MediaKind", "TEXT NOT NULL DEFAULT 'Image'");
        await EnsureColumnAsync(db, "Photos", "DurationMilliseconds", "INTEGER NULL");
        await EnsureColumnAsync(db, "Photos", "RotationDegrees", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "LibraryRoots", "IsProtected", "INTEGER NOT NULL DEFAULT 0");
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
        var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Path, DisplayName, IsProtected FROM LibraryRoots ORDER BY DisplayName COLLATE NOCASE;";
        var results = new List<LibraryRoot>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(new LibraryRoot(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2)));
        return results;
    }

    public async Task SetRootProtectionAsync(string path, bool isProtected)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE LibraryRoots SET IsProtected=$protected WHERE Path=$path;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$protected", isProtected ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Removes a folder from PicNest's catalog. Source files are never touched.</summary>
    public async Task<int> RemoveRootAsync(string rootPath)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        using var transaction = db.BeginTransaction();
        var rootPrefix = (Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar) + "%";

        var photos = db.CreateCommand();
        photos.Transaction = transaction;
        photos.CommandText = "DELETE FROM Photos WHERE FolderPath=$root OR FolderPath LIKE $prefix;";
        photos.Parameters.AddWithValue("$root", rootPath);
        photos.Parameters.AddWithValue("$prefix", rootPrefix);
        var removedPhotos = await photos.ExecuteNonQueryAsync();

        var root = db.CreateCommand();
        root.Transaction = transaction;
        root.CommandText = "DELETE FROM LibraryRoots WHERE Path=$root;";
        root.Parameters.AddWithValue("$root", rootPath);
        await root.ExecuteNonQueryAsync();
        transaction.Commit();
        return removedPhotos;
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
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync();
        var albumPhoto = db.CreateCommand();
        albumPhoto.Transaction = transaction;
        albumPhoto.CommandText = "DELETE FROM AlbumPhotos WHERE PhotoPath = $path;";
        albumPhoto.Parameters.AddWithValue("$path", path);
        await albumPhoto.ExecuteNonQueryAsync();
        var photo = db.CreateCommand();
        photo.Transaction = transaction;
        photo.CommandText = "DELETE FROM Photos WHERE Path = $path;";
        photo.Parameters.AddWithValue("$path", path);
        await photo.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT a.Id, a.Name, COUNT(p.Path)
            FROM Albums a
            LEFT JOIN AlbumPhotos ap ON ap.AlbumId = a.Id
            LEFT JOIN Photos p ON p.Path = ap.PhotoPath
            GROUP BY a.Id, a.Name
            ORDER BY a.Name COLLATE NOCASE;
            """;
        var results = new List<Album>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(new Album(rows.GetInt64(0), rows.GetString(1), rows.GetInt32(2)));
        return results;
    }

    public async Task<Album> CreateAlbumAsync(string name)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)) throw new ArgumentException("An album needs a name.", nameof(name));

        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO Albums(Name) VALUES($name);";
        insert.Parameters.AddWithValue("$name", normalizedName);
        await insert.ExecuteNonQueryAsync();

        var query = db.CreateCommand();
        query.CommandText = "SELECT Id, Name FROM Albums WHERE Name=$name;";
        query.Parameters.AddWithValue("$name", normalizedName);
        await using var rows = await query.ExecuteReaderAsync();
        if (!await rows.ReadAsync()) throw new InvalidOperationException("PicNest could not retrieve the new album.");
        return new Album(rows.GetInt64(0), rows.GetString(1), 0);
    }

    public async Task DeleteAlbumAsync(long albumId)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Albums WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", albumId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> AddPhotosToAlbumAsync(long albumId, IEnumerable<string> paths)
    {
        var uniquePaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (uniquePaths.Length == 0) return 0;

        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync();
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT OR IGNORE INTO AlbumPhotos(AlbumId, PhotoPath) VALUES($albumId, $path);";
        var album = cmd.Parameters.Add("$albumId", SqliteType.Integer);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        album.Value = albumId;
        var added = 0;
        foreach (var itemPath in uniquePaths)
        {
            path.Value = itemPath;
            added += await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return added;
    }

    public async Task UpdateAlbumPhotoPathAsync(string sourcePath, string destinationPath)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE AlbumPhotos SET PhotoPath=$destination WHERE PhotoPath=$source;";
        cmd.Parameters.AddWithValue("$source", sourcePath);
        cmd.Parameters.AddWithValue("$destination", destinationPath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetFavoriteAsync(IEnumerable<string> paths, bool isFavorite)
    {
        var uniquePaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (uniquePaths.Length == 0) return;

        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync();
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE Photos SET IsFavorite=$favorite WHERE Path=$path;";
        var favorite = cmd.Parameters.Add("$favorite", SqliteType.Integer);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        favorite.Value = isFavorite ? 1 : 0;
        foreach (var itemPath in uniquePaths)
        {
            path.Value = itemPath;
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    /// <summary>Stores PicNest's non-destructive display rotation and its matching derived thumbnail.</summary>
    public async Task SetRotationAsync(string path, int rotationDegrees, string thumbnailPath)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Photos SET RotationDegrees=$rotation, ThumbnailPath=$thumbnail WHERE Path=$path;";
        cmd.Parameters.AddWithValue("$rotation", rotationDegrees);
        cmd.Parameters.AddWithValue("$thumbnail", thumbnailPath);
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
        await UpsertBatchAsync([photo]);
    }

    /// <summary>
    /// Adds or updates a group of catalog records in one SQLite transaction. Image files and
    /// thumbnails are deliberately kept out of the database; this is metadata only.
    /// </summary>
    public async Task UpsertBatchAsync(IReadOnlyList<PhotoRecord> photos, CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return;

        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Photos(Path, FolderPath, DateTaken, Hash, Width, Height, ThumbnailPath, IsFavorite, MediaKind, DurationMilliseconds, RotationDegrees)
            VALUES($path,$folder,$date,$hash,$width,$height,$thumb,$favorite,$mediaKind,$duration,$rotation)
            ON CONFLICT(Path) DO UPDATE SET FolderPath=excluded.FolderPath, DateTaken=excluded.DateTaken,
              Hash=excluded.Hash, Width=excluded.Width, Height=excluded.Height,
              ThumbnailPath=CASE WHEN Photos.RotationDegrees=0 THEN excluded.ThumbnailPath ELSE Photos.ThumbnailPath END,
              MediaKind=excluded.MediaKind, DurationMilliseconds=excluded.DurationMilliseconds,
              RotationDegrees=Photos.RotationDegrees;
            """;
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        var folder = cmd.Parameters.Add("$folder", SqliteType.Text);
        var date = cmd.Parameters.Add("$date", SqliteType.Text);
        var hash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var width = cmd.Parameters.Add("$width", SqliteType.Integer);
        var height = cmd.Parameters.Add("$height", SqliteType.Integer);
        var thumbnail = cmd.Parameters.Add("$thumb", SqliteType.Text);
        var favorite = cmd.Parameters.Add("$favorite", SqliteType.Integer);
        var mediaKind = cmd.Parameters.Add("$mediaKind", SqliteType.Text);
        var duration = cmd.Parameters.Add("$duration", SqliteType.Integer);
        var rotation = cmd.Parameters.Add("$rotation", SqliteType.Integer);

        foreach (var photo in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path.Value = photo.Path;
            folder.Value = photo.FolderPath;
            date.Value = photo.DateTaken.ToUniversalTime().ToString("O");
            hash.Value = photo.Hash;
            width.Value = photo.Width;
            height.Value = photo.Height;
            thumbnail.Value = photo.ThumbnailPath;
            favorite.Value = photo.IsFavorite ? 1 : 0;
            mediaKind.Value = photo.MediaKind.ToString();
            duration.Value = photo.DurationMilliseconds is null ? DBNull.Value : photo.DurationMilliseconds.Value;
            rotation.Value = photo.RotationDegrees;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PhotoRecord>> SearchAsync(string query = "", string? folderPath = null,
        bool favoritesOnly = false, bool includeSubfolders = true)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT Id,Path,FolderPath,DateTaken,Hash,Width,Height,ThumbnailPath,IsFavorite,MediaKind,DurationMilliseconds,RotationDegrees FROM Photos
          WHERE ($query='' OR Path LIKE '%' || $query || '%' OR Caption LIKE '%' || $query || '%')
            AND ($folder='' OR FolderPath=$folder OR ($includeSubfolders=1 AND FolderPath LIKE $folderPrefix))
            AND ($favoritesOnly=0 OR IsFavorite=1)
          ORDER BY DateTaken DESC, Path;
          """;
        cmd.Parameters.AddWithValue("$query", query);
        var folder = folderPath ?? "";
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$folderPrefix", folder.Length == 0 ? "" : (Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar) + "%");
        cmd.Parameters.AddWithValue("$favoritesOnly", favoritesOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$includeSubfolders", includeSubfolders ? 1 : 0);
        var results = new List<PhotoRecord>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(ReadPhotoRecord(rows));
        return results;
    }

    public async Task<IReadOnlyList<PhotoRecord>> SearchAlbumAsync(long albumId, string query = "")
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT p.Id,p.Path,p.FolderPath,p.DateTaken,p.Hash,p.Width,p.Height,p.ThumbnailPath,p.IsFavorite,p.MediaKind,p.DurationMilliseconds,p.RotationDegrees
          FROM Photos p
          INNER JOIN AlbumPhotos ap ON ap.PhotoPath=p.Path
          WHERE ap.AlbumId=$albumId
            AND ($query='' OR p.Path LIKE '%' || $query || '%' OR p.Caption LIKE '%' || $query || '%')
          ORDER BY p.DateTaken DESC, p.Path;
          """;
        cmd.Parameters.AddWithValue("$albumId", albumId);
        cmd.Parameters.AddWithValue("$query", query);
        var results = new List<PhotoRecord>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(ReadPhotoRecord(rows));
        return results;
    }

    /// <summary>Returns only still images whose current file content has not yet had a face scan.</summary>
    public async Task<IReadOnlyList<PhotoRecord>> GetFaceScanCandidatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT p.Id,p.Path,p.FolderPath,p.DateTaken,p.Hash,p.Width,p.Height,p.ThumbnailPath,p.IsFavorite,p.MediaKind,p.DurationMilliseconds,p.RotationDegrees
          FROM Photos p
          LEFT JOIN FaceScans scan ON scan.PhotoId=p.Id
          WHERE p.MediaKind='Image' AND (scan.PhotoId IS NULL OR scan.SourceHash<>p.Hash)
          ORDER BY p.DateTaken, p.Path;
          """;
        var results = new List<PhotoRecord>();
        await using var rows = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken)) results.Add(ReadPhotoRecord(rows));
        return results;
    }

    public async Task<IReadOnlyList<FacePerson>> GetFacePeopleAsync()
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT person.Id,person.Name,COUNT(DISTINCT face.PhotoId),person.FaceCount,person.RepresentativeFacePath
          FROM FacePeople person
          INNER JOIN DetectedFaces face ON face.PersonId=person.Id
          GROUP BY person.Id,person.Name,person.FaceCount,person.RepresentativeFacePath
          HAVING COUNT(face.Id)>0
          ORDER BY CASE WHEN person.Name IS NULL OR person.Name='' THEN 1 ELSE 0 END,
                   person.Name COLLATE NOCASE, person.FaceCount DESC;
          """;
        var results = new List<FacePerson>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync())
            results.Add(new FacePerson(rows.GetInt64(0), rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetInt32(2), rows.GetInt32(3), rows.GetString(4)));
        return results;
    }

    internal async Task<IReadOnlyList<FacePersonVector>> GetFacePersonVectorsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,Embedding,FaceCount,RepresentativeFacePath FROM FacePeople;";
        var results = new List<FacePersonVector>();
        await using var rows = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
            results.Add(new FacePersonVector(rows.GetInt64(0), DeserializeEmbedding(rows.GetFieldValue<byte[]>(1)), rows.GetInt32(2), rows.GetString(3)));
        return results;
    }

    internal async Task<FacePersonVector> CreateFacePersonAsync(float[] embedding, string representativeFacePath,
        CancellationToken cancellationToken = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO FacePeople(Embedding,FaceCount,RepresentativeFacePath) VALUES($embedding,0,$preview); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$embedding", SerializeEmbedding(embedding));
        cmd.Parameters.AddWithValue("$preview", representativeFacePath);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        return new FacePersonVector(id, embedding, 0, representativeFacePath);
    }

    internal async Task RecordFaceScanAsync(PhotoRecord photo, IReadOnlyList<FaceAssignment> assignments,
        IReadOnlyCollection<FacePersonVector> updatedPeople, CancellationToken cancellationToken = default)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);

        var clearFaces = db.CreateCommand();
        clearFaces.Transaction = transaction;
        clearFaces.CommandText = "DELETE FROM DetectedFaces WHERE PhotoId=$photoId;";
        clearFaces.Parameters.AddWithValue("$photoId", photo.Id);
        await clearFaces.ExecuteNonQueryAsync(cancellationToken);

        var updatePerson = db.CreateCommand();
        updatePerson.Transaction = transaction;
        updatePerson.CommandText = """
            UPDATE FacePeople SET Embedding=$embedding,FaceCount=$count,RepresentativeFacePath=$preview,
              UpdatedAt=CURRENT_TIMESTAMP WHERE Id=$id;
            """;
        var updateId = updatePerson.Parameters.Add("$id", SqliteType.Integer);
        var updateEmbedding = updatePerson.Parameters.Add("$embedding", SqliteType.Blob);
        var updateCount = updatePerson.Parameters.Add("$count", SqliteType.Integer);
        var updatePreview = updatePerson.Parameters.Add("$preview", SqliteType.Text);
        foreach (var person in updatedPeople)
        {
            updateId.Value = person.Id;
            updateEmbedding.Value = SerializeEmbedding(person.Embedding);
            updateCount.Value = person.FaceCount;
            updatePreview.Value = person.RepresentativeFacePath;
            await updatePerson.ExecuteNonQueryAsync(cancellationToken);
        }

        var insertFace = db.CreateCommand();
        insertFace.Transaction = transaction;
        insertFace.CommandText = """
            INSERT INTO DetectedFaces(PhotoId,PersonId,FaceIndex,Confidence,Embedding,PreviewPath)
            VALUES($photoId,$personId,$faceIndex,$confidence,$embedding,$preview);
            """;
        var facePhotoId = insertFace.Parameters.Add("$photoId", SqliteType.Integer);
        var facePersonId = insertFace.Parameters.Add("$personId", SqliteType.Integer);
        var faceIndex = insertFace.Parameters.Add("$faceIndex", SqliteType.Integer);
        var faceConfidence = insertFace.Parameters.Add("$confidence", SqliteType.Real);
        var faceEmbedding = insertFace.Parameters.Add("$embedding", SqliteType.Blob);
        var facePreview = insertFace.Parameters.Add("$preview", SqliteType.Text);
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            facePhotoId.Value = photo.Id;
            facePersonId.Value = assignment.PersonId;
            faceIndex.Value = index;
            faceConfidence.Value = assignment.Detection.Confidence;
            faceEmbedding.Value = SerializeEmbedding(assignment.Detection.Embedding);
            facePreview.Value = assignment.Detection.PreviewPath;
            await insertFace.ExecuteNonQueryAsync(cancellationToken);
        }

        var scan = db.CreateCommand();
        scan.Transaction = transaction;
        scan.CommandText = """
            INSERT INTO FaceScans(PhotoId,SourceHash) VALUES($photoId,$hash)
            ON CONFLICT(PhotoId) DO UPDATE SET SourceHash=excluded.SourceHash,ScannedAt=CURRENT_TIMESTAMP;
            """;
        scan.Parameters.AddWithValue("$photoId", photo.Id);
        scan.Parameters.AddWithValue("$hash", photo.Hash);
        await scan.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RenameFacePersonAsync(long personId, string? name, CancellationToken cancellationToken = default)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(cancellationToken);
        var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE FacePeople SET Name=$name,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", personId);
        cmd.Parameters.AddWithValue("$name", normalizedName is null ? DBNull.Value : normalizedName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PhotoRecord>> SearchFacePersonAsync(long personId, string query = "")
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = """
          SELECT DISTINCT p.Id,p.Path,p.FolderPath,p.DateTaken,p.Hash,p.Width,p.Height,p.ThumbnailPath,p.IsFavorite,p.MediaKind,p.DurationMilliseconds,p.RotationDegrees
          FROM Photos p
          INNER JOIN DetectedFaces face ON face.PhotoId=p.Id
          WHERE face.PersonId=$personId
            AND ($query='' OR p.Path LIKE '%' || $query || '%' OR p.Caption LIKE '%' || $query || '%')
          ORDER BY p.DateTaken DESC,p.Path;
          """;
        cmd.Parameters.AddWithValue("$personId", personId);
        cmd.Parameters.AddWithValue("$query", query);
        var results = new List<PhotoRecord>();
        await using var rows = await cmd.ExecuteReaderAsync();
        while (await rows.ReadAsync()) results.Add(ReadPhotoRecord(rows));
        return results;
    }

    private static PhotoRecord ReadPhotoRecord(SqliteDataReader rows)
    {
        var mediaKind = Enum.TryParse<MediaKind>(rows.GetString(9), true, out var parsedKind) ? parsedKind : MediaKind.Image;
        long? duration = rows.IsDBNull(10) ? null : rows.GetInt64(10);
        var rotation = rows.GetInt32(11);
        return new PhotoRecord(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), DateTime.Parse(rows.GetString(3)), rows.GetString(4), rows.GetInt32(5), rows.GetInt32(6), rows.GetString(7), rows.GetBoolean(8), mediaKind, duration, rotation);
    }

    private static byte[] SerializeEmbedding(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) throw new InvalidDataException("A saved face embedding is invalid.");
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static async Task EnsureColumnAsync(SqliteConnection db, string tableName, string columnName, string columnDefinition)
    {
        var info = db.CreateCommand();
        info.CommandText = $"PRAGMA table_info({tableName});";
        var hasColumn = false;
        await using (var rows = await info.ExecuteReaderAsync())
            while (await rows.ReadAsync())
                if (string.Equals(rows.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
        if (hasColumn) return;

        var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync();
    }
}
