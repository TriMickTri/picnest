using System.Collections.ObjectModel;
using PicNest.Models;
using PicNest.Services;

namespace PicNest.ViewModels;

public sealed class LibraryViewModel : IDisposable
{
    private readonly LibraryDatabase _database = new();
    private readonly LibraryIndexer _indexer;
    private readonly FolderWatchService _watcher;
    private readonly FaceRecognitionService _faceRecognition = new();
    private readonly CancellationTokenSource _backgroundIndexCancellation = new();
    private readonly SemaphoreSlim _backgroundIndexGate = new(1, 1);
    private readonly object _backgroundIndexLock = new();
    private readonly HashSet<string> _queuedBackgroundFolders = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = "";
    private string? _selectedFolderPath;
    private long? _selectedAlbumId;
    private long? _selectedPersonId;
    private bool _favoritesOnly;
    private bool _includeSubfolders = true;
    private bool _protectedFoldersUnlocked;
    private bool _protectedOnly;
    private readonly HashSet<string> _collapsedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public LibraryViewModel()
    {
        _indexer = new LibraryIndexer(_database);
        _watcher = new FolderWatchService(HandleFileChangeAsync);
    }

    public ObservableCollection<DateGroup> Groups { get; } = [];
    public ObservableCollection<FolderNode> Folders { get; } = [];
    public ObservableCollection<Album> Albums { get; } = [];
    public ObservableCollection<FacePerson> People { get; } = [];
    public ObservableCollection<LibraryRoot> ImportedFolders { get; } = [];
    public string Status { get; private set; } = "Choose a folder to build your private library.";
    public bool AreProtectedFoldersUnlocked => _protectedFoldersUnlocked;
    public event EventHandler? LibraryChanged;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await RefreshImportedFoldersAsync();
        await RefreshAlbumsAsync();
        await RefreshPeopleAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync();
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        if (Folders.Count > 0) Status = "Watching your photo folders for changes.";
    }

    public async Task ScanAsync(string folder, IProgress<string>? progress = null)
    {
        await DiagnosticLog.InformationAsync($"Import scan requested for: {folder}");
        await _database.InitializeAsync();
        await _database.AddRootAsync(folder);
        Status = "Scanning…";
        var indexed = await _indexer.IndexFolderAsync(folder, progress);
        await RemoveMissingPhotosAsync(folder);
        await RefreshImportedFoldersAsync();
        await RefreshAlbumsAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        Status = $"{indexed:n0} media items indexed locally. Watching for changes.";
    }

    /// <summary>Adds a library root immediately and indexes it on a worker, so the folder dialog stays responsive.</summary>
    public async Task AddFolderAndQueueIndexAsync(string folder)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        await DiagnosticLog.InformationAsync($"Background import requested for: {fullPath}");
        await _database.InitializeAsync();
        await _database.AddRootAsync(fullPath);
        await RefreshImportedFoldersAsync();
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));

        lock (_backgroundIndexLock)
        {
            if (!_queuedBackgroundFolders.Add(fullPath))
            {
                Status = $"{Path.GetFileName(fullPath)} is already being indexed in the background.";
                return;
            }
        }

        Status = $"Added {Path.GetFileName(fullPath)}. Indexing is running in the background.";
        _ = Task.Run(() => IndexQueuedFolderAsync(fullPath, _backgroundIndexCancellation.Token));
    }

    private async Task IndexQueuedFolderAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            await _backgroundIndexGate.WaitAsync(cancellationToken);
            try
            {
                await DiagnosticLog.InformationAsync($"Background indexing started: {folder}");
                var indexed = await _indexer.IndexFolderAsync(folder, cancellationToken: cancellationToken);
                await RemoveMissingPhotosAsync(folder);
                Status = $"{indexed:n0} media items indexed locally. Watching for changes.";
                await DiagnosticLog.InformationAsync($"Background indexing completed: {folder}. {indexed:n0} media item(s) indexed.");
            }
            finally
            {
                _backgroundIndexGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Background indexing was cancelled while PicNest was closing.";
            await DiagnosticLog.InformationAsync($"Background indexing cancelled: {folder}");
        }
        catch (Exception error)
        {
            Status = $"Could not finish indexing {Path.GetFileName(folder)}: {error.Message}";
            await DiagnosticLog.ErrorAsync($"Background indexing failed for {folder}: {error}");
        }
        finally
        {
            lock (_backgroundIndexLock) _queuedBackgroundFolders.Remove(folder);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RefreshImportedFoldersAsync()
    {
        var roots = await _database.GetRootsAsync();
        ImportedFolders.Clear();
        foreach (var root in roots) ImportedFolders.Add(root);
    }

    public async Task SetRootProtectionAsync(LibraryRoot root, bool isProtected)
    {
        await _database.SetRootProtectionAsync(root.Path, isProtected);
        if (isProtected && !_protectedFoldersUnlocked && _selectedFolderPath is not null && IsWithin(_selectedFolderPath, root.Path))
            _selectedFolderPath = null;
        await RefreshImportedFoldersAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        Status = isProtected
            ? $"{root.DisplayName} is now hidden in PicNest's Protected view."
            : $"{root.DisplayName} is no longer protected in PicNest.";
        await DiagnosticLog.InformationAsync($"Set PicNest protected-folder state for {root.Path}: {isProtected}.");
    }

    public async Task SetProtectedFoldersUnlockedAsync(bool unlocked)
    {
        _protectedFoldersUnlocked = unlocked;
        if (!unlocked)
        {
            _protectedOnly = false;
            var protectedRoots = (await _database.GetRootsAsync()).Where(root => root.IsProtected).ToArray();
            if (_selectedFolderPath is not null && protectedRoots.Any(root => IsWithin(_selectedFolderPath, root.Path)))
                _selectedFolderPath = null;
        }
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        Status = unlocked ? "Protected folders unlocked for this PicNest session." : "Protected folders locked.";
        await DiagnosticLog.InformationAsync($"PicNest protected folders {(unlocked ? "unlocked" : "locked")} for this session.");
    }

    public async Task ShowProtectedFoldersAsync()
    {
        if (!_protectedFoldersUnlocked) throw new InvalidOperationException("Unlock protected folders first.");
        _selectedFolderPath = null;
        _selectedAlbumId = null;
        _selectedPersonId = null;
        _favoritesOnly = false;
        _includeSubfolders = true;
        _protectedOnly = true;
        await RefreshAsync(_searchText);
        Status = "Showing protected folders.";
    }

    public async Task RefreshAlbumsAsync()
    {
        var albums = await _database.GetAlbumsAsync();
        Albums.Clear();
        foreach (var album in albums) Albums.Add(album);
    }

    public async Task RefreshPeopleAsync()
    {
        var people = await _database.GetFacePeopleAsync();
        People.Clear();
        foreach (var person in people) People.Add(person);
    }

    public async Task RemoveImportedFolderAsync(LibraryRoot root)
    {
        await DiagnosticLog.InformationAsync($"Removing imported folder from PicNest catalog: {root.Path}");
        var removedPhotos = await _database.RemoveRootAsync(root.Path);
        if (_selectedFolderPath is not null && IsWithin(_selectedFolderPath, root.Path)) _selectedFolderPath = null;
        await RefreshImportedFoldersAsync();
        await RefreshAlbumsAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(item => item.Path));
        Status = $"Removed {root.DisplayName} from PicNest. {removedPhotos:n0} catalog photos removed; original files were not changed.";
    }

    public async Task RefreshLibraryAsync(IProgress<string>? progress = null)
    {
        await DiagnosticLog.InformationAsync("Refresh folders requested.");
        await _database.InitializeAsync();
        var roots = await _database.GetRootsAsync();
        await DiagnosticLog.InformationAsync($"Found {roots.Count:n0} saved library root(s) to refresh.");
        if (roots.Count == 0)
            await DiagnosticLog.WarningAsync("Refresh folders found no saved library roots. Import a folder first.");
        var indexed = 0;
        var removedRoots = 0;
        var removedPhotos = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                await DiagnosticLog.WarningAsync($"Saved library root is gone and will be removed from the PicNest catalog: {root.Path}");
                removedPhotos += await _database.RemoveRootAsync(root.Path);
                removedRoots++;
                if (_selectedFolderPath is not null && IsWithin(_selectedFolderPath, root.Path)) _selectedFolderPath = null;
                continue;
            }
            progress?.Report($"Scanning {root.DisplayName}…");
            indexed += await _indexer.IndexFolderAsync(root.Path, progress);
            await RemoveMissingPhotosAsync(root.Path);
        }
        await RefreshImportedFoldersAsync();
        await RefreshAlbumsAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        Status = removedRoots == 0
            ? $"Folders refreshed. {indexed:n0} media items checked."
            : $"Folders refreshed. {indexed:n0} media items checked; removed {removedRoots:n0} missing folder(s) and {removedPhotos:n0} catalog item(s).";
    }

    public async Task SelectFolderAsync(string? folderPath, bool includeSubfolders = true)
    {
        _selectedFolderPath = folderPath;
        _selectedAlbumId = null;
        _selectedPersonId = null;
        _favoritesOnly = false;
        _protectedOnly = false;
        _includeSubfolders = includeSubfolders;
        await RefreshAsync(_searchText);
    }

    public async Task ShowFavoritesAsync()
    {
        _selectedFolderPath = null;
        _selectedAlbumId = null;
        _selectedPersonId = null;
        _favoritesOnly = true;
        _protectedOnly = false;
        _includeSubfolders = true;
        await RefreshAsync(_searchText);
    }

    public async Task ShowAlbumAsync(Album album)
    {
        _selectedFolderPath = null;
        _selectedAlbumId = album.Id;
        _selectedPersonId = null;
        _favoritesOnly = false;
        _protectedOnly = false;
        _includeSubfolders = true;
        await RefreshAsync(_searchText);
        Status = $"Showing album: {album.Name}.";
    }

    /// <summary>Shows a person group as a virtual album without copying or changing any photo files.</summary>
    public async Task ShowFacePersonAsync(FacePerson person)
    {
        _selectedFolderPath = null;
        _selectedAlbumId = null;
        _selectedPersonId = person.Id;
        _favoritesOnly = false;
        _protectedOnly = false;
        _includeSubfolders = true;
        await RefreshAsync(_searchText);
        Status = $"Showing people album: {person.DisplayName}.";
    }

    public async Task RenameFacePersonAsync(FacePerson person, string? name)
    {
        await _database.RenameFacePersonAsync(person.Id, name);
        await RefreshPeopleAsync();
        if (_selectedPersonId == person.Id) await RefreshAsync(_searchText);
        Status = string.IsNullOrWhiteSpace(name)
            ? $"Cleared the name for Person {person.Id}."
            : $"Named this people album {name.Trim()}.";
        await DiagnosticLog.InformationAsync($"Updated local person name for group {person.Id}: {name?.Trim() ?? "<unnamed>"}.");
    }

    /// <summary>
    /// Scans only images whose current content has not already been processed. Detected faces are
    /// embedded and grouped locally; names are stored separately and supplied by the owner.
    /// </summary>
    public async Task<FaceScanSummary> ScanFacesAsync(IProgress<FaceScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync();
        var candidates = await _database.GetFaceScanCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            Status = "Every indexed picture is already up to date for People.";
            return new FaceScanSummary(0, 0, 0, 0);
        }

        Status = "Preparing local face recognition…";
        await DiagnosticLog.InformationAsync($"Local face scan started for {candidates.Count:n0} picture(s).");
        await _faceRecognition.EnsureReadyAsync(message =>
        {
            Status = message;
            progress?.Report(new FaceScanProgress(0, candidates.Count, message));
        }, cancellationToken);

        const float groupingThreshold = 0.46f;
        var people = (await _database.GetFacePersonVectorsAsync(cancellationToken)).ToDictionary(person => person.Id);
        var scanned = 0;
        var facesFound = 0;
        var newPeople = 0;
        var failures = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photo = candidates[index];
            progress?.Report(new FaceScanProgress(index, candidates.Count, Path.GetFileName(photo.Path)));
            try
            {
                var faces = await _faceRecognition.DetectAsync(photo, cancellationToken);
                var assignments = new List<FaceAssignment>(faces.Count);
                var changedPeople = new Dictionary<long, FacePersonVector>();
                foreach (var face in faces)
                {
                    var person = FindClosestPerson(people.Values, face.Embedding, groupingThreshold);
                    if (person is null)
                    {
                        person = await _database.CreateFacePersonAsync(face.Embedding, face.PreviewPath, cancellationToken);
                        people.Add(person.Id, person);
                        newPeople++;
                    }

                    person = MergeEmbedding(person, face.Embedding);
                    people[person.Id] = person;
                    changedPeople[person.Id] = person;
                    assignments.Add(new FaceAssignment(person.Id, face));
                }

                await _database.RecordFaceScanAsync(photo, assignments, changedPeople.Values, cancellationToken);
                scanned++;
                facesFound += faces.Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                failures++;
                await DiagnosticLog.WarningAsync($"Could not scan faces in {photo.Path}: {error.Message}");
            }
        }

        await RefreshPeopleAsync();
        if (_selectedPersonId is not null) await RefreshAsync(_searchText);
        progress?.Report(new FaceScanProgress(candidates.Count, candidates.Count, "Face scan complete."));
        Status = $"Face scan complete: {scanned:n0} picture(s), {facesFound:n0} face(s), {newPeople:n0} new people group(s).";
        await DiagnosticLog.InformationAsync($"Local face scan completed. {scanned:n0} picture(s), {facesFound:n0} face(s), {newPeople:n0} new group(s), {failures:n0} failure(s).");
        return new FaceScanSummary(scanned, facesFound, newPeople, failures);
    }

    /// <summary>Folders collapsed in the Explorer tree are excluded from the thumbnail timeline.</summary>
    public async Task SetCollapsedFolderPathsAsync(IEnumerable<string> paths)
    {
        _collapsedFolderPaths.Clear();
        foreach (var path in paths) _collapsedFolderPaths.Add(path);
        await RefreshAsync(_searchText);
    }

    public async Task<Album> CreateAlbumAsync(string name, IReadOnlyList<PhotoRecord>? photosToAdd = null)
    {
        await _database.InitializeAsync();
        var album = await _database.CreateAlbumAsync(name);
        var added = photosToAdd is null ? 0 : await _database.AddPhotosToAlbumAsync(album.Id, photosToAdd.Select(photo => photo.Path));
        await RefreshAlbumsAsync();
        Status = added == 0
            ? $"Created album: {album.Name}."
            : $"Created album: {album.Name}, with {added:n0} selected item(s).";
        await DiagnosticLog.InformationAsync($"Created album '{album.Name}' with {added:n0} initial item(s).");
        return Albums.FirstOrDefault(item => item.Id == album.Id) ?? album;
    }

    public async Task<int> AddToAlbumAsync(Album album, IReadOnlyList<PhotoRecord> photos)
    {
        var added = await _database.AddPhotosToAlbumAsync(album.Id, photos.Select(photo => photo.Path));
        await RefreshAlbumsAsync();
        if (_selectedAlbumId == album.Id) await RefreshAsync(_searchText);
        Status = added == 0
            ? $"Those item(s) are already in {album.Name}."
            : $"Added {added:n0} item(s) to {album.Name}.";
        await DiagnosticLog.InformationAsync($"Added {added:n0} item(s) to album '{album.Name}'.");
        return added;
    }

    public async Task DeleteAlbumAsync(Album album)
    {
        await _database.DeleteAlbumAsync(album.Id);
        if (_selectedAlbumId == album.Id)
        {
            _selectedAlbumId = null;
            await RefreshAsync(_searchText);
        }
        await RefreshAlbumsAsync();
        Status = $"Removed album: {album.Name}. Original files were not changed.";
        await DiagnosticLog.InformationAsync($"Removed album '{album.Name}'. Original files were not changed.");
    }

    public async Task SetFavoritesAsync(IReadOnlyList<PhotoRecord> photos, bool isFavorite)
    {
        await _database.SetFavoriteAsync(photos.Select(photo => photo.Path), isFavorite);
        await RefreshAsync(_searchText);
        Status = isFavorite
            ? $"{photos.Count:n0} item(s) added to Favorites."
            : $"{photos.Count:n0} item(s) removed from Favorites.";
        await DiagnosticLog.InformationAsync($"Marked {photos.Count:n0} selected item(s) as {(isFavorite ? "favorite" : "not favorite")}.");
    }

    /// <summary>Persists a display rotation and refreshes the derived thumbnail without altering the original file.</summary>
    public async Task<PhotoRecord> RotatePhotoAsync(PhotoRecord photo, int rotationDegrees)
    {
        if (photo.MediaKind != MediaKind.Image) throw new InvalidOperationException("Only still images can be rotated.");

        var rotation = NormalizeRotation(rotationDegrees);
        var thumbnailPath = await LibraryIndexer.CreateRotatedThumbnailAsync(photo, rotation);
        await _database.SetRotationAsync(photo.Path, rotation, thumbnailPath);
        var updated = photo with { RotationDegrees = rotation, ThumbnailPath = thumbnailPath };
        await RefreshAsync(_searchText);
        Status = rotation == 0
            ? $"Reset rotation for {Path.GetFileName(photo.Path)}."
            : $"Rotated {Path.GetFileName(photo.Path)} to {rotation}°.";
        await DiagnosticLog.InformationAsync($"Set non-destructive rotation to {rotation} degrees for {photo.Path}. Thumbnail cache updated: {thumbnailPath}");
        return updated;
    }

    public Task<BatchFileResult> CopyMediaAsync(IReadOnlyList<PhotoRecord> photos, string destinationFolder) =>
        TransferMediaAsync(photos, destinationFolder, move: false);

    public Task<BatchFileResult> MoveMediaAsync(IReadOnlyList<PhotoRecord> photos, string destinationFolder) =>
        TransferMediaAsync(photos, destinationFolder, move: true);

    public async Task<BatchFileResult> DeleteMediaAsync(IReadOnlyList<PhotoRecord> photos)
    {
        var result = await Task.Run(() =>
        {
            var deleted = new List<MediaTransfer>();
            var failures = new List<string>();
            foreach (var photo in photos)
            {
                try
                {
                    if (!File.Exists(photo.Path)) throw new FileNotFoundException("The original file no longer exists.");
                    RecycleBin.MoveFile(photo.Path);
                    deleted.Add(new MediaTransfer(photo, null));
                }
                catch (Exception error)
                {
                    failures.Add($"{Path.GetFileName(photo.Path)}: {error.Message}");
                }
            }
            return new BatchFileResult(deleted.Count, failures, deleted);
        });

        foreach (var transfer in result.Transfers) await _database.DeleteByPathAsync(transfer.Source.Path);
        await RefreshAlbumsAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        Status = BatchStatus("Deleted", result, "moved to the Recycle Bin");
        await DiagnosticLog.InformationAsync($"Deleted {result.Succeeded:n0} selected item(s) to the Recycle Bin. {result.Failures.Count:n0} failure(s).");
        return result;
    }

    public async Task RefreshAsync(string searchText = "")
    {
        await _database.InitializeAsync();
        _searchText = searchText;
        var records = _selectedPersonId is { } personId
            ? await _database.SearchFacePersonAsync(personId, _searchText)
            : _selectedAlbumId is { } albumId
                ? await _database.SearchAlbumAsync(albumId, _searchText)
                : await _database.SearchAsync(_searchText, _selectedFolderPath, _favoritesOnly, _includeSubfolders);
        var protectedRoots = (await _database.GetRootsAsync()).Where(root => root.IsProtected).ToArray();
        if (_protectedOnly)
            records = records.Where(photo => protectedRoots.Any(root => IsWithin(photo.FolderPath, root.Path))).ToArray();
        else if (!_protectedFoldersUnlocked && protectedRoots.Length > 0)
            records = records.Where(photo => !protectedRoots.Any(root => IsWithin(photo.FolderPath, root.Path))).ToArray();
        // Explorer collapse is a navigation filter, not a library-wide visibility rule.
        // Favorites and albums are virtual collections, so they stay complete regardless
        // of how the folder tree is expanded.
        if (_collapsedFolderPaths.Count > 0 && _selectedAlbumId is null && _selectedPersonId is null && !_favoritesOnly)
            records = records.Where(photo => !_collapsedFolderPaths.Any(folder => IsWithin(photo.FolderPath, folder))).ToArray();
        Groups.Clear();
        foreach (var group in records.GroupBy(p => p.DateTaken.Date).OrderByDescending(group => group.Key))
            Groups.Add(new DateGroup(group.Key, new ObservableCollection<PhotoTile>(group.Select(photo => new PhotoTile(photo)))));
    }

    /// <summary>Returns the current timeline order for viewer navigation.</summary>
    public IReadOnlyList<PhotoRecord> GetVisiblePhotos() =>
        Groups.SelectMany(group => group.Photos).Select(tile => tile.Photo).ToArray();

    public async Task RefreshFolderTreeAsync()
    {
        var roots = await _database.GetRootsAsync();
        if (!_protectedFoldersUnlocked) roots = roots.Where(root => !root.IsProtected).ToArray();
        var nodes = roots.ToDictionary(root => root.Path, root => new FolderNode(root.Path, root.DisplayName), StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(root => Directory.Exists(root.Path)))
            foreach (var path in EnumerateSubfolders(root.Path)) EnsureFolderNode(root, path, nodes);
        foreach (var summary in await _database.GetFolderSummariesAsync())
        {
            var root = roots.Where(candidate => IsWithin(summary.Path, candidate.Path)).OrderByDescending(candidate => candidate.Path.Length).FirstOrDefault();
            if (root is null) continue;
            var current = EnsureFolderNode(root, summary.Path, nodes);
            current.Count += summary.PhotoCount;
            for (var parent = FindParent(nodes, current.Path); parent is not null; parent = FindParent(nodes, parent.Path)) parent.Count += summary.PhotoCount;
        }
        Folders.Clear();
        foreach (var root in nodes.Values.Where(node => roots.Any(root => string.Equals(root.Path, node.Path, StringComparison.OrdinalIgnoreCase))).OrderBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)) Folders.Add(root);
        await DiagnosticLog.InformationAsync($"Folder tree refreshed with {nodes.Count:n0} folder node(s) across {roots.Count:n0} library root(s).");
    }

    private async Task HandleFileChangeAsync(string path, WatcherChangeTypes change)
    {
        if (change == WatcherChangeTypes.Created && Directory.Exists(path))
        {
            await DiagnosticLog.InformationAsync($"New directory found: {path}");
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!LibraryIndexer.IsSupportedMedia(path))
        {
            // Directory events do not have an image extension, but they still change the folder tree.
            if (change is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted) LibraryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (change.HasFlag(WatcherChangeTypes.Deleted) || !File.Exists(path))
        {
            await _database.DeleteByPathAsync(path);
        }
        else
        {
            if (change == WatcherChangeTypes.Created)
                await DiagnosticLog.InformationAsync($"New {(LibraryIndexer.IsSupportedVideo(path) ? "video" : "photo")} found: {path}");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { await _indexer.IndexMediaAsync(path); break; }
                catch (IOException) when (attempt < 2) { await Task.Delay(400); }
            }
        }
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(string folder, string root) => folder.Equals(root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static int NormalizeRotation(int rotationDegrees)
    {
        var normalized = rotationDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private async Task<BatchFileResult> TransferMediaAsync(IReadOnlyList<PhotoRecord> photos, string destinationFolder, bool move)
    {
        var result = await Task.Run(() =>
        {
            var transfers = new List<MediaTransfer>();
            var failures = new List<string>();
            Directory.CreateDirectory(destinationFolder);
            foreach (var photo in photos)
            {
                try
                {
                    if (!File.Exists(photo.Path)) throw new FileNotFoundException("The original file no longer exists.");
                    var destination = GetAvailableDestinationPath(photo.Path, destinationFolder, move);
                    if (destination is null) continue;
                    if (move) File.Move(photo.Path, destination);
                    else File.Copy(photo.Path, destination);
                    transfers.Add(new MediaTransfer(photo, destination));
                }
                catch (Exception error)
                {
                    failures.Add($"{Path.GetFileName(photo.Path)}: {error.Message}");
                }
            }
            return new BatchFileResult(transfers.Count, failures, transfers);
        });

        var roots = await _database.GetRootsAsync();
        foreach (var transfer in result.Transfers)
        {
            if (move)
            {
                if (transfer.Destination is not null) await _database.UpdateAlbumPhotoPathAsync(transfer.Source.Path, transfer.Destination);
                await _database.DeleteByPathAsync(transfer.Source.Path);
            }
            if (transfer.Destination is not null && roots.Any(root => IsWithin(transfer.Destination, root.Path)))
                await _indexer.IndexMediaAsync(transfer.Destination);
        }

        await RefreshAlbumsAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        var operation = move ? "Moved" : "Copied";
        Status = BatchStatus(operation, result, move ? "moved" : "copied");
        await DiagnosticLog.InformationAsync($"{operation} {result.Succeeded:n0} selected item(s). {result.Failures.Count:n0} failure(s).");
        return result;
    }

    private static string? GetAvailableDestinationPath(string sourcePath, string destinationFolder, bool move)
    {
        var filename = Path.GetFileName(sourcePath);
        var candidate = Path.Combine(destinationFolder, filename);
        if (move && string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(candidate)) return candidate;

        var baseName = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var copy = 1; ; copy++)
        {
            candidate = Path.Combine(destinationFolder, $"{baseName} ({copy}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string BatchStatus(string operation, BatchFileResult result, string successSuffix) =>
        result.Failures.Count == 0
            ? $"{operation} {result.Succeeded:n0} item(s): {successSuffix}."
            : $"{operation} {result.Succeeded:n0} item(s); {result.Failures.Count:n0} could not be completed.";

    private static FolderNode EnsureFolderNode(LibraryRoot root, string path, IDictionary<string, FolderNode> nodes)
    {
        var current = nodes[root.Path];
        var relative = Path.GetRelativePath(root.Path, path);
        if (relative == ".") return current;
        var currentPath = root.Path;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!nodes.TryGetValue(currentPath, out var child))
            {
                child = new FolderNode(currentPath, segment);
                nodes.Add(currentPath, child);
                current.Children.Add(child);
            }
            current = child;
        }
        return current;
    }

    private static FolderNode? FindParent(IReadOnlyDictionary<string, FolderNode> nodes, string path)
    {
        var parentPath = Path.GetDirectoryName(path);
        return parentPath is not null && nodes.TryGetValue(parentPath, out var parent) ? parent : null;
    }

    private static IEnumerable<string> EnumerateSubfolders(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] children;
            try { children = Directory.GetDirectories(current); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                yield return child;
                pending.Push(child);
            }
        }
    }

    private async Task RemoveMissingPhotosAsync(string root)
    {
        foreach (var path in await _database.GetPhotoPathsUnderRootAsync(root))
            if (!File.Exists(path)) await _database.DeleteByPathAsync(path);
    }
    public void Dispose()
    {
        _backgroundIndexCancellation.Cancel();
        _watcher.Dispose();
        _faceRecognition.Dispose();
    }

    private static FacePersonVector? FindClosestPerson(IEnumerable<FacePersonVector> people, float[] embedding, float threshold)
    {
        FacePersonVector? closest = null;
        var closestScore = threshold;
        foreach (var person in people)
        {
            if (person.Embedding.Length != embedding.Length) continue;
            var score = 0f;
            for (var index = 0; index < embedding.Length; index++) score += person.Embedding[index] * embedding[index];
            if (score <= closestScore) continue;
            closestScore = score;
            closest = person;
        }
        return closest;
    }

    private static FacePersonVector MergeEmbedding(FacePersonVector person, float[] embedding)
    {
        var merged = new float[embedding.Length];
        for (var index = 0; index < embedding.Length; index++)
            merged[index] = person.Embedding[index] * person.FaceCount + embedding[index];
        var magnitude = Math.Sqrt(merged.Sum(value => value * value));
        if (magnitude > double.Epsilon)
            for (var index = 0; index < merged.Length; index++) merged[index] = (float)(merged[index] / magnitude);
        return person with { Embedding = merged, FaceCount = person.FaceCount + 1 };
    }
}

public sealed class FolderNode(string path, string displayName)
{
    public string Path { get; } = path;
    public string DisplayName { get; } = displayName;
    public int Count { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
}

public sealed record BatchFileResult(int Succeeded, IReadOnlyList<string> Failures, IReadOnlyList<MediaTransfer> Transfers);

public sealed record MediaTransfer(PhotoRecord Source, string? Destination);

public sealed record DateGroup(DateTime Date, ObservableCollection<PhotoTile> Photos)
{
    public string Title => Date.ToString("MMM d, yyyy");
}

/// <summary>Lightweight presentation data. The timeline loads bitmap data only when a tile is visible.</summary>
public sealed class PhotoTile
{
    public PhotoTile(PhotoRecord photo) => Photo = photo;

    public PhotoRecord Photo { get; }
    public bool IsVideo => Photo.MediaKind == MediaKind.Video;
    public string DisplayName => Path.GetFileName(Photo.Path);
}
