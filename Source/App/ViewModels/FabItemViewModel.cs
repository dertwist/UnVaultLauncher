using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core.Cosmos;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Util;
using UnVault.Core.Vault;

namespace UnVault.App.ViewModels;

/// <summary>One engine version of a Fab item: its artifact, from the online library and/or the vault cache.</summary>
public sealed record FabVersion(string EngineAppName, string ArtifactID, FabProjectVersion? Library, VaultEntry? Vault)
{
    public bool IsDownloaded => Vault is { IsComplete: true };

    /// <summary>The Windows build Fab currently offers for this artifact.</summary>
    public string? LatestBuild => Library?.BuildVersions?.FirstOrDefault(b => b.Platform == "Windows")?.BuildVersion;

    /// <summary>Downloaded, but Fab has another (newer) build than the Vault Cache copy.</summary>
    public bool IsOutdated => IsDownloaded && IsOlder(Vault!.Build, LatestBuild);

    /// <summary>Fab's build is the current one, so any different build here counts as older.</summary>
    internal static bool IsOlder(string? have, string? latest) =>
        !string.IsNullOrEmpty(have) && !string.IsNullOrEmpty(latest) && !string.Equals(have, latest, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A plugin installed into an engine: where, in which folder, who put it there, and which build it is (if known).</summary>
public sealed record FabInstall(string EngineAppName, string EngineDirectory, string ArtifactID, PluginSource Source, string Folder, bool CanRemove,
    string? BuildVersion = null);

/// <summary>A line of an item's preview: what it tells, and that of this item.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>One picture of an item's gallery in its preview, small; held from the cache for as long as the preview shows it.</summary>
public sealed partial class PreviewShot : ViewModelBase
{
    private ThumbnailCache.Use? _use;

    /// <param name="key">What the picture belongs to, when its link changes (an item's own picture); else the link.</param>
    public PreviewShot(string url, ThumbnailCache thumbnails, string? key = null, DateTimeOffset? uploaded = null)
    {
        URL = url;
        Key = key ?? url;
        Uploaded = uploaded;
        _use = thumbnails.Get(url, Key, uploaded);
    }

    public string URL { get; }
    public string Key { get; }
    public DateTimeOffset? Uploaded { get; }

    [ObservableProperty] public partial Bitmap? Picture { get; private set; }

    /// <summary>The one shown large above the strip.</summary>
    [ObservableProperty] public partial bool IsCurrent { get; set; }

    internal async Task LoadAsync()
    {
        if (_use is not { } use)
            return;
        var bitmap = await use.Picture;
        if (_use == use) // not given back meanwhile
            Picture = bitmap;
    }

    internal void Release(ThumbnailCache thumbnails)
    {
        if (_use is not { } use)
            return;
        _use = null;
        Picture = null; // first, as the cache may free the picture once it's back
        thumbnails.Release(use);
    }
}

/// <summary>One row in the Fab library: a product with all its per-engine versions.</summary>
public partial class FabItemViewModel : ViewModelBase
{
    private readonly FabLibraryViewModel _owner;
    private readonly string? _thumbnailURL;
    private int _viewers;
    private Services.ThumbnailCache.Use? _thumbnailUse; // the picture held for the tiles showing this item
    private Services.ThumbnailCache.Use? _pictureUse; // the larger one held for the preview

    public FabItemViewModel(FabLibraryViewModel owner, string key, string title, FabItemKind kind, FabLibraryItem? library,
        IReadOnlyList<FabVersion> versions, IReadOnlyList<FabInstall> installs, string? thumbnailURL)
    {
        _owner = owner;
        Key = key;
        Title = title;
        _kind = kind;
        Library = library;
        _versions = versions;
        Installs = installs;
        _thumbnailURL = thumbnailURL;
        SearchText = $"{title} {library?.Seller} {library?.Description} {string.Join(' ', (library?.Categories ?? []).Select(c => c.Name))}";
        _category = library?.Categories?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Name))?.Name;
        // Of the Vault Cache copy, until the preview asks Fab what the files for its newest engine come to.
        long downloaded = versions.Where(v => v.IsDownloaded).Select(v => v.Vault!.Size).DefaultIfEmpty().Max();
        Size = downloaded > 0 ? downloaded : null;
        FirstSeen = owner.Marks.FirstSeen.GetValueOrDefault(key);
        IsFavorite = owner.Marks.Favorites.Contains(key);
        IsHidden = owner.Marks.Hidden.Contains(key);

        // Versions can repeat an artifact (one per engine it supports); updates are per artifact.
        var latest = versions.Where(v => v.LatestBuild is not null).GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LatestBuild, StringComparer.OrdinalIgnoreCase);
        OutdatedDownloads = [.. versions.Where(v => v.IsOutdated).GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
        // Only installs UnVault can bring up to date: its own, or EGL's in their own Marketplace folder.
        OutdatedInstalls = [.. installs.Where(i => i.CanRemove && FabVersion.IsOlder(i.BuildVersion, latest.GetValueOrDefault(i.ArtifactID)))];
    }

