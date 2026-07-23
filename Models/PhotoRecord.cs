namespace PicNest.Models;

public enum MediaKind
{
    Image,
    Video
}

public sealed record PhotoRecord(
    long Id,
    string Path,
    string FolderPath,
    DateTime DateTaken,
    string Hash,
    int Width,
    int Height,
    string ThumbnailPath,
    bool IsFavorite,
    MediaKind MediaKind = MediaKind.Image,
    long? DurationMilliseconds = null);
