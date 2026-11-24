using System.Security.AccessControl;
using System.Security.Principal;
using UnVault.Core.Util;

namespace UnVault.Core.Tests;

/// <summary>
/// Whether a folder can be written in, asked before an install starts: C:\Program Files\Epic Games, where the Epic Games
/// Launcher puts engines, lets only administrators write, and an install there used to fail at its first file.
/// </summary>
public sealed class FolderAccessTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-access-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Allow(_folder);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_folder_of_ones_own_can_be_written_in_and_is_left_as_it_was()
    {
        Assert.True(FolderAccess.CanWriteUnder(_folder));
        Assert.True(FolderAccess.CanWriteUnder(Path.Combine(_folder, "Epic Games", "UE_5.9"))); // not there yet: the folder above it answers
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public void A_folder_only_others_may_change_cant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows access rules.");
        Deny(_folder);

        Assert.False(FolderAccess.CanWriteUnder(_folder));
        Assert.False(FolderAccess.CanWriteUnder(Path.Combine(_folder, "UE_5.9", ".unvault"))); // as the install would make it
    }

    [Fact]
    public void A_drive_that_isnt_there_is_left_for_the_install_to_report()
    {
        string? missing = "ZYXWVUTSRQPONMLKJIHGFED".Select(letter => $@"{letter}:\").FirstOrDefault(root => !Directory.Exists(root));
        Assert.SkipWhen(missing is null || !OperatingSystem.IsWindows(), "Needs a drive letter nothing is on.");

        Assert.True(FolderAccess.CanWriteUnder(Path.Combine(missing!, "Epic Games", "UE_5.9")));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSystemAccessRule NoWriting =>
        new(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.Write, AccessControlType.Deny);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Deny(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var rules = directory.GetAccessControl();
        rules.AddAccessRule(NoWriting);
        directory.SetAccessControl(rules);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Allow(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var rules = directory.GetAccessControl();
        rules.RemoveAccessRule(NoWriting);
        directory.SetAccessControl(rules);
    }
}
