namespace PicNest.Models;

public sealed record PhotoRecord(
    long Id,
    string Path,
    string FolderPath,
    DateTime DateTaken,
    string Hash,
    int Width,
    int Height,
    string ThumbnailPath,
    bool IsFavorite);
