namespace PicNest.Services;

/// <summary>Watches chosen library roots; it never watches the whole drive or changes source files.</summary>
public sealed class FolderWatchService(Func<string, WatcherChangeTypes, Task> onChange) : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public void Watch(IEnumerable<string> roots)
    {
        var wanted = roots.Where(Directory.Exists).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in _watchers.Keys.Where(path => !wanted.Contains(path)).ToArray())
        {
            _watchers[obsolete].Dispose();
            _watchers.Remove(obsolete);
        }
        foreach (var root in wanted.Where(path => !_watchers.ContainsKey(path))) AddWatcher(root);
    }

    private void AddWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, e) => Queue(e.FullPath, e.ChangeType);
        watcher.Changed += (_, e) => Queue(e.FullPath, e.ChangeType);
        watcher.Deleted += (_, e) => Queue(e.FullPath, e.ChangeType);
        watcher.Renamed += (_, e) => { Queue(e.OldFullPath, WatcherChangeTypes.Deleted); Queue(e.FullPath, WatcherChangeTypes.Created); };
        _watchers.Add(root, watcher);
    }

    private async void Queue(string path, WatcherChangeTypes change)
    {
        // Camera imports commonly produce several events while a file is still being written.
        await Task.Delay(350);
        try { await onChange(path, change); }
        catch { /* A later watcher event or the next scan will retry a transient file-system failure. */ }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
