using System.Security.Cryptography;

namespace PicNest.Services;

/// <summary>
/// Stores a verifier for PicNest's privacy lock. It intentionally does not claim to encrypt the
/// original files; it only controls what PicNest shows in its own interface.
/// </summary>
public static class ProtectedFolderSecurity
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool HasPassword =>
        !string.IsNullOrWhiteSpace(PreferencesStore.Current.ProtectedPasswordSalt) &&
        !string.IsNullOrWhiteSpace(PreferencesStore.Current.ProtectedPasswordHash);

    public static async Task SetPasswordAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ArgumentException("Use a password with at least four characters.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = DeriveHash(password, salt);
        var current = PreferencesStore.Current;
        await PreferencesStore.SaveAsync(new AppPreferences
        {
            Theme = current.Theme,
            DiagnosticDirectory = current.DiagnosticDirectory,
            ThumbnailDirectory = current.ThumbnailDirectory,
            SlideshowSeconds = current.SlideshowSeconds,
            SlideshowTransition = current.SlideshowTransition,
            SlideshowIncludeVideos = current.SlideshowIncludeVideos,
            ProtectedPasswordSalt = Convert.ToBase64String(salt),
            ProtectedPasswordHash = Convert.ToBase64String(hash)
        });
    }

    public static bool Verify(string password)
    {
        if (!HasPassword) return false;
        try
        {
            var salt = Convert.FromBase64String(PreferencesStore.Current.ProtectedPasswordSalt!);
            var expectedHash = Convert.FromBase64String(PreferencesStore.Current.ProtectedPasswordHash!);
            var actualHash = DeriveHash(password, salt);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DeriveHash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
