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
    public string Status { get; private set; } = "Choose a folder to build your private library.";
    public event EventHandler? LibraryChanged;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await RefreshFolderTreeAsync();
        await RefreshAsync();
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        if (Folders.Count > 0) Status = "Watching your photo folders for changes.";
    }

    public async Task ScanAsync(string folder, IProgress<string>? progress = null)
    {
        await _database.InitializeAsync();
        await _database.AddRootAsync(folder);
        Status = "Scanning…";
        var indexed = await _indexer.IndexFolderAsync(folder, progress);
        await RemoveMissingPhotosAsync(folder);
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        _watcher.Watch((await _database.GetRootsAsync()).Select(root => root.Path));
        Status = $"{indexed:n0} photos indexed locally. Watching for changes.";
    }

    public async Task RefreshLibraryAsync(IProgress<string>? progress = null)
    {
        await _database.InitializeAsync();
        var roots = await _database.GetRootsAsync();
        var indexed = 0;
        foreach (var root in roots.Where(root => Directory.Exists(root.Path)))
        {
            progress?.Report($"Scanning {root.DisplayName}…");
            indexed += await _indexer.IndexFolderAsync(root.Path, progress);
            await RemoveMissingPhotosAsync(root.Path);
        }
        await RefreshFolderTreeAsync();
        await RefreshAsync(_searchText);
        Status = $"Folders refreshed. {indexed:n0} photos checked.";
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
            Groups.Add(new DateGroup(group.Key, new ObservableCollection<PhotoRecord>(group)));
    }

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
    }

    private async Task HandleFileChangeAsync(string path, WatcherChangeTypes change)
    {
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

public sealed record DateGroup(DateTime Date, ObservableCollection<PhotoRecord> Photos)
{
    public string Title => Date.ToString("MMM d, yyyy");
}
