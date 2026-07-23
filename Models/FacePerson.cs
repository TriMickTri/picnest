namespace PicNest.Models;

/// <summary>
/// A local-only person group. PicNest learns a visual grouping first; a name is added only
/// when the library owner supplies one.
/// </summary>
public sealed record FacePerson(long Id, string? Name, int PhotoCount, int FaceCount, string RepresentativeFacePath)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Person {Id}" : Name;
    public string Summary => $"{PhotoCount:n0} photo(s) · {FaceCount:n0} face(s)";
}

internal sealed record FacePersonVector(long Id, float[] Embedding, int FaceCount, string RepresentativeFacePath);

internal sealed record FaceDetection(float Confidence, float[] Embedding, string PreviewPath);

internal sealed record FaceAssignment(long PersonId, FaceDetection Detection);

public sealed record FaceScanProgress(int Completed, int Total, string CurrentFile);

public sealed record FaceScanSummary(int PhotosScanned, int FacesFound, int NewPeople, int Failures)
{
    public bool WasAlreadyCurrent => PhotosScanned == 0 && Failures == 0;
}
