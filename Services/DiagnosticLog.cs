using System.Runtime.CompilerServices;
using System.Text;

namespace PicNest.Services;

public enum DiagnosticLevel
{
    Debug,
    Information,
    Warning,
    Error
}

/// <summary>Small, dependency-free diagnostic log for local troubleshooting.</summary>
public static class DiagnosticLog
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static Task InformationAsync(
        string text,
        [CallerMemberName] string function = "",
        [CallerLineNumber] int line = 0,
        [CallerFilePath] string sourceFile = "") =>
        WriteAsync(DiagnosticLevel.Information, text, function, line, sourceFile);

    public static Task WarningAsync(
        string text,
        [CallerMemberName] string function = "",
        [CallerLineNumber] int line = 0,
        [CallerFilePath] string sourceFile = "") =>
        WriteAsync(DiagnosticLevel.Warning, text, function, line, sourceFile);

    private static async Task WriteAsync(DiagnosticLevel level, string text, string function, int line, string sourceFile)
    {
        try
        {
            LibraryPaths.EnsureCreated();
            var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} - {level}\n" +
                        $"Function: {Path.GetFileName(sourceFile)}.{function} (line {line})\n" +
                        $"{text}{Environment.NewLine}{Environment.NewLine}";
            await WriteLock.WaitAsync();
            try { await File.AppendAllTextAsync(LibraryPaths.DiagnosticLogFile, entry, Encoding.UTF8); }
            finally { WriteLock.Release(); }
        }
        catch
        {
            // Diagnostics must never prevent photo indexing or browsing.
        }
    }
}
