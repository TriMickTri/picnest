namespace PicNest.Models;

public sealed record LibraryRoot(string Path, string DisplayName);

public sealed record FolderSummary(string Path, int PhotoCount);
