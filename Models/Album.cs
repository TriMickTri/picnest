namespace PicNest.Models;

/// <summary>A named, local PicNest collection. It contains references to source files, never copies.</summary>
public sealed record Album(long Id, string Name, int PhotoCount);
