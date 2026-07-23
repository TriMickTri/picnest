using System.Collections.ObjectModel;
using PicNest.Models;
using PicNest.Services;

namespace PicNest.ViewModels;

public sealed class LibraryViewModel : IDisposable
{
    private readonly LibraryDatabase _database = new();
    private readonly LibraryIndexer _indexer;
    private readonly FolderWatchService _watcher;
    private string _searchText = "";
    private string? _selectedFolderPath;

    public LibraryViewModel()
    {
        _indexer = new LibraryIndexer(_database);
        _watcher = new FolderWatchService(HandleFileChangeAsync);
    }

    public ObservableCollection<DateGroup> Groups { get; } = [];
    public ObservableCollection<FolderNode> Folders { get; } = [];
    public ObservableCollection<LibraryRoot> ImportedFolders { get; } = [];
    public string Status { get; private set; } = "Choose a folder to build your private library.";
    public event EventHandler? LibraryChanged;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await RefreshImportedFoldersAsync();
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
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        Status = $"{indexed:n0} photos indexed locally. Watching for changes.";
    }

    public async Task RefreshImportedFoldersAsync()
    {
        var roots = await _database.GetRootsAsync();
        ImportedFolders.Clear();
        foreach (var root in roots) ImportedFolders.Add(root);
    }

    public async Task RemoveImportedFolderAsync(LibraryRoot root)
    {
        await DiagnosticLog.InformationAsync($"Removing imported folder from PicNest catalog: {root.Path}");
        var removedPhotos = await _database.RemoveRootAsync(root.Path);
        if (_selectedFolderPath is not null && IsWithin(_selectedFolderPath, root.Path)) _selectedFolderPath = null;
        await RefreshImportedFoldersAsync();
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
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        Status = removedRoots == 0
            ? $"Folders refreshed. {indexed:n0} photos checked."
            : $"Folders refreshed. {indexed:n0} photos checked; removed {removedRoots:n0} missing folder(s) and {removedPhotos:n0} catalog photo(s).";
    }

    public async Task SelectFolderAsync(string? folderPath)
    {
        _selectedFolderPath = folderPath;
        await RefreshAsync(_searchText);
    }

    public async Task RefreshAsync(string searchText = "")
    {
        await _database.InitializeAsync();
        _searchText = searchText;
        var records = await _database.SearchAsync(_searchText, _selectedFolderPath);
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

        if (!LibraryIndexer.IsSupportedImage(path))
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
                await DiagnosticLog.InformationAsync($"New photo found: {path}");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { await _indexer.IndexImageAsync(path); break; }
                catch (IOException) when (attempt < 2) { await Task.Delay(400); }
            }
        }
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(string folder, string root) => folder.Equals(root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
    public void Dispose() => _watcher.Dispose();
}

public sealed class FolderNode(string path, string displayName)
{
    public string Path { get; } = path;
    public string DisplayName { get; } = displayName;
    public int Count { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
}

public sealed record DateGroup(DateTime Date, ObservableCollection<PhotoTile> Photos)
{
    public string Title => Date.ToString("MMM d, yyyy");
}

/// <summary>Lightweight presentation data. The timeline loads bitmap data only when a tile is visible.</summary>
public sealed class PhotoTile
{
    public PhotoTile(PhotoRecord photo) => Photo = photo;

    public PhotoRecord Photo { get; }
    public string DisplayName => Path.GetFileName(Photo.Path);
}
