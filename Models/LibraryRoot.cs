namespace PicNest.Models;

public sealed record LibraryRoot(string Path, string DisplayName, bool IsProtected = false)
{
    public string ProtectionLabel => IsProtected ? "Protected" : "Standard";
}

public sealed record FolderSummary(string Path, int PhotoCount);
