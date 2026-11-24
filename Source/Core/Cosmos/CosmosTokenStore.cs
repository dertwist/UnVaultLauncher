using System.Security.Cryptography;
using System.Text;

namespace UnVault.Core.Cosmos;

/// <summary>
/// Keeps the Cosmos sign-in token as the Epic session is kept (<see cref="Epic.SessionStore"/>): on Windows encrypted with
/// DPAPI for the current Windows user, elsewhere a 0600 file.
/// </summary>
public sealed class CosmosTokenStore(string path)
{
    // Ties the DPAPI blob to this app; not a secret.
    private static readonly byte[] Entropy = "Unvault.CosmosToken.v1"u8.ToArray();

    public static CosmosTokenStore Default { get; } = new(Path.Combine(AppPaths.DataDirectory, "cosmos.dat"));

    /// <summary>The saved token; null when signed out, or when the file can't be read (another user's, damaged).</summary>
    public string? Load()
    {
        if (!File.Exists(path))
            return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
                data = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            string token = Encoding.UTF8.GetString(data);
            return token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] data = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
