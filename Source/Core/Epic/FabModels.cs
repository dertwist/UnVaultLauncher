using System.Text.Json.Serialization;

namespace UnVault.Core.Epic;

/// <summary>One page of GET fab.com/e/accounts/{accountId}/ue/library.</summary>
internal sealed class FabLibraryPage
{
    [JsonPropertyName("cursors")] public FabCursors? Cursors { get; set; }
    [JsonPropertyName("results")] public List<FabLibraryItem>? Results { get; set; }
}

internal sealed class FabCursors
{
    [JsonPropertyName("next")] public string? Next { get; set; }
}

/// <summary>Something in the account's Fab library usable from Unreal Engine (plugin, asset pack, project).</summary>
public sealed class FabLibraryItem
{
    [JsonPropertyName("assetId")] public string AssetID { get; set; } = "";
    [JsonPropertyName("assetNamespace")] public string AssetNamespace { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("seller")] public string? Seller { get; set; }

    /// <summary>e.g. "asset-pack", "complete_project"; plugins have their own value.</summary>
    [JsonPropertyName("distributionMethod")] public string? DistributionMethod { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("url")] public string? URL { get; set; }
    [JsonPropertyName("categories")] public List<FabCategory>? Categories { get; set; }
    [JsonPropertyName("images")] public List<FabImage>? Images { get; set; }

    /// <summary>
    /// A small preview image: the narrowest one at least 300 px wide (or the widest if all are smaller). Web pictures
    /// only: a local:// link would have the app read whatever file the server named.
    /// </summary>
    [JsonIgnore]
    public FabImage? ThumbnailImage =>
        (Images ?? []).Where(i => i.URL?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) // JSON can give a null
            .OrderBy(i => i.Width >= 300 ? i.Width : int.MaxValue - i.Width).FirstOrDefault();

    [JsonIgnore] public string? ThumbnailURL => ThumbnailImage?.URL;

    /// <summary>One downloadable artifact per supported engine version (e.g. MyPlugin_5.4, MyPlugin_5.5).</summary>
    [JsonPropertyName("projectVersions")] public List<FabProjectVersion>? ProjectVersions { get; set; }

    [JsonIgnore]
    public IEnumerable<string> EngineVersions =>
        (ProjectVersions ?? []).SelectMany(v => v.EngineVersions ?? []).Distinct();

    /// <summary>The listing on fab.com this came from: the end of its link, when that's an ID (old Marketplace items have none).</summary>
    [JsonIgnore]
    public string? ListingID => URL?[(URL.LastIndexOf('/') + 1)..] is { } last && Guid.TryParse(last, out _) ? last : null;
}

/// <summary>GET fab.com/i/listings/{id}: what a listing's page shows and the library leaves out. Public, like the page.</summary>
public sealed class FabListing
{
    /// <summary>What Fab sells it as, e.g. "3d-model".</summary>
    [JsonPropertyName("listingType")] public string? ListingType { get; set; }

    [JsonPropertyName("tags")] public List<FabTag>? Tags { get; set; }

    /// <summary>When its current version came out.</summary>
    [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When it first came out.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Buyers' rating, out of 5; null while nobody rated it.</summary>
    [JsonPropertyName("averageRating")] public double? AverageRating { get; set; }

    [JsonPropertyName("reviewCount")] public int? ReviewCount { get; set; }

    /// <summary>The listing's gallery: its pictures, and videos, which aren't shown.</summary>
    [JsonPropertyName("medias")] public List<FabMedia>? Medias { get; set; }

    /// <summary>Links to the gallery's pictures, in its order.</summary>
    [JsonIgnore]
    public IEnumerable<string> Pictures =>
        (Medias ?? []).Where(m => m?.Type == "image" && m.MediaURL?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true).Select(m => m.MediaURL!);

    /// <summary>
    /// What its page shows under "Technical details", as plain text (<see cref="Fab.FabTechnicalDetails"/>). A request of its
    /// own, made when the item's preview first shows: null until then, empty when Fab has none. Ours, not Fab's.
    /// </summary>
    [JsonPropertyName("technicalDetails")] public string? TechnicalDetails { get; set; }

    /// <summary>When this copy was read; ours, not Fab's.</summary>
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }

    /// <summary>What was kept of the listing when this copy was read (<see cref="Fab.FabListingCache.Fields"/>); ours, not Fab's.</summary>
    [JsonPropertyName("fields")] public int Fields { get; set; }
}

public sealed class FabTag
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class FabMedia
{
    /// <summary>"image" or "video".</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("mediaUrl")] public string? MediaURL { get; set; }
}

public sealed class FabImage
{
    [JsonPropertyName("url")] public string URL { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("uploadedDate")] public DateTimeOffset? UploadedDate { get; set; }
}

public sealed class FabCategory
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class FabProjectVersion
{
    [JsonPropertyName("artifactId")] public string ArtifactID { get; set; } = "";
    [JsonPropertyName("engineVersions")] public List<string>? EngineVersions { get; set; }
    [JsonPropertyName("targetPlatforms")] public List<string>? TargetPlatforms { get; set; }
    [JsonPropertyName("buildVersions")] public List<FabBuildVersion>? BuildVersions { get; set; }
}

public sealed class FabBuildVersion
{
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

internal sealed class FabArtifactManifestRequest
{
    [JsonPropertyName("item_id")] public string ItemID { get; set; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
}

internal sealed class FabArtifactManifestResponse
{
    [JsonPropertyName("downloadInfo")] public List<FabDownloadInfo>? DownloadInfo { get; set; }
}

/// <summary>Where to get one Fab artifact's build manifest. The URLs are signed and expire.</summary>
public sealed class FabDownloadInfo
{
    [JsonPropertyName("artifactId")] public string ArtifactID { get; set; } = "";
    [JsonPropertyName("assetFormat")] public string? AssetFormat { get; set; }
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>SHA-1 (hex) of the manifest file.</summary>
    [JsonPropertyName("manifestHash")] public string? ManifestHash { get; set; }

    [JsonPropertyName("distributionPoints")] public List<FabDistributionPoint>? DistributionPoints { get; set; }
}

public sealed class FabDistributionPoint
{
    [JsonPropertyName("manifestUrl")] public string ManifestURL { get; set; } = "";
    [JsonPropertyName("signatureExpiration")] public DateTimeOffset SignatureExpiration { get; set; }
}

// Fab sends some numbers as strings ("width": "640").
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FabLibraryPage))]
[JsonSerializable(typeof(FabArtifactManifestRequest))]
[JsonSerializable(typeof(FabArtifactManifestResponse))]
[JsonSerializable(typeof(Fab.FabLibrarySnapshot))]
[JsonSerializable(typeof(Dictionary<string, FabListing>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
internal sealed partial class FabJSONContext : JsonSerializerContext;
