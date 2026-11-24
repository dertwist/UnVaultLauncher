using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnVault.Core.Util;

namespace UnVault.Core.Cosmos;

/// <summary>
/// What a product's page on Cosmos tells of it (GET api.cosmos.leartesstudios.com/products/{slug}), as far as the Library
/// shows it. Cosmos gives it to anyone, as it does the page. Kinds of product differ in what they carry, and nothing says
/// which fields a kind has, so each is read on its own and left out when it isn't there or isn't what's expected.
/// </summary>
public sealed class CosmosProduct
{
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];

    /// <summary>Cosmos's own grouping ("asia", "realistic"); the Library's categories for its items.</summary>
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = [];

    /// <summary>The Unreal Engine versions it's made for, as "5.4"; none for a product that doesn't say.</summary>
    [JsonPropertyName("engineVersions")] public List<string> EngineVersions { get; set; } = [];

    /// <summary>Links to its pictures.</summary>
    [JsonPropertyName("images")] public List<string> Images { get; set; } = [];

    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>What it is, as Cosmos puts it: "prop".</summary>
    [JsonPropertyName("objectType")] public string? ObjectType { get; set; }

    /// <summary>Polygons at full detail.</summary>
    [JsonPropertyName("polygons")] public long? Polygons { get; set; }

    [JsonPropertyName("meshes")] public long? Meshes { get; set; }
    [JsonPropertyName("textures")] public long? Textures { get; set; }

    /// <summary>The sizes its textures come in, in pixels along a side.</summary>
    [JsonPropertyName("textureResolutions")] public List<string> TextureResolutions { get; set; } = [];

    /// <summary>The 3D file formats it comes in besides the engines' own.</summary>
    [JsonPropertyName("formats")] public List<string> Formats { get; set; } = [];

    /// <summary>What the pack includes, in the seller's words: its page's "Features".</summary>
    [JsonPropertyName("features")] public string? Features { get; set; }

    [JsonPropertyName("materialInstances")] public long? MaterialInstances { get; set; }

    /// <summary>Whether its meshes come with collision; null when Cosmos doesn't say.</summary>
    [JsonPropertyName("collision")] public bool? Collision { get; set; }

    /// <summary>What Cosmos calls its files, asked for by this name; its name in links when it has none of its own.</summary>
    [JsonPropertyName("folder")] public string? Folder { get; set; }

    /// <summary>The levels of detail a single asset comes in: "nanite", "lod0" …</summary>
    [JsonPropertyName("lods")] public List<string> Lods { get; set; } = [];

    /// <summary>The file formats a sound comes in.</summary>
    [JsonPropertyName("audioFormats")] public List<string> AudioFormats { get; set; } = [];

    /// <summary>What an environment's files come to, in GB, by the engine versions they're for.</summary>
    [JsonPropertyName("fileSizes")] public List<CosmosFileSize> FileSizes { get; set; } = [];

    /// <summary>What its files for an engine version come to; null when Cosmos doesn't say (it does for environments only).</summary>
    public long? SizeFor(string? engineVersion) =>
        (FileSizes.FirstOrDefault(f => engineVersion is not null && f.Versions.Contains(engineVersion)) ?? FileSizes.FirstOrDefault()) is { Gigabytes: > 0 } size
            ? (long)(size.Gigabytes * 1024 * 1024 * 1024)
            : null;

    /// <summary>When this copy was read; ours, not Cosmos's.</summary>
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }

    /// <summary>What was kept of the product when this copy was read (<see cref="CosmosProductCache.Fields"/>); ours, not Cosmos's.</summary>
    [JsonPropertyName("fields")] public int Fields { get; set; }

    /// <summary>Reads a product from Cosmos's answer. Anything missing or of another shape is left out rather than failing the rest.</summary>
    public static CosmosProduct Parse(JsonElement root)
    {
        var details = Part(root, "details");
        var unreal = Part(details, "ue"); // an environment's Unreal build; a single asset has these beside it instead
        return new CosmosProduct
        {
            Description = Text(root, "description") ?? Text(unreal, "description") ?? Text(root, "subtitle"),
            Tags = Texts(root, "tags"),
            Genres = Texts(details, "genres"),
            EngineVersions = [.. Texts(unreal, "versions").Where(v => Version.TryParse(v, out _))],
            Images = [.. Links(unreal).Concat(Links(details)).Distinct()],
            UpdatedAt = Text(root, "updatedAt") is { } updated && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            ObjectType = Text(details, "object_type"),
            Polygons = Number(Part(details, "polycount"), "lod0"),
            Meshes = Number(unreal, "mesh_numbers"),
            Textures = Number(unreal, "texture_number"),
            TextureResolutions = Texts(unreal, "texture_resolutions") is { Count: > 0 } resolutions ? resolutions : Texts(details, "texture_resolutions"),
            Formats = Texts(details, "threed_formats"),
            Features = Text(details, "features"),
            MaterialInstances = Number(unreal, "material_instances"),
            Collision = (Property(unreal, "collision") ?? Property(details, "collision")) switch
            {
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                _ => null,
            },
            Folder = Text(Part(root, "plugin"), "folder") ?? Text(details, "folder"),
            Lods = Texts(details, "lods"),
            AudioFormats = Texts(details, "format") is { Count: > 0 } formats ? formats : Text(details, "format") is { } format ? [format] : [],
            FileSizes = Property(unreal, "file_sizes") is { ValueKind: JsonValueKind.Array } sizes
                ? [.. sizes.EnumerateArray().Select(size => new CosmosFileSize
                    {
                        Gigabytes = Text(size, "size") is { } gigabytes && double.TryParse(gigabytes, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0,
                        Versions = Texts(size, "version") is { Count: > 0 } versions ? versions : Text(size, "version") is { } version ? [version] : [],
                    }).Where(size => size.Gigabytes > 0)]
                : [],
        };

        static List<string> Links(JsonElement? parent) =>
            Property(parent, "images") is { ValueKind: JsonValueKind.Array } list
                ? [.. list.EnumerateArray().Select(i => Text(i, "url")).OfType<string>().Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))]
                : [];
    }

    private static JsonElement? Property(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var value) ? value : null;

    private static JsonElement? Part(JsonElement? parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    /// <summary>A text or a number, as text; null for anything else.</summary>
    private static string? Text(JsonElement? parent, string name) => Property(parent, name) is { } value ? Text(value) : null;

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) => text.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    private static List<string> Texts(JsonElement? parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Array } list ? [.. list.EnumerateArray().Select(Text).OfType<string>()] : [];

    /// <summary>A whole number, written as one or as text ("8650").</summary>
    private static long? Number(JsonElement? parent, string name) =>
        Text(parent, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : null;
}

