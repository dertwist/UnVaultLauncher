using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;
using UnVault.Core.Cosmos;
using UnVault.Core.EGL;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Install;
using UnVault.Core.Projects;
using UnVault.Core.Util;
using UnVault.Core.Vault;

namespace UnVault.App.ViewModels;

/// <summary>One row of the Fab grid; <paramref name="Columns"/> is the page's, so a short last row keeps full-size tiles.</summary>
public sealed record FabItemRow(IReadOnlyList<FabItemViewModel> Items, int Columns);

/// <summary>
/// The Fab tab: the account's Fab library merged with what's already in the vault cache. Signed out, it still
/// shows (and can install) everything previously downloaded.
/// </summary>
public partial class FabLibraryViewModel : ViewModelBase
{
    private static string AllEngines => Strings.AllEngines;

    private List<FabItemViewModel> _all = [];
    private IReadOnlyList<FabLibraryItem>? _library;
    private DateTimeOffset? _libraryFetchedAt;
    private string? _libraryAccountID; // whose _library is
    private Task? _refreshing;
    private bool _rebuilding;
    private int _columns = 4;
    private CancellationTokenSource? _searchDelay;
    private bool _listingsLoaded;
    private Task? _readingListings;
    private LibraryMarks? _marks;

    /// <summary>Listings read at once: few, to go easy on Fab.</summary>
    private const int ListingsAtOnce = 4;

