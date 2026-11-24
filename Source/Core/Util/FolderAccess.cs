namespace UnVault.Core.Util;

/// <summary>
/// Whether this process may write in a folder. Asked before an install starts: C:\Program Files\Epic Games, where the Epic
/// Games Launcher puts engines, lets only administrators write, and UnVault isn't run as one.
/// </summary>
public static class FolderAccess
{
    /// <summary>
    /// Whether files can be made in a folder; when it isn't there yet, in the nearest folder above it that is, as that's
    /// where it would be made. Found out by making an empty folder there and removing it: nothing else tells for sure.
    /// True when there's no saying (a drive that isn't there, a path that isn't one): the install reports those itself.
    /// </summary>
    public static bool CanWriteUnder(string path)
    {
        try
        {
            string? folder = Path.GetFullPath(path);
            while (folder is not null && !Directory.Exists(folder))
                folder = Path.GetDirectoryName(folder);
            if (folder is null)
                return true;

            string probe = Path.Combine(folder, $".unvault-{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