/// <summary>What an environment's files for some engine versions come to.</summary>
public sealed class CosmosFileSize
{
    [JsonPropertyName("gb")] public double Gigabytes { get; set; }
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];
}

/// <summary>
/// %LOCALAPPDATA%\UnVaultLauncher\cosmos-products.json: products' details by their name in links. Each takes a request of
/// its own, so they're kept and asked for again only now and then, as Fab's listings are (<see cref="Fab.FabListingCache"/>).
/// </summary>
public static class CosmosProductCache
{
    /// <summary>How long a copy is good for.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "cosmos-products.json");

    public static Dictionary<string, CosmosProduct> Load(string? path = null) =>
        SnapshotFile.Load(path ?? DefaultPath, CosmosJSONContext.Default.DictionaryStringCosmosProduct) ?? [];

    public static void Save(Dictionary<string, CosmosProduct> products, string? path = null) =>
        SnapshotFile.Save(path ?? DefaultPath, products, CosmosJSONContext.Default.DictionaryStringCosmosProduct);

    /// <summary>
    /// Goes up when more of a product is kept (2: what a download asks by, and what its files come to; 3: what it
    /// includes), so copies read before that are read again rather than used without it.
    /// </summary>
    public const int Fields = 3;

    /// <summary>Not read yet, long enough ago to ask again, or read when less of it was kept.</summary>
    public static bool IsDue(Dictionary<string, CosmosProduct> products, string slug) =>
        products.GetValueOrDefault(slug) is not { } have || DateTimeOffset.UtcNow - have.FetchedAt > MaxAge || have.Fields < Fields;
}