    /// <summary>Identifies the product across reloads; titles aren't unique (two sellers' "Garden Pack").</summary>
    public string Key { get; }

    public string Title { get; }
    private readonly FabItemKind _kind;
    private readonly IReadOnlyList<FabVersion> _versions;
    private (CosmosProduct? Product, IReadOnlyList<FabVersion> Versions) _cosmosVersions = (null, []);

    /// <summary>
    /// What it is. A Cosmos item is an asset pack once Cosmos has told which engines it's for: its files are a project's,
    /// so it goes into projects, or becomes one, as Fab's do. Until then, and for one that doesn't say, it's only downloaded.
    /// </summary>
    public FabItemKind Kind => Cosmos is null ? _kind : Versions.Count > 0 ? FabItemKind.AssetPack : FabItemKind.Unknown;

    public FabLibraryItem? Library { get; }

    /// <summary>Its copies by engine version. A Cosmos item's are the versions Cosmos lists, one download each.</summary>
    public IReadOnlyList<FabVersion> Versions
    {
        get
        {
            if (Cosmos is not { } cosmos)
                return _versions;
            var product = Product;
            if (!ReferenceEquals(product, _cosmosVersions.Product))
                _cosmosVersions = (product, [.. (product?.EngineVersions ?? []).Select(v => new FabVersion("UE_" + v, $"cosmos:{cosmos.Slug}:{v}", null, null))]);
            return _cosmosVersions.Versions;
        }
    }
    public IReadOnlyList<FabInstall> Installs { get; }
    public string SearchText { get; }

    /// <summary>
    /// Set for an item from Leartes' Cosmos store rather than Fab. Nothing of it is downloaded or installed here: it's
    /// listed, found and filtered with the rest, and opened on Cosmos.
    /// </summary>
    public CosmosItem? Cosmos { get; init; }

    /// <summary>A Cosmos item's file in the Cosmos folder, once downloaded.</summary>
    public CosmosFile? CosmosFile { get; init; }

    public string KindLabel => Kind switch
    {
        FabItemKind.Plugin => Strings.KindPlugin,
        FabItemKind.AssetPack => Strings.KindAssetPack,
        FabItemKind.Project => Strings.KindProject,
        _ => Cosmos is null ? Strings.KindOther : Strings.SourceCosmos,
    };

    public string? SellerText =>
        !string.IsNullOrWhiteSpace(Library?.Seller) ? Library.Seller : string.IsNullOrWhiteSpace(Cosmos?.Service) ? null : Cosmos.Service;

    private readonly string? _category;

    /// <summary>Fab's category for it; the library gives each item one. For a Cosmos item, the first of Cosmos's genres, once read.</summary>
    public string? Category => _category ?? Product?.Genres.FirstOrDefault();

    /// <summary>A Cosmos item's details, once read from Cosmos (see <see cref="FabLibraryViewModel.ReadProductsAsync"/>).</summary>
    private CosmosProduct? Product => Cosmos is { } cosmos ? _owner.Products.GetValueOrDefault(cosmos.Slug) : null;

    /// <summary>The engines it's for, by app name ("UE_5.7"): its versions', or what Cosmos lists.</summary>
    public IEnumerable<string> EngineAppNames => Versions.Select(v => v.EngineAppName);

    /// <summary>When the library first listed it; the earliest date for one seen only in the Vault Cache.</summary>
    public DateTimeOffset FirstSeen { get; }