    public FabLibraryViewModel(MainViewModel owner)
    {
        Owner = owner;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSignedIn))
                OnPropertyChanged(nameof(IsSignedOut));
        };
    }

    public MainViewModel Owner { get; }

    /// <summary>Raised when the user changes the search or a filter (not when the library reloads).</summary>
    public event EventHandler? FilterChanged;

    /// <summary>The items passing the search and filters.</summary>
    [ObservableProperty] public partial IReadOnlyList<FabItemViewModel> Items { get; private set; } = [];

    /// <summary><see cref="Items"/> cut into grid rows, so the view can create tiles only for rows on screen.</summary>
    [ObservableProperty] public partial IReadOnlyList<FabItemRow> Rows { get; private set; } = [];

    /// <summary>Tiles per row; the view sets it from its width.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            if (value < 1 || value == _columns)
                return;
            _columns = value;
            BuildRows();
        }
    }
    public IReadOnlyList<string> KindOptions { get; } = [Strings.AllTypes, Strings.KindPlugins, Strings.KindAssetPacks, Strings.KindProjects];
    public ObservableCollection<string> EngineOptions { get; } = [AllEngines];
    public ObservableCollection<string> CategoryOptions { get; } = [Strings.AllCategories];
    public ObservableCollection<string> SellerOptions { get; } = [Strings.AllSellers];

    /// <summary>Fills in as the listings' details are read from Fab.</summary>
    public ObservableCollection<string> ListingOptions { get; } = [Strings.AllListingTypes];

    /// <summary>The orders Fab's own library page offers, in its order; <see cref="SortIndex"/> picks one.</summary>
    public IReadOnlyList<string> SortOptions { get; } =
        [Strings.SortNewest, Strings.SortOldest, Strings.SortTitleAscending, Strings.SortTitleDescending, Strings.SortLastUpdated];

    private const int SortOldest = 1, SortTitleAscending = 2, SortTitleDescending = 3, SortLastUpdated = 4;

    public bool IsSignedOut => !Owner.IsSignedIn;
    public bool HasLoaded { get; private set; }

    /// <summary>The user's favorites and hidden items, read on first use.</summary>
    internal LibraryMarks Marks => _marks ??= LibraryMarks.Load(MarksPath);

    /// <summary>Where the marks are kept; tests keep theirs in a file of their own.</summary>
    internal string? MarksPath { get; set; }

    /// <summary>Listings' details by listing ID, as far as they've been read from Fab.</summary>
    internal Dictionary<string, FabListing> Listings { get; set; } = [];

    /// <summary>Where they're kept; tests keep theirs in a file of their own.</summary>
    internal string? ListingsPath { get; set; }

    /// <summary>What the account owns on Leartes' Cosmos store, once read; null while signed out of it, or not read yet.</summary>
    internal IReadOnlyList<CosmosItem>? Cosmos { get; set; }

    /// <summary>The Cosmos products' files in the Cosmos folder, by product, as last looked.</summary>
    internal IReadOnlyList<CosmosFile> CosmosFiles { get; set; } = [];

    /// <summary>Cosmos products' details by their name in links, as far as they've been read from Cosmos.</summary>
    internal Dictionary<string, CosmosProduct> Products { get; set; } = [];

    /// <summary>Where they're kept; tests keep theirs in a file of their own.</summary>
    internal string? ProductsPath { get; set; }

    public IReadOnlyList<string> SourceOptions { get; } = [Strings.AllSources, Strings.SourceFab, Strings.SourceCosmos];

    /// <summary>Which store's items show: 0 all, 1 Fab's (the Vault Cache's too), 2 Cosmos's.</summary>
    [ObservableProperty] public partial int SourceIndex { get; set; }

    partial void OnSourceIndexChanged(int value) => OnFilterChanged();

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int KindIndex { get; set; }
    [ObservableProperty] public partial string? EngineOption { get; set; } = AllEngines;
    [ObservableProperty] public partial string? CategoryOption { get; set; } = Strings.AllCategories;
    [ObservableProperty] public partial string? SellerOption { get; set; } = Strings.AllSellers;
    [ObservableProperty] public partial string? ListingOption { get; set; } = Strings.AllListingTypes;
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial bool DownloadedOnly { get; set; }
    [ObservableProperty] public partial bool InstalledOnly { get; set; }
    [ObservableProperty] public partial bool UpdatesOnly { get; set; }
    [ObservableProperty] public partial bool FavoritesOnly { get; set; }
    [ObservableProperty] public partial bool ShowHidden { get; set; }

    /// <summary>The item whose preview shows beside the grid; null while it's closed.</summary>
    [ObservableProperty] public partial FabItemViewModel? Preview { get; set; }

    public const double DefaultPreviewWidth = 380, MinPreviewWidth = 300, MaxPreviewWidth = 900;

    /// <summary>How wide the preview is; the user drags its left edge. Kept in the settings, once the drag ends.</summary>
    public double PreviewWidth
    {
        get => Math.Clamp(Owner.Settings.PreviewWidth ?? DefaultPreviewWidth, MinPreviewWidth, MaxPreviewWidth);
        set
        {
            double width = Math.Clamp(Math.Round(value), MinPreviewWidth, MaxPreviewWidth);
            if (width == PreviewWidth)
                return;
            Owner.Settings.PreviewWidth = width;
            OnPropertyChanged();
        }
    }

    /// <summary>The drag ended: the width is saved. One that can't be saved stays for this session.</summary>
    internal void SavePreviewWidth()
    {
        try
        {
            Owner.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time it's dragged.
        }
    }

    partial void OnPreviewChanged(FabItemViewModel? oldValue, FabItemViewModel? newValue)
    {
        oldValue?.SetPreviewed(false);
        newValue?.SetPreviewed(true);
    }

    /// <summary>How many downloads Download Shown would start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadShownText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadShownCommand))]
    public partial int DownloadShownCount { get; private set; }

    public string DownloadShownText => Localized.Format(Strings.DownloadShown, DownloadShownCount);

    /// <summary>How many items have a newer build on Fab (drives the dot on the Library tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatesFilterText))]
    public partial int UpdateCount { get; private set; }

    public string UpdatesFilterText => UpdateCount > 0 ? Localized.Format(Strings.UpdateAvailableFilterCount, UpdateCount) : Strings.UpdateAvailableFilter;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? LoadingText { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>Shown when there's no tile: whether filters hide everything or there's nothing at all.</summary>
    [ObservableProperty] public partial string EmptyText { get; set; } = "";

    /// <summary>Filters once typing pauses rather than on every key.</summary>
    partial void OnSearchTextChanged(string value) => FilterSoon();

    private async void FilterSoon()
    {
        _searchDelay?.Cancel();
        var delay = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(100, delay.Token);
        }
        catch (OperationCanceledException)
        {
            return; // another key came first
        }
        OnFilterChanged();
    }

    /// <summary>Empties the search box and shows everything again at once (the × button, or Esc).</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        if (SearchText.Length == 0)
            return;
        SearchText = "";
        _searchDelay?.Cancel();
        OnFilterChanged();
    }

    partial void OnKindIndexChanged(int value) => OnFilterChanged();
    partial void OnEngineOptionChanged(string? value) => OnFilterChanged();
    partial void OnCategoryOptionChanged(string? value) => OnFilterChanged();
    partial void OnSellerOptionChanged(string? value) => OnFilterChanged();
    partial void OnListingOptionChanged(string? value) => OnFilterChanged();
    partial void OnSortIndexChanged(int value) => OnFilterChanged();
    partial void OnDownloadedOnlyChanged(bool value) => OnFilterChanged();
    partial void OnInstalledOnlyChanged(bool value) => OnFilterChanged();
    partial void OnUpdatesOnlyChanged(bool value) => OnFilterChanged();
    partial void OnFavoritesOnlyChanged(bool value) => OnFilterChanged();
    partial void OnShowHiddenChanged(bool value) => OnFilterChanged();
    partial void OnIsLoadingChanged(bool value) => IsEmpty = !value && Items.Count == 0;

    private void OnFilterChanged()
    {
        if (_rebuilding)
            return;
        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reloads the vault cache and installs; with <paramref name="includeLibrary"/>, also asks Fab for the library.</summary>
    public async Task LoadAsync(bool includeLibrary)
    {
        IsLoading = true;
        LoadingText = Strings.ScanningDownloads;
        try
        {
            string vaultDirectory = Owner.Settings.ResolveVaultCache().Path;
            var engines = Owner.LocalEngines;
            var finding = Task.Run(() => FindPluginInstalls(engines));
            IReadOnlyList<VaultEntry> vault;
            try
            {
                vault = await Task.Run(() => VaultCache.Scan(vaultDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                vault = [];
                Owner.Notice = Localized.Format(Strings.VaultCacheUnreadable, vaultDirectory, ex.Message);
            }
            var installs = await finding;
            if (!_listingsLoaded)
            {
                (Listings, Products) = await Task.Run(() => (FabListingCache.Load(ListingsPath), CosmosProductCache.Load(ProductsPath)));
                _listingsLoaded = true;
            }

            string? accountID = Owner.CurrentAccountID;
            if (accountID != _libraryAccountID)
            {
                // Signed out or into another account: nothing of the last one's may show, nor its fetch still under way.
                _library = null;
                _libraryFetchedAt = null;
                _libraryAccountID = accountID;
                _refreshing = null;
                Error = null;
            }

            if (accountID is not null && (includeLibrary || _library is null))
            {
                // Fab is slow to list a big library, so show the copy from last time meanwhile.
                if (_library is null && await Task.Run(() => FabLibraryCache.Load(accountID)) is { } saved && Owner.CurrentAccountID == accountID)
                {
                    _library = saved.Items;
                    _libraryFetchedAt = saved.FetchedAt;
                    SetItems(_library, vault, installs);
                    HasLoaded = true;
                }

                var refresh = _refreshing ??= RefreshLibraryAsync(accountID);
                try
                {
                    await refresh;
                }
                finally
                {
                    if (_refreshing == refresh)
                        _refreshing = null; // even after a failure, so the next load tries again
                }
                if (Owner.CurrentAccountID != accountID)
                    return; // the account changed meanwhile; its own load shows its library
            }

            if (Owner.IsCosmosSignedIn && (includeLibrary || Cosmos is null))
                await RefreshCosmosAsync();
            if (Cosmos is not null)
            {
                string cosmosFolder = Owner.Settings.ResolveCosmosFolder();
                CosmosFiles = await Task.Run(() => CosmosDownloads.Find(cosmosFolder));
            }

            SetItems(_library, vault, installs);
            HasLoaded = true;
            if (_readingListings is not { IsCompleted: false })
                _readingListings = ReadDetailsAsync(_library ?? [], Cosmos ?? []);
        }
        finally
        {
            LoadingText = null;
            IsLoading = false;
        }
    }

    /// <summary>
    /// Asks Cosmos what the account owns there. A sign-in Cosmos no longer accepts ends it here too; anything else that
    /// goes wrong leaves what was read before, and says so.
    /// </summary>
    private async Task RefreshCosmosAsync()
    {
        var services = Owner.Services;
        if (await Task.Run(services.CosmosTokens.Load) is not { } token)
        {
            Owner.CosmosSessionEnded();
            return;
        }

        LoadingText = Strings.LoadingCosmos;
        try
        {
            var progress = new Progress<int>(read => LoadingText = Localized.Format(Strings.LoadingCosmosRead, read));
            var items = await services.Cosmos.GetInventoryAsync(token, progress);
            if (!Owner.IsCosmosSignedIn)
                return; // signed out of it meanwhile
            Cosmos = items;

            // As with Fab's: when an item was got isn't told, so the first time it's listed stands in for that.
            var now = DateTimeOffset.Now;
            bool added = false;
            foreach (var item in items)
                added |= Marks.FirstSeen.TryAdd(KeyOf(item), now);
            if (added)
                Marks.Save(MarksPath);
        }
        catch (CosmosException ex) when (ex.IsSignedOut)
        {
            Owner.CosmosSessionEnded();
        }
        catch (Exception ex) when (ex is CosmosException or HttpRequestException or TaskCanceledException)
        {
            Owner.Notice = Localized.Format(Strings.CosmosLoadFailed, ex.Message);
        }
    }

    /// <summary>
    /// Reads from Fab the details of the library's listings not read yet, or not for a while: a request each, so a few at a
    /// time and in the background, with what's shown catching up now and then. Stops at one that fails; the next load goes on.
    /// </summary>
    internal Task ReadListingsAsync(IReadOnlyList<FabLibraryItem> library) =>
        ReadInBatchesAsync(library.Select(i => i.ListingID).OfType<string>().Distinct().Where(id => FabListingCache.IsDue(Listings, id)),
            ReadListingAsync, (id, listing) => Listings[id] = listing);

    /// <summary>The same for what's owned on Cosmos: each product's details, which its inventory leaves out.</summary>
    internal Task ReadProductsAsync(IReadOnlyList<CosmosItem> cosmos) =>
        ReadInBatchesAsync(cosmos.Select(i => i.Slug).Where(s => s.Length > 0).Distinct().Where(slug => CosmosProductCache.IsDue(Products, slug)),
            ReadProductAsync, (slug, product) => Products[slug] = product);

    private async Task ReadDetailsAsync(IReadOnlyList<FabLibraryItem> library, IReadOnlyList<CosmosItem> cosmos)
    {
        await ReadListingsAsync(library);
        await ReadProductsAsync(cosmos);
    }

    /// <param name="read">Reads one; null when the store didn't answer, which ends the run.</param>
    private async Task ReadInBatchesAsync<T>(IEnumerable<string> due, Func<string, Task<T?>> read, Action<string, T> keep) where T : class
    {
        var batches = due.Chunk(ListingsAtOnce).ToList();
        for (int i = 0; i < batches.Count; i++)
        {
            var answers = await Task.WhenAll(batches[i].Select(read));
            foreach (var (id, answer) in batches[i].Zip(answers))
            {
                if (answer is not null)
                    keep(id, answer);
            }

            bool failed = answers.Contains(null);
            if (failed || i == batches.Count - 1 || i % 10 == 9)
                await DetailsReadAsync();
            if (failed)
                return;
        }
    }

    /// <summary>Null when Fab didn't answer, or not with a listing.</summary>
    private async Task<FabListing?> ReadListingAsync(string listingID)
    {
        try
        {
            return await Owner.Services.API.GetFabListingAsync(listingID);
        }
        catch (EpicAPIException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // Taken off Fab, or shown only signed in on its site (mature content): nothing to read, and no use asking again soon.
            return new FabListing { FetchedAt = DateTimeOffset.UtcNow, Fields = FabListingCache.Fields };
        }
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Null when Cosmos didn't answer.</summary>
    private async Task<CosmosProduct?> ReadProductAsync(string slug)
    {
        try
        {
            return await Owner.Services.Cosmos.GetProductAsync(slug);
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden or null)
        {
            // Taken off Cosmos, or an answer that isn't a product: nothing to read, and no use asking again soon.
            return new CosmosProduct { FetchedAt = DateTimeOffset.UtcNow, Fields = CosmosProductCache.Fields };
        }
        catch (Exception ex) when (ex is CosmosException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a Fab item's page shows under "Technical details": a request of its own, so made when the item's preview first
    /// shows, and kept with its listing. Null when there's none, the listing isn't read yet, or Fab doesn't answer.
    /// </summary>
    internal async Task<string?> TechnicalDetailsOfAsync(FabItemViewModel item)
    {
        if (item.Library?.ListingID is not { } id || Listings.GetValueOrDefault(id) is not { } listing)
            return null;
        if (listing.TechnicalDetails is null)
        {
            try
            {
                listing.TechnicalDetails = await Owner.Services.API.GetFabTechnicalDetailsAsync(id);
            }
            catch (EpicAPIException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                listing.TechnicalDetails = ""; // none to show, and no use asking again
            }
            catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException)
            {
                return null; // asked again when the preview next shows it
            }
            await SaveDetailsAsync();
        }
        return listing.TechnicalDetails is { Length: > 0 } text ? text : null;
    }

    private async Task SaveDetailsAsync()
    {
        var (listings, products) = (new Dictionary<string, FabListing>(Listings), new Dictionary<string, CosmosProduct>(Products));
        try
        {
            await Task.Run(() =>
            {
                FabListingCache.Save(listings, ListingsPath);
                CosmosProductCache.Save(products, ProductsPath);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // They're read again next time.
        }
    }

    /// <summary>More details were read: they're kept for next time, and offered to filter and sort by.</summary>
    private async Task DetailsReadAsync()
    {
        await SaveDetailsAsync();

        _rebuilding = true;
        try
        {
            RebuildOptions();
        }
        finally
        {
            _rebuilding = false;
        }
        Preview?.DetailsRead();
        foreach (var item in _all.Where(i => i.Cosmos is not null))
            item.ProductRead();
        // Only when it changes what's shown: the rows are made anew, which a user scrolling through them would see.
        if (ListingOption != Strings.AllListingTypes || CategoryOption != Strings.AllCategories || EngineOption != AllEngines
            || SortIndex == SortLastUpdated || SearchText.Trim().Length > 0)
            ApplyFilter();
    }

    /// <summary>The filters' choices, from the items and what's been read about them. Under <see cref="_rebuilding"/>.</summary>
    private void RebuildOptions()
    {
        EngineOption = Rebuilt(EngineOptions, AllEngines, EngineOption,
            _all.SelectMany(i => i.EngineAppNames).Where(e => e.StartsWith("UE_", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(EngineLibrary.ParseVersion).Select(e => $"UE {e[3..]}"), sorted: true);
        CategoryOption = Rebuilt(CategoryOptions, Strings.AllCategories, CategoryOption, _all.Select(i => i.Category));
        SellerOption = Rebuilt(SellerOptions, Strings.AllSellers, SellerOption, _all.Select(i => i.SellerText));
        ListingOption = Rebuilt(ListingOptions, Strings.AllListingTypes, ListingOption, _all.Select(i => i.ListingType));
    }

    private async Task RefreshLibraryAsync(string accountID)
    {
        LoadingText = _library is null ? Strings.LoadingLibrary : Strings.UpdatingLibrary;
        try
        {
            var progress = new Progress<int>(read => LoadingText = Localized.Format(_library is null ? Strings.LoadingLibraryRead : Strings.UpdatingLibraryRead, read));
            var library = await Owner.Services.API.GetFabLibraryAsync(progress);
            if (Owner.CurrentAccountID != accountID)
                return;
            _library = library;
            _libraryFetchedAt = DateTimeOffset.Now;
            Error = null;

            // Fab doesn't say when an item was acquired; the first time it's listed stands in for that.
            bool added = false;
            foreach (var item in library.Where(i => !FabKinds.IsEngine(i)))
                added |= Marks.FirstSeen.TryAdd(KeyOf(item), _libraryFetchedAt.Value);
            if (added)
                Marks.Save(MarksPath);

            var snapshot = new FabLibrarySnapshot { AccountID = accountID, FetchedAt = _libraryFetchedAt.Value, Items = library };
            try
            {
                await Task.Run(() => FabLibraryCache.Save(snapshot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only costs the instant start next time.
            }
        }
        catch (NotLoggedInException ex)
        {
            Owner.SessionEnded(ex);
        }
        catch (Exception ex) when (ex is EpicAPIException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // IOException and UnauthorizedAccessException: saving refreshed sign-in tokens failed.
            if (Owner.CurrentAccountID != accountID)
                return;
            Error = _library is null
                ? Localized.Format(Strings.LibraryLoadFailed, ex.Message)
                : Localized.Format(Strings.LibraryUpdateFailed, _libraryFetchedAt?.ToLocalTime(), ex.Message);
        }
    }

    /// <summary>Builds the rows: library items with their vault copies, then vault-only downloads grouped by product.</summary>
    internal void SetItems(IReadOnlyList<FabLibraryItem>? library, IReadOnlyList<VaultEntry> vault, IReadOnlyList<FabInstall> installs)
    {
        var vaultByArtifact = vault.GroupBy(v => v.ArtifactID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<FabItemViewModel>();

        int order = 0;
        foreach (var item in (library ?? []).Where(i => !FabKinds.IsEngine(i)))
        {
            var versions = new List<FabVersion>();
            foreach (var projectVersion in item.ProjectVersions ?? [])
            {
                vaultByArtifact.TryGetValue(projectVersion.ArtifactID, out var downloaded);
                if (downloaded is not null)
                    used.Add(projectVersion.ArtifactID);
                foreach (string engine in projectVersion.EngineVersions ?? [])
                    versions.Add(new FabVersion(engine, projectVersion.ArtifactID, projectVersion, downloaded));
            }

            var kind = FabKinds.FromLibrary(item);
            if (kind == FabItemKind.Unknown && versions.FirstOrDefault(v => v.Vault is not null)?.Vault is { } sample)
                kind = FabKinds.FromVault(sample);
            items.Add(Row(KeyOf(item), item.Title, kind, item, versions, installs, item.ThumbnailURL, order++));
        }

        // Downloads not (or no longer) in the library — or everything, when signed out.
        foreach (var group in vault.Where(v => !used.Contains(v.ArtifactID))
                     .GroupBy(v => v.ItemID.Length > 0 ? v.ItemID : v.Title, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var versions = group
                .Select(v => new FabVersion(FabKinds.EngineAppFromBuild(v.Version) ?? FabKinds.EngineAppFromBuild(v.Build) ?? "?", v.ArtifactID, null, v))
                .ToList();
            items.Add(Row("vault:" + group.Key, first.Title, FabKinds.FromVault(first), null, versions, installs, first.Thumbnail));
        }

        // What the account owns on Cosmos: nothing of them is downloaded or installed here, so they're just listed.
        foreach (var item in Cosmos ?? [])
        {
            items.Add(new FabItemViewModel(this, KeyOf(item), item.Title, FabItemKind.Unknown, null, [], [], item.CoverImage?.URL)
            {
                Cosmos = item, LibraryOrder = order++,
                CosmosFile = CosmosFiles.FirstOrDefault(f => string.Equals(f.Slug, item.Slug, StringComparison.OrdinalIgnoreCase)),
            });
        }

        _all = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        UpdateBusy();
        // The items are new ones: the preview goes on with the one for the same product, and closes if that's gone.
        if (Preview is { } previewed)
            Preview = _all.FirstOrDefault(i => i.Key == previewed.Key);

        // Rebuilding the options makes a combo box clear its selection; that's not the user changing the filter.
        _rebuilding = true;
        try
        {
            RebuildOptions();
        }
        finally
        {
            _rebuilding = false;
        }

        ApplyFilter();

        FabItemViewModel Row(string key, string title, FabItemKind kind, FabLibraryItem? libraryItem, List<FabVersion> versions, IReadOnlyList<FabInstall> allInstalls, string? thumbnail,
            int libraryOrder = -1)
        {
            var artifacts = versions.Select(v => v.ArtifactID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var itemInstalls = allInstalls.Where(i => artifacts.Contains(i.ArtifactID)).ToList();
            return new FabItemViewModel(this, key, title, kind, libraryItem, versions, itemInstalls, thumbnail) { LibraryOrder = libraryOrder };
        }
    }

    internal static string KeyOf(FabLibraryItem item) =>
        item.AssetID.Length > 0 ? $"fab:{item.AssetNamespace}/{item.AssetID}" : "fab:" + item.Title;

    internal static string KeyOf(CosmosItem item) => "cosmos:" + item.ID;

    /// <summary>
    /// Fills a filter's choices: everything first, then each value once. Left alone when they're the same as before, so
    /// an open list doesn't change under the user.
    /// </summary>
    /// <param name="sorted">The values come in the order to show, each once.</param>
    /// <returns>What's chosen now: the same as before if it's still offered.</returns>
    private static string Rebuilt(ObservableCollection<string> options, string all, string? chosen, IEnumerable<string?> values, bool sorted = false)
    {
        var names = values.OfType<string>();
        if (!sorted)
            names = names.Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
        var list = names.ToList();
        if (!options.Skip(1).SequenceEqual(list))
        {
            options.Clear();
            options.Add(all);
            foreach (string name in list)
                options.Add(name);
        }
        return chosen is not null && options.Contains(chosen) ? chosen : all;
    }

    /// <summary>The engine filtered on, as an app name; null for all engines.</summary>
    private string? EngineFilter => EngineOption is { } option && option != AllEngines ? "UE_" + option[3..] : null;

    private void ApplyFilter()
    {
        var kind = KindIndex switch { 1 => FabItemKind.Plugin, 2 => FabItemKind.AssetPack, 3 => FabItemKind.Project, _ => (FabItemKind?)null };
        string? engine = EngineFilter;
        string? category = Chosen(CategoryOption, Strings.AllCategories);
        string? seller = Chosen(SellerOption, Strings.AllSellers);
        string? listing = Chosen(ListingOption, Strings.AllListingTypes);
        string search = SearchText.Trim();

        var shown = _all.Where(item =>
            (SourceIndex == 0 || (item.Cosmos is not null) == (SourceIndex == 2))
            && (kind is null || item.Kind == kind)
            && (engine is null || item.EngineAppNames.Contains(engine, StringComparer.OrdinalIgnoreCase))
            && (category is null || Same(item.Category, category))
            && (seller is null || Same(item.SellerText, seller))
            && (listing is null || Same(item.ListingType, listing))
            && (!DownloadedOnly || item.IsDownloaded)
            && (!InstalledOnly || item.IsInstalled)
            && (!UpdatesOnly || item.HasUpdate)
            && (!FavoritesOnly || item.IsFavorite)
            && (ShowHidden || !item.IsHidden)
            && item.Matches(search));

        // Swapped in whole: adding a thousand items one by one meant a thousand change notifications. The items are by
        // title already, and stay so among those a sort finds equal. Newest is by when the library first listed an item,
        // then by the store's own order, which lists what was got first first. Vault Cache copies not in the library
        // have neither: they come last.
        Items = (SortIndex switch
        {
            SortOldest => shown.OrderBy(i => i.LibraryOrder < 0).ThenBy(i => i.FirstSeen).ThenBy(i => i.LibraryOrder),
            SortTitleAscending => shown,
            SortTitleDescending => shown.Reverse(),
            SortLastUpdated => shown.OrderByDescending(i => i.UpdatedAt), // those not read from Fab yet come last
            _ => shown.OrderBy(i => i.LibraryOrder < 0).ThenByDescending(i => i.FirstSeen).ThenByDescending(i => i.LibraryOrder),
        }).ToList();
        BuildRows();
        DownloadShownCount = ShownDownloads().Count;

        int downloaded = _all.Count(i => i.IsDownloaded);
        UpdateCount = _all.Count(i => i.HasUpdate && !i.IsHidden);

        Summary = Items.Count == _all.Count
            ? Localized.Plural(nameof(Strings.LibrarySummary_Other), _all.Count, downloaded)
            : Localized.Plural(nameof(Strings.LibraryFiltered_Other), _all.Count, Items.Count);
        IsEmpty = !IsLoading && Items.Count == 0;
        EmptyText = _all.Count > 0 ? Strings.NothingMatches
            : HasError ? "" // the banner above says what went wrong
            : IsSignedOut ? Strings.VaultCacheEmpty
            : Strings.FabLibraryEmpty;

        static string? Chosen(string? option, string all) => option is not null && option != all ? option : null;
        static bool Same(string? value, string chosen) => string.Equals(value, chosen, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Sizes asked of Fab before, by artifact: asked once, however often an item's preview opens.</summary>
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an item's files come to for its newest engine version, by Fab's own list of them (its manifest): the listing
    /// tells no size. Null for an item that isn't in the library, signed out, or when Fab doesn't answer.
    /// </summary>
    internal async Task<long?> SizeOfAsync(FabItemViewModel item)
    {
        if (!Owner.IsSignedIn || item.Library is null
            || item.Versions.Where(v => v.Library is not null).MaxBy(v => EngineLibrary.ParseVersion(v.EngineAppName)) is not { } version)
            return null;
        if (_sizes.TryGetValue(version.ArtifactID, out long known))
            return known;
        try
        {
            var source = await FabWorkflow.FetchAsync(Owner.Services.API, Artifact(item, version), CancellationToken.None);
            long size = await Task.Run(() => source.Manifest.Files.Sum(f => f.FileSize));
            return _sizes[version.ArtifactID] = size;
        }
        catch (Exception ex) when (ex is EpicAPIException or NotLoggedInException or InstallException or HttpRequestException or TaskCanceledException
                                       or IOException or UnauthorizedAccessException)
        {
            return null; // the preview shows the rest without it
        }
    }

    /// <summary>Puts an item among the user's favorites or hidden items, or takes it out, and saves that.</summary>
    internal void Mark(FabItemViewModel item, HashSet<string> marked, bool on)
    {
        if (on)
            marked.Add(item.Key);
        else
            marked.Remove(item.Key);
        Marks.Save(MarksPath);
        // Only when it changes what's shown: the rows are made anew.
        if (marked == Marks.Hidden || FavoritesOnly)
            ApplyFilter();
    }

    /// <summary>
    /// What Download Shown fetches: of each item shown, the copy for the engine filtered on. With no engine chosen, the one
    /// for its newest engine, and nothing when the Vault Cache has a current copy of the item for any.
    /// </summary>
    private List<(FabItemViewModel Item, FabVersion Version)> ShownDownloads()
    {
        string? engine = EngineFilter;
        var downloads = new List<(FabItemViewModel, FabVersion)>();
        foreach (var item in Items.Where(i => i.IsInLibrary && !i.IsBusy))
        {
            var offered = item.Versions.Where(v => v.Library is not null);
            var version = engine is not null ? offered.FirstOrDefault(v => string.Equals(v.EngineAppName, engine, StringComparison.OrdinalIgnoreCase))
                : item.Versions.Any(IsCurrent) ? null
                : offered.MaxBy(v => EngineLibrary.ParseVersion(v.EngineAppName));
            if (version is not null && !IsCurrent(version))
                downloads.Add((item, version));
        }
        return downloads;

        static bool IsCurrent(FabVersion version) => version.IsDownloaded && !version.IsOutdated;
    }

    private bool CanDownloadShown => DownloadShownCount > 0;

    /// <summary>Asks first, then downloads them into the Vault Cache one at a time: a library can be hundreds of items.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadShown))]
    private void DownloadShown()
    {
        var downloads = ShownDownloads();
        if (downloads.Count == 0)
            return;
        Owner.Dialog = new ConfirmViewModel(Owner, Localized.Plural(nameof(Strings.ConfirmDownloadShownTitle_Other), downloads.Count),
            Strings.ConfirmDownloadShownMessage, Strings.StartDownloads, () =>
            {
                var queue = new object();
                Owner.StartOperations([.. downloads.Select(d => Download(d.Item, d.Version, queue))]);
            });
    }

    private void BuildRows() => Rows = Items.Chunk(_columns).Select(row => new FabItemRow(row, _columns)).ToList();

    /// <summary>Plugins in the installed engines, whoever put them there: UnVault, EGL, or something else.</summary>
    private static IReadOnlyList<FabInstall> FindPluginInstalls(IReadOnlyList<LocalEngine> engines)
    {
        var launcherInstalled = EGLInstallations.ReadLauncherInstalled();
        var eglItems = EGLInstallations.ReadItems();
        var installs = new List<FabInstall>();
        foreach (var engine in engines.Where(e => e.Exists))
        {
            try
            {
                // An install that didn't finish isn't installed: Install is offered again, and continues it.
                installs.AddRange(EnginePlugins.Find(engine.Directory, launcherInstalled, eglItems).Where(p => p.IsComplete)
                    .Select(p => new FabInstall(engine.AppName, engine.Directory, p.ArtifactID, p.Source, p.Folder, p.CanRemove, p.BuildVersion)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable engine folder just shows no plugins.
            }
        }
        return installs;
    }

    /// <summary>Marks the items running operations work on; see <see cref="MainViewModel.UpdateBusy"/>.</summary>
    internal void UpdateBusy()
    {
        var busy = Owner.Operations.Where(o => o.IsRunning && o.FabItemKey is not null).Select(o => o.FabItemKey!).ToHashSet(StringComparer.Ordinal);
        foreach (var item in _all)
            item.IsBusy = busy.Contains(item.Key);
    }

    internal async void OpenAction(FabItemViewModel item)
    {
        var mode = item.Kind switch
        {
            FabItemKind.Plugin => FabActionMode.InstallPlugin,
            FabItemKind.AssetPack => FabActionMode.AddToProject,
            FabItemKind.Project => FabActionMode.CreateProject,
            _ => FabActionMode.Download,
        };
        await OpenDialogAsync(item, mode);
    }

    internal async void OpenDownload(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.Download);

    internal async void OpenCreateProject(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.CreateProject);

    internal async void OpenRemove(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.RemovePlugin);

    internal async void OpenInstallToProject(FabItemViewModel item) => await OpenDialogAsync(item, FabActionMode.InstallToProject);

    private async Task OpenDialogAsync(FabItemViewModel item, FabActionMode mode)
    {
        if (item.IsBusy)
            return; // two operations on the same files would collide
        var dialog = new FabActionViewModel(this, item, mode);
        Owner.Dialog = dialog;
        await dialog.LoadAsync();
    }

    /// <summary>An install into each engine, one after another (each its own operation, to pause or retry on its own).</summary>
    internal void StartPluginInstall(FabItemViewModel item, IEnumerable<(FabVersion Version, LocalEngine Engine)> targets) =>
        Owner.StartOperations([.. targets.Select(target => PluginInstall(item, target.Version, target.Engine))]);

    private OperationViewModel PluginInstall(FabItemViewModel item, FabVersion version, LocalEngine engine) =>
        new(Owner, item.Title, new OperationAction(OperationKind.InstallPlugin, engine.AppName[3..]), async (operation, status, cancellationToken) =>
        {
            if (version.IsDownloaded && !version.IsOutdated)
            {
                operation.SetPhase(Strings.PhaseCopyingFromVault);
                await FabWorkflow.InstallPluginFromVaultAsync(version.Vault!, engine.Directory, status, cancellationToken);
            }
            else
            {
                operation.SetPhase(Strings.PhaseDownloading);
                var artifact = Artifact(item, version);
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                await FabWorkflow.InstallPluginAsync(source, artifact, engine.Directory, Owner.CreateInstaller(), status, cancellationToken);
            }
            return engine.Directory;
        }, engines: [engine.AppName], fabItemKey: item.Key);

    /// <summary>
    /// An add into each project, one after another. Projects on the same engine version take the same Vault Cache copy, which
    /// the first of them downloads.
    /// </summary>
    internal void StartAddToProject(FabItemViewModel item, IEnumerable<(FabVersion Version, UnrealProject Project)> targets)
    {
        if (item.Cosmos is not null)
        {
            // The same from Cosmos: its zip for the version, downloaded once for the projects that take it, then its Content.
            var files = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase); // zip by artifact
            var get = CosmosDownload(item);
            Owner.StartOperations([.. targets.Select(target => new OperationViewModel(Owner, item.Title,
                new OperationAction(OperationKind.AddToProject, target.Project.Name), async (operation, status, cancellationToken) =>
                {
                    var (version, project) = target;
                    if (!files.TryGetValue(version.ArtifactID, out string? zip))
                        files[version.ArtifactID] = zip = await get(version, operation, status, cancellationToken);
                    var copy = operation.BeginPhase(Strings.PhaseCopyingIntoProject);
                    int count = await CosmosDownloads.AddToProjectAsync(zip, project.Directory, copy, cancellationToken);
                    return Localized.Plural(nameof(Strings.AddDetail_Other), count, project.Directory);
                }, fabItemKey: item.Key))]);
            return;
        }

        var downloaded = new ConcurrentDictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase); // by artifact
        Owner.StartOperations([.. targets.Select(target => new OperationViewModel(Owner, item.Title,
            new OperationAction(OperationKind.AddToProject, target.Project.Name), async (operation, status, cancellationToken) =>
            {
                var (version, project) = target;
                if (!downloaded.TryGetValue(version.ArtifactID, out var entry))
                    downloaded[version.ArtifactID] = entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
                var copy = operation.BeginPhase(Strings.PhaseCopyingIntoProject);
                int files = await FabWorkflow.AddToProjectAsync(entry, project.Directory, copy, cancellationToken);
                return Localized.Plural(nameof(Strings.AddDetail_Other), files, project.Directory);
            }, fabItemKey: item.Key))]);
    }

    /// <summary>
    /// The plugin into each project's Plugins folder, one after another, compiled first with <paramref name="compileWith"/>
    /// when it's given. Projects taking the same version share its download and its compile.
    /// </summary>
    internal void StartInstallToProject(FabItemViewModel item, IEnumerable<(FabVersion Version, UnrealProject Project)> targets, LocalEngine? compileWith)
    {
        var list = targets.ToList();
        var downloaded = new ConcurrentDictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase); // by artifact
        var compiled = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A compiled copy goes once the last project taking it has it; one a stop leaves behind goes at the next start.
        var takers = new ConcurrentDictionary<string, int>(list.GroupBy(t => t.Version.ArtifactID, StringComparer.OrdinalIgnoreCase)
            .Select(g => KeyValuePair.Create(g.Key, g.Count())), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> engines = compileWith is null ? [] : [compileWith.AppName]; // not changed while it compiles

        Owner.StartOperations([.. list.Select(target => new OperationViewModel(Owner, item.Title,
            new OperationAction(OperationKind.InstallToProject, target.Project.Name), async (operation, status, cancellationToken) =>
            {
                var (version, project) = target;
                if (!downloaded.TryGetValue(version.ArtifactID, out var entry))
                    downloaded[version.ArtifactID] = entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
                string folder = await Task.Run(() => ProjectPlugins.FolderIn(entry), cancellationToken);
                if (compileWith is { } engine)
                {
                    string engineVersion = engine.AppName[3..];
                    if (!compiled.TryGetValue(version.ArtifactID, out var built))
                    {
                        if (!FileNames.IsPlain(version.ArtifactID))
                            throw new InstallException(Localized.Format(Strings.ArtifactNotAFolderName, version.ArtifactID));
                        built = PluginBuilder.NewBuildFolder();
                        operation.SetPhase(Localized.Format(Strings.PhaseCompilingWith, engineVersion));
                        try
                        {
                            await PluginBuilder.BuildAsync(engine.Directory, folder, built, PluginBuilder.LogPath(version.ArtifactID, engine.AppName),
                                (done, total) => operation.SetPhase(Localized.Format(Strings.PhaseCompilingWithSteps, engineVersion, done, total)), cancellationToken);
                        }
                        catch
                        {
                            PluginBuilder.DeleteBuild(built); // a stopped or failed build is started anew
                            throw;
                        }
                        compiled[version.ArtifactID] = built;
                    }
                    folder = built;
                }

                var copy = operation.BeginPhase(Strings.PhaseCopyingIntoProject);
                var (installedTo, _) = await ProjectPlugins.InstallAsync(folder, project.Directory, copy, cancellationToken);
                if (compileWith is not null && takers.AddOrUpdate(version.ArtifactID, 0, (_, left) => left - 1) == 0)
                    await Task.Run(() => PluginBuilder.DeleteBuild(folder));
                return compileWith is { } used ? Localized.Format(Strings.InstallToProjectDetailCompiled, used.AppName[3..], installedTo) : installedTo;
            }, engines, fabItemKey: item.Key))]);
    }

    internal void StartCreateProject(FabItemViewModel item, FabVersion version, string targetDirectory)
    {
        if (item.Cosmos is not null)
        {
            var get = CosmosDownload(item);
            Owner.StartOperation(new OperationViewModel(Owner, item.Title, new OperationAction(OperationKind.CreateProject), async (operation, status, cancellationToken) =>
            {
                string zip = await get(version, operation, status, cancellationToken);
                var copy = operation.BeginPhase(Strings.PhaseCopyingProject);
                return await CosmosDownloads.CreateProjectAsync(zip, targetDirectory, copy, cancellationToken);
            }, fabItemKey: item.Key));
            return;
        }

        Owner.StartOperation(new OperationViewModel(Owner, item.Title, new OperationAction(OperationKind.CreateProject), async (operation, status, cancellationToken) =>
        {
            var entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
            var copy = operation.BeginPhase(Strings.PhaseCopyingProject);
            string uproject = await FabWorkflow.CreateProjectAsync(entry, targetDirectory, copy, cancellationToken);
            return uproject;
        }, fabItemKey: item.Key));
    }

    internal void StartDownload(FabItemViewModel item, FabVersion version) => Owner.StartOperation(Download(item, version));

    /// <summary>
    /// Downloads a Cosmos item's files into the Cosmos folder, as its site would: for the engine filtered on when it's an
    /// environment that has it, else its newest. Paused, it goes on from where it stopped.
    /// </summary>
    internal void StartCosmosDownload(FabItemViewModel item)
    {
        if (item.Cosmos is null || item.IsBusy)
            return;
        // For the engine filtered on when it comes for it, else its newest.
        var version = item.Versions.FirstOrDefault(v => string.Equals(v.EngineAppName, EngineFilter, StringComparison.OrdinalIgnoreCase))
            ?? item.Versions.MaxBy(v => EngineLibrary.ParseVersion(v.EngineAppName));
        var get = CosmosDownload(item, again: true);
        Owner.StartOperation(new OperationViewModel(Owner, item.Title, new OperationAction(OperationKind.Download, version?.EngineAppName[3..] ?? Strings.SourceCosmos),
            async (operation, status, cancellationToken) =>
            {
                string path = await get(version, operation, status, cancellationToken);
                return $"{path}  ·  {ByteSize.Format(new FileInfo(path).Length)}";
            }, fabItemKey: item.Key));
    }

    /// <summary>A Cosmos item's file for one of its versions, among those in the Cosmos folder as last looked; null when it isn't there.</summary>
    internal CosmosFile? CosmosFileFor(CosmosItem cosmos, FabVersion? version) =>
        CosmosDownloads.FileFor(CosmosFiles, cosmos.Slug, version?.EngineAppName[3..]);

    /// <summary>
    /// What gets a Cosmos item's zip for a version in an operation: the one in the Cosmos folder, else downloaded there as
    /// Cosmos's site would. What the work needs from here is taken now, as it runs off the UI thread.
    /// </summary>
    /// <param name="again">Download it even when it's there: the user asked for the download itself.</param>
    private Func<FabVersion?, OperationViewModel, InstallStatus, CancellationToken, Task<string>> CosmosDownload(FabItemViewModel item, bool again = false)
    {
        var cosmos = item.Cosmos!;
        var services = Owner.Services;
        string folder = Owner.Settings.ResolveCosmosFolder();
        var files = CosmosFiles;
        // What a download asks by is in the product's details; read in the work if the background hasn't got to it.
        var known = Products.GetValueOrDefault(cosmos.Slug) is { Fields: >= CosmosProductCache.Fields } read ? read : null;
        return async (version, operation, status, cancellationToken) =>
        {
            string? engine = version?.EngineAppName[3..];
            if (!again && CosmosDownloads.FileFor(files, cosmos.Slug, engine) is { } have && File.Exists(have.Path))
                return have.Path;

            operation.SetPhase(Strings.PhaseAskingCosmos);
            if (services.CosmosTokens.Load() is not { } token)
                throw new CosmosException(Strings.CosmosSessionEnded, System.Net.HttpStatusCode.Unauthorized);
            var product = known ?? await services.Cosmos.GetProductAsync(cosmos.Slug, cancellationToken);
            var (link, option) = await CosmosDownloads.RequestAsync(services.HTTP, token, cosmos.Slug, CosmosDownloads.OptionsFor(cosmos, product, engine), cancellationToken);

            operation.SetPhase(Strings.PhaseDownloading);
            return await CosmosDownloads.DownloadAsync(services.HTTP, link, folder, CosmosDownloads.FileName(cosmos.Slug, option, link), status, cancellationToken);
        };
    }

    private OperationViewModel Download(FabItemViewModel item, FabVersion version, object? queue = null) =>
        new(Owner, item.Title, new OperationAction(OperationKind.Download, version.EngineAppName[3..]), async (operation, status, cancellationToken) =>
        {
            var entry = await EnsureInVaultAsync(item, version, operation, status, cancellationToken);
            return $"{entry.Directory}  ·  {ByteSize.Format(entry.Size)}";
        }, fabItemKey: item.Key, queue: queue);

    /// <param name="itemKey">The library item it belongs to, kept busy meanwhile; null for a plugin the library doesn't list.</param>
    /// <returns>The removal, already started, for anything that shows its outcome (Installed Plugins).</returns>
    internal OperationViewModel StartRemove(string title, string? itemKey, FabInstall install)
    {
        var removal = Removal(title, itemKey, install);
        Owner.StartOperation(removal);
        return removal;
    }

    /// <summary>A removal from each engine, one after another (each its own operation, to retry on its own).</summary>
    internal void StartRemovals(string title, string? itemKey, IReadOnlyList<FabInstall> installs) =>
        Owner.StartOperations([.. installs.Select(install => Removal(title, itemKey, install))]);

    private OperationViewModel Removal(string title, string? itemKey, FabInstall install) =>
        new(Owner, title, new OperationAction(OperationKind.RemovePlugin, install.EngineAppName[3..]), async (operation, _, _) =>
        {
            operation.SetPhase(Strings.PhaseDeletingFiles);
            var removed = await Task.Run(() => FabWorkflow.UninstallPlugin(install.EngineDirectory, install.ArtifactID, folder: install.Folder));
            return Localized.Format(Strings.RemoveDetail, ByteSize.Format(removed.BytesFreed));
        }, engines: [install.EngineAppName], fabItemKey: itemKey);

    /// <summary>The library or Vault Cache item an artifact belongs to; null when it's in neither (or the library hasn't loaded).</summary>
    internal FabItemViewModel? FindItem(string artifactID) =>
        _all.FirstOrDefault(i => i.Versions.Any(v => string.Equals(v.ArtifactID, artifactID, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Uses the Vault Cache copy if it's current; otherwise downloads (or updates) it there first.</summary>
    private async Task<VaultEntry> EnsureInVaultAsync(FabItemViewModel item, FabVersion version, OperationViewModel operation, InstallStatus status, CancellationToken cancellationToken)
    {
        if (version.IsDownloaded && !version.IsOutdated)
            return version.Vault!;

        operation.SetPhase(version.IsOutdated ? Strings.PhaseUpdatingVaultCopy : Strings.PhaseDownloading);
        var artifact = Artifact(item, version);
        var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
        return await FabWorkflow.DownloadToVaultAsync(source, artifact, Owner.Settings.ResolveVaultCache().Path, Owner.CreateInstaller(), status, cancellationToken);
    }

    internal void OpenUpdate(FabItemViewModel item)
    {
        if (!item.HasUpdate || item.IsBusy)
            return;
        Owner.Dialog = new ConfirmViewModel(Owner, Localized.Format(Strings.ConfirmUpdateTitle, item.Title), Strings.ConfirmUpdateMessage,
            Strings.Update, () => StartUpdate(item, item.OutdatedDownloads, item.OutdatedInstalls));
    }

    /// <summary>Brings outdated copies of an item up to Fab's build, one after another.</summary>
    internal void StartUpdate(FabItemViewModel item, IReadOnlyList<FabVersion> downloads, IReadOnlyList<FabInstall> installs)
    {
        Owner.StartOperation(new OperationViewModel(Owner, item.Title, new OperationAction(OperationKind.Update), async (operation, _, cancellationToken) =>
        {
            foreach (var version in downloads)
            {
                var status = operation.BeginPhase(Localized.Format(Strings.PhaseUpdatingVaultCopyFor, version.EngineAppName[3..]));
                var artifact = Artifact(item, version);
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                await FabWorkflow.DownloadToVaultAsync(source, artifact, Owner.Settings.ResolveVaultCache().Path, Owner.CreateInstaller(), status, cancellationToken);
            }
            foreach (var install in installs)
            {
                string engine = install.EngineAppName[3..];
                var version = item.Versions.First(v => string.Equals(v.ArtifactID, install.ArtifactID, StringComparison.OrdinalIgnoreCase));
                var artifact = Artifact(item, version);
                var checking = operation.BeginPhase(Localized.Format(Strings.PhaseCheckingEngine, engine));
                var source = await FabWorkflow.FetchAsync(Owner.Services.API, artifact, cancellationToken);
                var update = await FabWorkflow.PlanPluginUpdateAsync(source, artifact, install.EngineDirectory, checking, cancellationToken);
                var status = operation.BeginPhase(Localized.Format(Strings.PhaseUpdatingInEngine, engine));
                await FabWorkflow.ApplyPluginUpdateAsync(update, Owner.CreateInstaller(), status, cancellationToken);
            }
            int count = downloads.Count + installs.Count;
            return Localized.Plural(nameof(Strings.UpdateDetail_Other), count);
        }, engines: [.. installs.Select(i => i.EngineAppName).Distinct(StringComparer.OrdinalIgnoreCase)], fabItemKey: item.Key));
    }

    private static FabArtifact Artifact(FabItemViewModel item, FabVersion version) =>
        item.Library is not null && version.Library is not null
            ? new FabArtifact(item.Library, version.Library)
            : throw new InstallException(Strings.SignInToDownloadItem);
}
