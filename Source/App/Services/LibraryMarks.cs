using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core;
using UnVault.Core.Util;

namespace UnVault.App.Services;

/// <summary>
/// What the user marked in the Library, and when each item first showed in it, kept in
/// %LOCALAPPDATA%\UnVaultLauncher\library-marks.json by item key. The user's own, so it stays through a sign-out.
/// </summary>
public sealed class LibraryMarks
{
    public HashSet<string> Favorites { get; set; } = [];

    /// <summary>Left out of the Library unless hidden items are asked for.</summary>
    public HashSet<string> Hidden { get; set; } = [];

    /// <summary>When the library first listed each item, as Fab doesn't say when it was acquired.</summary>
    public Dictionary<string, DateTimeOffset> FirstSeen { get; set; } = [];

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "library-marks.json");

    /// <summary>The file is there but couldn't be read: these marks start empty, and are never saved over it.</summary>
    private bool KeepsFile { get; init; }

    /// <summary>
    /// The saved marks; none when there are none yet. As with <see cref="ProjectCategories"/>, a damaged file is set aside as
    /// {file}.bad rather than saved over, and one that can't be read now is left as it is.
    /// </summary>
    public static LibraryMarks Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path))
            return new();
        try
        {
            var marks = JsonSerializer.Deserialize(File.ReadAllText(path), LibraryMarksJSONContext.Default.LibraryMarks)
                ?? throw new JsonException("The file holds no marks.");
            // Edited by hand, a list may be left out.
            marks.Favorites ??= [];
            marks.Hidden ??= [];
            marks.FirstSeen ??= [];
            return marks;
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, path + ".bad", overwrite: true);
                return new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new() { KeepsFile = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new() { KeepsFile = true };
        }
    }

    /// <summary>Marks that can't be saved stay for this session; the next change tries again.</summary>
    public void Save(string? path = null)
    {
        if (KeepsFile)
            return;
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, LibraryMarksJSONContext.Default.LibraryMarks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next change.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LibraryMarks))]
internal sealed partial class LibraryMarksJSONContext : JsonSerializerContext;