    /// <summary>Its place in the library as Fab lists it, which is by when it was got, first to last; -1 when not in the library.</summary>
    public int LibraryOrder { get; init; } = -1;

    /// <summary>Its listing's details, once read from Fab (see <see cref="FabLibraryViewModel.ReadListingsAsync"/>).</summary>
    private FabListing? Listing => Library?.ListingID is { } id ? _owner.Listings.GetValueOrDefault(id) : null;

    /// <summary>What Fab sells it as, in words: "3d-model" → "3d model". For a Cosmos item, what Cosmos does.</summary>
    public string? ListingType => (Listing?.ListingType ?? Cosmos?.Type)?.Replace('-', ' ');

    /// <summary>When its current version came out on Fab; for a Cosmos item, when Cosmos last changed it.</summary>
    public DateTimeOffset? UpdatedAt => Listing?.PublishedAt ?? Product?.UpdatedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteText))]
    public partial bool IsFavorite { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenText))]
    public partial bool IsHidden { get; private set; }

    public string FavoriteText => IsFavorite ? Strings.RemoveFromFavorites : Strings.AddToFavorites;
    public string HiddenText => IsHidden ? Strings.ShowInLibrary : Strings.HideFromLibrary;

    /// <summary>What the preview lists under the title; only what's known of the item.</summary>
    public IReadOnlyList<DetailRow> Details
    {
        get
        {
            var listing = Listing;
            var product = Product;
            return
            [
                .. new DetailRow?[]
                {
                    new(Strings.DetailType, KindLabel),
                    Row(Strings.DetailObjectType, product?.ObjectType),
                    Row(Strings.DetailCategory, Category),
                    Row(Strings.DetailListingType, ListingType),
                    Row(Strings.DetailEngines, Cosmos is null || EngineAppNames.Any() ? EngineVersionsText : null),
                    Row(Strings.DetailSize, (Size ?? CosmosFile?.Size ?? product?.SizeFor(product.EngineVersions.LastOrDefault())) is { } size ? ByteSize.Format(size) : null),
                    Row(Strings.DetailRating, listing is { AverageRating: > 0 }
                        ? Localized.Format(Strings.DetailRatingValue, listing.AverageRating, listing.ReviewCount ?? 0) : null),
                    Row(Strings.DetailReleased, Date(listing?.CreatedAt)),
                    Row(Strings.DetailUpdated, Date(UpdatedAt)),
                    Row(Strings.DetailPolygons, product?.Polygons?.ToString("N0", CultureInfo.CurrentCulture)),
                    Row(Strings.DetailMeshes, product?.Meshes?.ToString("N0", CultureInfo.CurrentCulture)),
                    Row(Strings.DetailTextures, product?.Textures?.ToString("N0", CultureInfo.CurrentCulture)),
                    Row(Strings.DetailMaterialInstances, product?.MaterialInstances?.ToString("N0", CultureInfo.CurrentCulture)),
                    Row(Strings.DetailTextureSizes, string.Join(", ", product?.TextureResolutions ?? [])),
                    Row(Strings.DetailCollision, product?.Collision is { } collision ? (collision ? Strings.Yes : Strings.No) : null),
                    Row(Strings.DetailFormats, string.Join(", ", product?.Formats ?? [])),
                }.OfType<DetailRow>(),
            ];

            static DetailRow? Row(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(label, value);
            static string? Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// What its files come to: for its newest engine version, once the preview has asked Fab; until then of the Vault Cache
    /// copy, if there is one. Neither store tells a size otherwise, and Cosmos tells none at all.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial long? Size { get; private set; }

    /// <summary>The listing's tags, once read from Fab or Cosmos.</summary>
    public IReadOnlyList<string> Tags =>
        [.. (Listing?.Tags ?? []).Select(t => t.Name).Concat(Product?.Tags ?? []).Where(n => !string.IsNullOrWhiteSpace(n))];

    /// <summary>The store's description as plain text; none when it only repeats the title.</summary>
    public string? DescriptionText =>
        (Library?.Description ?? Product?.Description) is { Length: > 0 } html && HtmlText.ToPlain(html) is { Length: > 0 } text && text != Title ? text : null;

    /// <summary>Links to the pictures in its listing's gallery, once read from Fab or Cosmos.</summary>
    private IEnumerable<string> ShotURLs => Listing?.Pictures ?? Product?.Images ?? [];

    /// <summary>More about it arrived from its store while the preview shows it.</summary>
    internal void DetailsRead()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(DescriptionText));
        // Until then the gallery was the item's own picture alone.
        if (IsPreviewed && Shots.Count <= 1 && ShotURLs.Any())
            ShowShots();
        if (IsPreviewed && TechnicalText is null)
            AskTechnical();
    }

    /// <summary>
    /// What its store's page tells under "Technical details" (Fab) or "Features" (Cosmos), as plain text, once the preview
    /// has asked: Fab's takes a request of its own.
    /// </summary>
    [ObservableProperty] public partial string? TechnicalText { get; private set; }

    private async void AskTechnical()
    {
        try
        {
            string? text = Cosmos is not null
                ? Product?.Features is { Length: > 0 } features ? HtmlText.ToPlain(features) : null
                : await _owner.TechnicalDetailsOfAsync(this);
            if (text is { Length: > 0 })
                TechnicalText = text;
        }
        catch (Exception)
        {
            // async void: an escaping exception would end the app. Without an answer they just aren't shown.
        }
    }

    /// <summary>The preview beside the grid shows this item: its tile is outlined.</summary>
    [ObservableProperty] public partial bool IsPreviewed { get; private set; }

    /// <summary>The picture the preview shows large: the one of <see cref="Shots"/> that's current.</summary>
    [ObservableProperty] public partial Bitmap? Picture { get; private set; }

    /// <summary>
    /// The item's gallery while the preview shows it, small, in a strip under the large picture: its listing's pictures,
    /// or its own picture alone when the listing has none (or isn't read yet).
    /// </summary>
    public ObservableCollection<PreviewShot> Shots { get; } = [];

    /// <summary>The one shown large; the arrows on it go to the one before and after.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShotPositionText))]
    public partial PreviewShot? CurrentShot { get; private set; }

    /// <summary>There's another picture to go to: the arrows and the strip show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShotPositionText))]
    public partial bool HasSeveralShots { get; private set; }

    /// <summary>"3 / 11", for the full-size view.</summary>
    public string ShotPositionText => CurrentShot is { } shot && HasSeveralShots ? $"{Shots.IndexOf(shot) + 1} / {Shots.Count}" : "";

    /// <summary>As many of a gallery's pictures as the strip holds.</summary>
    private const int MaxShots = 24;

    /// <summary>Shows it in the preview, or ends that: its pictures are loaded for as long as it shows, and Fab asked its size.</summary>
    internal void SetPreviewed(bool previewed)
    {
        IsPreviewed = previewed;
        if (!previewed)
        {
            ClearShots();
            return;
        }
        ShowShots();
        AskSize();
        AskTechnical();
    }

    private void ClearShots()
    {
        var thumbnails = _owner.Owner.Services.Thumbnails;
        ShowLarge(null);
        foreach (var shot in Shots)
            shot.Release(thumbnails);
        Shots.Clear();
        HasSeveralShots = false;
    }

    private async void ShowShots()
    {
        ClearShots();
        var thumbnails = _owner.Owner.Services.Thumbnails;
        var shots = ShotURLs.Distinct().Take(MaxShots).Select(url => new PreviewShot(url, thumbnails)).ToList();
        if (shots.Count == 0 && _thumbnailURL is not null)
            shots.Add(new PreviewShot(_thumbnailURL, thumbnails, Key, Library?.ThumbnailImage?.UploadedDate));
        foreach (var shot in shots)
            Shots.Add(shot);
        HasSeveralShots = shots.Count > 1;
        ShowLarge(shots.FirstOrDefault());

        foreach (var shot in shots)
        {
            try
            {
                await shot.LoadAsync();
            }
            catch (Exception)
            {
                // async void: an escaping exception would end the app. A missing picture is just a placeholder.
            }
        }
    }

    /// <summary>Makes a picture of the gallery the one shown large, at the preview's size; null empties that place.</summary>
    private async void ShowLarge(PreviewShot? shot)
    {
        var thumbnails = _owner.Owner.Services.Thumbnails;
        if (CurrentShot is { } before)
            before.IsCurrent = false;
        CurrentShot = shot;
        if (_pictureUse is { } shown)
        {
            _pictureUse = null;
            Picture = null; // first, as the cache may free the picture once it's back
            thumbnails.Release(shown);
        }
        if (shot is null)
            return;

        shot.IsCurrent = true;
        var use = _pictureUse = thumbnails.Get(shot.URL, shot.Key, shot.Uploaded, atWidth: thumbnails.Width * 2);
        try
        {
            var bitmap = await use.Picture;
            if (_pictureUse == use) // the preview hasn't moved on meanwhile
                Picture = bitmap;
        }
        catch (Exception)
        {
            // async void, as above.
        }
    }

    private async void AskSize()
    {
        try
        {
            if (await _owner.SizeOfAsync(this) is { } size)
                Size = size;
        }
        catch (Exception)
        {
            // async void, as above: without an answer the size just isn't shown.
        }
    }

    [RelayCommand]
    private void ShowShot(PreviewShot shot) => ShowLarge(shot);

    /// <summary>The picture after the current one; from the last, round to the first.</summary>
    [RelayCommand]
    private void NextShot() => StepShot(1);

    [RelayCommand]
    private void PreviousShot() => StepShot(-1);

    private void StepShot(int by)
    {
        if (CurrentShot is { } shot && Shots.Count > 1)
            ShowLarge(Shots[(Shots.IndexOf(shot) + by + Shots.Count) % Shots.Count]);
    }

    /// <summary>Opens the current picture at full size over the window, to look closer.</summary>
    [RelayCommand]
    private void OpenViewer()
    {
        if (CurrentShot is not null)
            _owner.Owner.Dialog = new PictureViewerViewModel(_owner.Owner, this);
    }

    [RelayCommand]
    private void ShowPreview() => _owner.Preview = this;

    [RelayCommand]
    private void ClosePreview() => _owner.Preview = null;

    [RelayCommand]
    private void ToggleFavorite() => _owner.Mark(this, _owner.Marks.Favorites, IsFavorite = !IsFavorite);

    [RelayCommand]
    private void ToggleHidden() => _owner.Mark(this, _owner.Marks.Hidden, IsHidden = !IsHidden);

    /// <summary>The tile's line under the title: who made it, or what it is when that's unknown (a Vault Cache copy only).</summary>
    public string BylineText => SellerText ?? KindLabel;

    /// <summary>The engines it's for; for a Cosmos item that doesn't say (or isn't read yet), where it's from.</summary>
    public string EngineVersionsText =>
        EngineAppNames.Any() ? "UE " + CompactVersions(EngineAppNames)
        : Cosmos is not null ? Strings.SourceCosmos
        : Strings.NoEngineVersions;

    public bool IsDownloaded => CosmosFile is not null || Versions.Any(v => v.IsDownloaded);
    public string? DownloadedText =>
        CosmosFile is { } file ? Localized.Format(Strings.InCosmosFolder, Path.GetFileName(file.Path), ByteSize.Format(file.Size))
        : IsDownloaded ? Localized.Format(Strings.InVaultCacheFor, CompactVersions(Versions.Where(v => v.IsDownloaded).Select(v => v.EngineAppName)))
        : null;

    public bool IsInstalled => Installs.Count > 0;
    public string? InstalledText => IsInstalled ? Localized.Format(Strings.InstalledIn, CompactVersions(Installs.Select(i => i.EngineAppName))) : null;

    /// <summary>Marks at the end of the tile's versions line, with their versions on hover; "Working…" in their place while busy.</summary>
    public bool ShowsInstalledMark => IsIdle && IsInstalled;
    public bool ShowsDownloadedMark => IsIdle && IsDownloaded;

    /// <summary>Only plugins go into engines; an installed item of unknown kind can still be removed.</summary>
    public bool ShowsRemove => Kind == FabItemKind.Plugin || IsInstalled;

    /// <summary>Vault Cache copies with a newer build on Fab (one per artifact).</summary>
    public IReadOnlyList<FabVersion> OutdatedDownloads { get; }

    /// <summary>Plugins in engines with a newer build on Fab.</summary>
    public IReadOnlyList<FabInstall> OutdatedInstalls { get; }

    public bool HasUpdate => OutdatedDownloads.Count > 0 || OutdatedInstalls.Count > 0;

    /// <summary>What an update would refresh, e.g. "Newer build on Fab for: installed in UE 5.7; Vault Cache copy for 5.6".</summary>
    public string? UpdateText
    {
        get
        {
            if (!HasUpdate)
                return null;
            string installed = CompactVersions(OutdatedInstalls.Select(i => i.EngineAppName));
            string downloaded = CompactVersions(OutdatedDownloads.Select(v => v.EngineAppName));
            return (OutdatedInstalls.Count > 0, OutdatedDownloads.Count > 0) switch
            {
                (true, true) => Localized.Format(Strings.NewerBuildInstalledAndCopy, installed, downloaded),
                (true, false) => Localized.Format(Strings.NewerBuildInstalled, installed),
                _ => Localized.Format(Strings.NewerBuildCopy, downloaded),
            };
        }
    }

    public bool IsInLibrary => Library is not null;
    /// <summary>Fab gave a link to the item's page, and it is one: https on Fab's site (see <see cref="SafeOpen"/>).</summary>
    public bool HasFabPage => SafeOpen.IsWebPage(Library?.URL);

    /// <summary>What the tile's main button does for this kind of item.</summary>
    public string ActionText => Kind switch
    {
        FabItemKind.Plugin => Strings.ActionInstallToEngine,
        FabItemKind.AssetPack => Strings.ActionAddToProject,
        FabItemKind.Project => Strings.ActionCreateProject,
        _ => Strings.ActionDownload,
    };

    /// <summary>The main button: Update while Fab has a newer build (as in the Epic Games Launcher), otherwise <see cref="ActionText"/>.</summary>
    public string PrimaryText => HasUpdate ? Strings.Update : ActionText;

    /// <summary>What the main button will do: what an update refreshes, or the action with the kind named, as the tile doesn't name it.</summary>
    public string PrimaryToolTip => UpdateText ?? Kind switch
    {
        FabItemKind.Plugin => Strings.TipInstallPlugin,
        FabItemKind.AssetPack => Strings.TipAddAssetPack,
        FabItemKind.Project => Strings.ActionCreateProject,
        _ => Cosmos is null ? Strings.DownloadToVaultCache : Strings.DownloadFromCosmosTip,
    };

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    /// <summary>An operation is working on this item's files; nothing else may start on them until it ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowsInstalledMark), nameof(ShowsDownloadedMark))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(RemoveCommand), nameof(InstallToProjectCommand), nameof(CreateProjectCommand), nameof(DownloadCosmosCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>A tile started showing this item (the grid only makes tiles for rows on screen): load its picture.</summary>
    internal async void ShowThumbnail()
    {
        if (++_viewers > 1 || _thumbnailURL is null)
            return;
        // By item, as Fab rotates the link; its upload date tells a creator's new picture from a rotated one.
        var use = _thumbnailUse = _owner.Owner.Services.Thumbnails.Get(_thumbnailURL, Key, Library?.ThumbnailImage?.UploadedDate);
        try
        {
            var bitmap = await use.Picture;
            if (_thumbnailUse == use) // not scrolled away meanwhile
                Thumbnail = bitmap;
        }
        catch (Exception)
        {
            // async void: an escaping exception would end the app. A missing picture is just a placeholder.
        }
    }

    /// <summary>No tile shows this item any more: the picture goes back to the cache, which keeps recent ones for scrolling back.</summary>
    internal void HideThumbnail()
    {
        if (_viewers == 0 || --_viewers > 0)
            return;
        Thumbnail = null; // first, as the cache may free the picture once it's back
        if (_thumbnailUse is { } use)
        {
            _thumbnailUse = null;
            _owner.Owner.Services.Thumbnails.Release(use);
        }
    }

    /// <summary>The search looks in the title, seller, description and category, and in the listing's type and tags once read.</summary>
    public bool Matches(string search) =>
        search.Length == 0 || SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Cosmos is { } cosmos && $"{cosmos.Service} {cosmos.Type} {cosmos.Slug}".Contains(search, StringComparison.OrdinalIgnoreCase)
        || Product is { } product && product.Tags.Concat(product.Genres).Append(product.Description ?? "").Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase))
        || Listing is { } listing && (listing.ListingType?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || (listing.Tags ?? []).Any(t => t.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));

    [RelayCommand]
    private void Primary()
    {
        if (HasUpdate)
            _owner.OpenUpdate(this);
        else
            Action();
    }

    /// <summary>The kind's own action, from the ⋯ menu while the main button is Update. A Cosmos item's is its download.</summary>
    [RelayCommand]
    private void Action()
    {
        if (Cosmos is not null && Kind == FabItemKind.Unknown)
            _owner.StartCosmosDownload(this);
        else
            _owner.OpenAction(this);
    }

    /// <summary>A Cosmos pack is a project too: besides going into one, it can be unpacked as one of its own.</summary>
    public bool ShowsCreateProject => Cosmos is not null && Kind == FabItemKind.AssetPack;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void CreateProject() => _owner.OpenCreateProject(this);

    /// <summary>A Cosmos item's files as Cosmos hands them out, into the Cosmos folder and no further.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void DownloadCosmos() => _owner.StartCosmosDownload(this);

    /// <summary>What Cosmos tells of it arrived: what it is and what its button does follow from that.</summary>
    internal void ProductRead()
    {
        foreach (string property in (string[])[nameof(Kind), nameof(KindLabel), nameof(Versions), nameof(EngineVersionsText), nameof(BylineText),
                     nameof(ActionText), nameof(PrimaryText), nameof(PrimaryToolTip), nameof(ShowsCreateProject), nameof(Category)])
            OnPropertyChanged(property);
    }

    public bool IsFromCosmos => Cosmos is not null;

    /// <summary>A Cosmos item's page there, where its other formats and its reviews are.</summary>
    [RelayCommand]
    private void OpenCosmosPage()
    {
        if (Cosmos is { } cosmos)
            _owner.Owner.OpenWebPage(CosmosEndpoints.ProductURL(cosmos.Slug));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Download() => _owner.OpenDownload(this);

    [RelayCommand]
    private void OpenFabPage() => _owner.Owner.OpenWebPage(Library?.URL);

    public bool HasFolder => IsDownloaded || IsInstalled;

    /// <summary>Opens the downloaded copy in the Vault Cache; failing that, where the plugin is installed.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        string? folder = (CosmosFile is { } file ? Path.GetDirectoryName(file.Path) : null)
            ?? Versions.FirstOrDefault(v => v.IsDownloaded)?.Vault?.Directory
            ?? Installs.FirstOrDefault(i => Directory.Exists(i.Folder))?.Folder;
        if (folder is not null)
            _owner.Owner.OpenFolder(folder);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Remove() => _owner.OpenRemove(this);

    /// <summary>Plugins only: into a project's Plugins folder rather than an engine.</summary>
    public bool ShowsInstallToProject => Kind == FabItemKind.Plugin;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void InstallToProject() => _owner.OpenInstallToProject(this);

    /// <summary>["UE_4.27","UE_5.3","UE_5.4","UE_5.5"] → "4.27, 5.3–5.5".</summary>
    internal static string CompactVersions(IEnumerable<string> engineAppNames)
    {
        var versions = engineAppNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => (Name: a.StartsWith("UE_", StringComparison.Ordinal) ? a[3..] : a, Version: EngineLibrary.ParseVersion(a)))
            .OrderBy(v => v.Version)
            .ToList();

        var parts = new List<string>();
        for (int i = 0; i < versions.Count; i++)
        {
            int start = i;
            while (i + 1 < versions.Count && Follows(versions[i + 1].Version, versions[i].Version))
                i++;
            parts.Add(i - start >= 2 ? $"{versions[start].Name}–{versions[i].Name}"
                : i > start ? $"{versions[start].Name}, {versions[i].Name}"
                : versions[start].Name);
        }
        return string.Join(", ", parts);
    }

    /// <summary>The engine release right after another: 5.7 after 5.6, and 5.0 after 4.27, as there was no 4.28.</summary>
    private static bool Follows(Version next, Version previous) =>
        next.Major == previous.Major ? next.Minor == previous.Minor + 1 : (previous.Major, previous.Minor, next.Major, next.Minor) == (4, 27, 5, 0);
}
