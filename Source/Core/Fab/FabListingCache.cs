using UnVault.Core.Epic;
using UnVault.Core.Util;

namespace UnVault.Core.Fab;

/// <summary>
/// %LOCALAPPDATA%\UnVaultLauncher\fab-listings.json: listings' details by listing ID. Each takes a request of its own, so
/// they're kept and asked for again only now and then. Nothing of the account's: it stays through a sign-out.
/// </summary>
public static class FabListingCache
{
    /// <summary>How long a copy is good for: a listing's version and tags change, but rarely.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "fab-listings.json");

    public static Dictionary<string, FabListing> Load(string? path = null) =>
        SnapshotFile.Load(path ?? DefaultPath, FabJSONContext.Default.DictionaryStringFabListing) ?? [];

    public static void Save(Dictionary<string, FabListing> listings, string? path = null) =>
        SnapshotFile.Save(path ?? DefaultPath, listings, FabJSONContext.Default.DictionaryStringFabListing);

    /// <summary>
    /// Goes up when more of a listing is kept (2: its gallery, rating and first release), so copies read before that are
    /// read again rather than shown without it.
    /// </summary>
    public const int Fields = 2;

    /// <summary>Not read yet, long enough ago to ask again, or read when less of it was kept.</summary>
    public static bool IsDue(Dictionary<string, FabListing> listings, string listingID) =>
        listings.GetValueOrDefault(listingID) is not { } have || DateTimeOffset.UtcNow - have.FetchedAt > MaxAge || have.Fields < Fields;
}
