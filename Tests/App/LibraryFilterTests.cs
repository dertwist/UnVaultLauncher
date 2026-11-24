using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using UnVault.App.Services;
using UnVault.App.ViewModels;
using UnVault.Core.Cosmos;
using UnVault.Core.Epic;
using UnVault.Core.Fab;
using UnVault.Core.Projects;
using UnVault.Core.Vault;

namespace UnVault.App.Tests;

/// <summary>
/// The Library's filters and order: by what the library says of an item (category, seller), what its listing on Fab adds
/// (type, tags, when it was updated), and what the user marked (favorites, hidden).
/// </summary>
public sealed class LibraryFilterTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"unvault-library-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Maple = "1f65d477-f44e-4ccb-95c1-788c99e33b56", Greybox = "2a000000-0000-0000-0000-000000000002", Village = "3a000000-0000-0000-0000-000000000003",
        Mine = "4eb4b87c-92bf-495a-b68a-5877fc07094f";

    private static FabLibraryItem Item(string title, string seller, string category, string method, string listingID, params string[] engines) =>
        new()
        {
            Title = title, Seller = seller, AssetID = title, AssetNamespace = "fab", DistributionMethod = method,
            Categories = [new FabCategory { ID = category, Name = category }], URL = "https://www.fab.com/listings/" + listingID,
            ProjectVersions = [.. engines.Select(e => new FabProjectVersion
            {
                ArtifactID = $"{title}_{e}", EngineVersions = [e], BuildVersions = [new FabBuildVersion { BuildVersion = "build-2", Platform = "Windows" }],
            })],
        };

    private static List<FabLibraryItem> Library() =>
    [
        Item("Norway Maple", "Quixel", "Trees", "ASSET_PACK", Maple, "UE_5.6", "UE_5.7"),
        Item("Greybox Tools", "Northwind Tools", "Engine Tools", "CODE_PLUGIN", Greybox, "UE_5.7"),
        Item("Hillside Village", "Contoso Art", "Towns & Villages", "ASSET_PACK", Village, "UE_5.5"),
    ];

    private FabLibraryViewModel Fab(AppServices? services = null)
    {
        var owner = new MainViewModel(services ?? new AppServices(), Path.Combine(_dir, "history.json")) { IsSignedIn = true };
        owner.Settings.VaultCacheDirectory = Directory.CreateDirectory(Path.Combine(_dir, "vault")).FullName; // set, so a load doesn't show this PC's
        owner.Settings.CosmosDirectory = Path.Combine(_dir, "cosmos"); // and none of its Cosmos downloads
        owner.Fab.MarksPath = Path.Combine(_dir, "marks.json");
        owner.Fab.ListingsPath = Path.Combine(_dir, "listings.json");
        return owner.Fab;
    }

    private static string[] Titles(FabLibraryViewModel fab) => [.. fab.Items.Select(i => i.Title)];

    [AvaloniaFact]
    public void Filters_by_category_seller_and_what_the_listing_says()
    {
        var fab = Fab();
        fab.Listings = new()
        {
            [Maple] = new FabListing { ListingType = "3d-model", Tags = [new FabTag { Name = "Photogrammetry" }], PublishedAt = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero) },
            [Greybox] = new FabListing { ListingType = "tool-and-plugin", PublishedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) },
        };
        fab.Marks.FirstSeen["fab:fab/Hillside Village"] = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        fab.SetItems(Library(), [], []);

        Assert.Equal(["All Categories", "Engine Tools", "Towns & Villages", "Trees"], fab.CategoryOptions);
        Assert.Equal(["All Sellers", "Contoso Art", "Northwind Tools", "Quixel"], fab.SellerOptions);
        Assert.Equal(["All Listing Types", "3d model", "tool and plugin"], fab.ListingOptions);

        fab.CategoryOption = "Trees";
        Assert.Equal(["Norway Maple"], Titles(fab));
        fab.CategoryOption = "All Categories";
        fab.SellerOption = "Contoso Art";
        Assert.Equal(["Hillside Village"], Titles(fab));
        fab.SellerOption = "All Sellers";
        fab.ListingOption = "tool and plugin";
        Assert.Equal(["Greybox Tools"], Titles(fab));
        fab.ListingOption = "All Listing Types";

        // The search reaches a listing's tags and type.
        Assert.Single(fab.Items, i => i.Matches("photogram"));
        Assert.Single(fab.Items, i => i.Matches("3d-model"));

        // Newest: the one the library listed last, then Fab's own order backwards, as it lists what was got first first.
        Assert.Equal(["Newest", "Oldest", "Title A-Z", "Title Z-A", "Last Updated"], fab.SortOptions);
        Assert.Equal(["Hillside Village", "Greybox Tools", "Norway Maple"], Titles(fab));
        fab.SortIndex = 1;
        Assert.Equal(["Norway Maple", "Greybox Tools", "Hillside Village"], Titles(fab));
        fab.SortIndex = 2;
        Assert.Equal(["Greybox Tools", "Hillside Village", "Norway Maple"], Titles(fab));
        fab.SortIndex = 3;
        Assert.Equal(["Norway Maple", "Hillside Village", "Greybox Tools"], Titles(fab));
        fab.SortIndex = 4;
        Assert.Equal(["Norway Maple", "Greybox Tools", "Hillside Village"], Titles(fab)); // one not read from Fab comes last
    }

    /// <summary>Downloads the library doesn't list have no place in its order: they come after it, by title, newest or oldest first.</summary>
    [AvaloniaFact]
    public void Vault_Cache_copies_not_in_the_library_come_last()
    {
        var fab = Fab();
        var vault = new VaultEntry { ArtifactID = "Temple_4.27", Title = "Abandoned Temple", Version = "4.27.0-1", Build = "build-1", Directory = Path.Combine(_dir, "Temple") };
        fab.SetItems(Library(), [vault], []);

        Assert.Equal(["Hillside Village", "Greybox Tools", "Norway Maple", "Abandoned Temple"], Titles(fab));
        fab.SortIndex = 1;
        Assert.Equal(["Norway Maple", "Greybox Tools", "Hillside Village", "Abandoned Temple"], Titles(fab));
    }

    [AvaloniaFact]
    public void An_items_preview_tells_what_is_known_of_it_and_follows_a_reload()
    {
        var fab = Fab();
        fab.Listings = new()
        {
            [Maple] = new FabListing { ListingType = "3d-model", Tags = [new FabTag { Name = "Tree" }], PublishedAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero) },
        };
        var library = Library();
        library[0].Description = "<p>A tree pack.</p><p>With seasons.</p>";
        fab.SetItems(library, [], []);
        var maple = fab.Items.Single(i => i.Title == "Norway Maple");

        maple.ShowPreviewCommand.Execute(null);

        Assert.Same(maple, fab.Preview);
        Assert.True(maple.IsPreviewed);
        Assert.Equal(["Type", "Category", "Listing Type", "Engine Versions", "Last Updated"], maple.Details.Select(d => d.Label));
        Assert.Equal(["Asset Pack", "Trees", "3d model", "UE 5.6, 5.7"], maple.Details.Take(4).Select(d => d.Value));
        Assert.Equal(["Tree"], maple.Tags);
        Assert.Equal("A tree pack.\n\nWith seasons.", maple.DescriptionText);

        // A reload makes the items anew: the preview goes on with the one for the same product.
        fab.SetItems(library, [], []);
        Assert.NotSame(maple, fab.Preview);
        Assert.Equal((maple.Key, false, true), (fab.Preview!.Key, maple.IsPreviewed, fab.Preview.IsPreviewed));

        // Another item takes its place; closing ends it.
        var greybox = fab.Items.Single(i => i.Title == "Greybox Tools");
        greybox.ShowPreviewCommand.Execute(null);
        Assert.False(fab.Items.Single(i => i.Title == "Norway Maple").IsPreviewed);
        Assert.Equal(["Type", "Category", "Engine Versions"], greybox.Details.Select(d => d.Label)); // nothing read from Fab about it
        Assert.Null(greybox.DescriptionText);
        greybox.ClosePreviewCommand.Execute(null);
        Assert.Null(fab.Preview);
        Assert.False(greybox.IsPreviewed);
    }

    [AvaloniaFact]
    public void Favorites_and_hidden_items_are_kept_and_change_what_shows()
    {
        var fab = Fab();
        fab.SetItems(Library(), [], []);
        var maple = fab.Items.Single(i => i.Title == "Norway Maple");
        var village = fab.Items.Single(i => i.Title == "Hillside Village");
        Assert.Equal(("Add to Favorites", "Hide from Library"), (maple.FavoriteText, village.HiddenText));

        maple.ToggleFavoriteCommand.Execute(null);
        village.ToggleHiddenCommand.Execute(null);

        Assert.Equal(("Remove from Favorites", "Show in Library"), (maple.FavoriteText, village.HiddenText));
        Assert.Equal(["Greybox Tools", "Norway Maple"], Titles(fab)); // hidden: gone at once
        fab.ShowHidden = true;
        Assert.Equal(3, fab.Items.Count);
        fab.FavoritesOnly = true;
        Assert.Equal(["Norway Maple"], Titles(fab));
        maple.ToggleFavoriteCommand.Execute(null);
        Assert.Empty(fab.Items); // no longer one: gone from the favorites at once
        maple.ToggleFavoriteCommand.Execute(null);

        // Kept in the marks file: another start shows the same.
        var later = Fab();
        later.SetItems(Library(), [], []);
        Assert.Equal(["Greybox Tools", "Norway Maple"], Titles(later));
        Assert.True(later.Items.Single(i => i.Title == "Norway Maple").IsFavorite);
    }

    [AvaloniaFact]
    public void Download_Shown_takes_the_engine_chosen_or_the_newest_and_leaves_out_what_the_Vault_Cache_has()
    {
        var fab = Fab();
        var vault = new VaultEntry { ArtifactID = "Hillside Village_UE_5.5", Title = "Hillside Village", Version = "5.5.0-1", Build = "build-2", Directory = Path.Combine(_dir, "Village") };
        Directory.CreateDirectory(vault.DataDirectory);
        File.WriteAllBytes(vault.ManifestPath, [0]);
        fab.SetItems(Library(), [vault], []);
        Assert.True(fab.Items.Single(i => i.Title == "Hillside Village").IsDownloaded);

        // Maple's newest (5.7) and Greybox; the village is there already.
        Assert.Equal("Download Shown (2)", fab.DownloadShownText);

        fab.EngineOption = "UE 5.6";
        Assert.Equal((1, true), (fab.DownloadShownCount, fab.DownloadShownCommand.CanExecute(null))); // only Maple is for 5.6
        fab.EngineOption = "UE 5.5";
        Assert.Equal((0, false), (fab.DownloadShownCount, fab.DownloadShownCommand.CanExecute(null)));

        // Nothing starts before the user has seen how many.
        fab.EngineOption = "All Engines";
        fab.DownloadShownCommand.Execute(null);
        var confirm = Assert.IsType<ConfirmViewModel>(fab.Owner.Dialog);
        Assert.Equal("Download 2 Items?", confirm.Title);
        Assert.Empty(fab.Owner.Operations);
    }

    [AvaloniaFact]
    public async Task Downloads_started_together_run_one_at_a_time()
    {
        var owner = Fab().Owner;
        var queue = new object();
        OperationViewModel Endless(string item, object? inQueue) => new(owner, item, new(OperationKind.Download, "5.7"), async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }, fabItemKey: item, queue: inQueue);
        var first = Endless("first", queue);
        var second = Endless("second", queue);
        var apart = Endless("apart", null);

        owner.StartOperations([first, second]);
        owner.StartOperation(apart);

        Assert.Equal((true, true, true), (first.IsRunning, second.IsWaiting, apart.IsRunning));
        foreach (var operation in new[] { second, first, apart })
        {
            operation.PauseCommand.Execute(null);
            await operation.Run;
        }
    }

    [AvaloniaFact]
    public async Task Reads_each_listing_once_and_keeps_it()
    {
        var requests = new List<string>();
        var services = new AppServices(new HttpClient(new FakeFab(request =>
        {
            string id = request.RequestUri!.Segments[^1];
            lock (requests)
                requests.Add(id);
            Assert.Null(request.Headers.Authorization); // public: the account's token stays out of it
            return id == Village ? new HttpResponseMessage(HttpStatusCode.NotFound) // taken off Fab
                : id == Mine ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"detail":"Mature content hidden for user"}""") }
                : JSON($$"""{ "listingType": "{{(id == Maple ? "3d-model" : "tool-and-plugin")}}", "publishedAt": "2026-11-09T16:13:45.380610Z", "tags": [{ "name": "Tree" }] }""");
        })), new SessionStore(Path.Combine(_dir, "session.dat")));
        var fab = Fab(services);
        // The one Fab won't show comes first: those after it are still read.
        List<FabLibraryItem> library = [Item("Old Mine", "Contoso Art", "Horror", "ASSET_PACK", Mine, "UE_5.7"), .. Library()];
        fab.SetItems(library, [], []);

        await fab.ReadListingsAsync(library);

        Assert.Equal([Maple, Greybox, Village, Mine], requests.Order());
        Assert.Equal(["All Listing Types", "3d model", "tool and plugin"], fab.ListingOptions);
        Assert.Equal(new DateTime(2026, 11, 9), fab.Items.Single(i => i.Title == "Norway Maple").UpdatedAt!.Value.UtcDateTime.Date);

        // All four are kept, those Fab has nothing to say about too: none is asked for again, now or at the next start.
        await fab.ReadListingsAsync(library);
        Assert.Equal(4, requests.Count);
        Assert.Equal(4, FabListingCache.Load(fab.ListingsPath).Count);
    }

    [AvaloniaFact]
    public async Task A_Fab_items_technical_details_are_asked_for_when_its_preview_first_shows()
    {
        var requests = new List<string>();
        var services = new AppServices(new HttpClient(new FakeFab(request =>
        {
            lock (requests)
                requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return request.RequestUri.AbsolutePath.EndsWith("/asset-formats/unreal-engine")
                ? JSON("""{ "techDetails": { "threeDModel": { "additionalInformation": "<p>15 unique meshes</p>", "hasCollision": true } } }""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), new SessionStore(Path.Combine(_dir, "session.dat")));
        var fab = Fab(services);
        fab.Listings = new() { [Maple] = new FabListing { ListingType = "3d-model", FetchedAt = DateTimeOffset.UtcNow, Fields = FabListingCache.Fields } };
        fab.SetItems(Library(), [], []);
        var maple = fab.Items.Single(i => i.Title == "Norway Maple");

        maple.ShowPreviewCommand.Execute(null);
        ScreenshotTests.WaitFor(() => maple.TechnicalText is not null);

        Assert.Equal("15 unique meshes\n\nHas collision: Yes", maple.TechnicalText);
        Assert.Contains($"/i/listings/{Maple}/asset-formats/unreal-engine", requests);

        // Kept with the listing: the next time its preview shows, and the next start, ask nothing.
        ScreenshotTests.WaitFor(() => FabListingCache.Load(fab.ListingsPath).GetValueOrDefault(Maple)?.TechnicalDetails is not null);
        int asked = requests.Count;
        maple.ClosePreviewCommand.Execute(null);
        maple.ShowPreviewCommand.Execute(null);
        ScreenshotTests.WaitFor(() => maple.TechnicalText is not null);
        Assert.Equal(asked, requests.Count);

        // An item whose listing isn't read yet has none to ask for.
        var greybox = fab.Items.Single(i => i.Title == "Greybox Tools");
        greybox.ShowPreviewCommand.Execute(null);
        Assert.Null(greybox.TechnicalText);
        Assert.DoesNotContain(requests, r => r.Contains(Greybox));
    }

    [AvaloniaFact]
    public async Task Stops_reading_listings_when_Fab_turns_one_down()
    {
        int requests = 0;
        var services = new AppServices(new HttpClient(new FakeFab(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        })), new SessionStore(Path.Combine(_dir, "session.dat")));
        var fab = Fab(services);
        var library = Enumerable.Range(0, 9).Select(i => Item($"Item {i}", "Seller", "Props", "ASSET_PACK", $"4a000000-0000-0000-0000-00000000000{i}", "UE_5.7")).ToList();

        await fab.ReadListingsAsync(library);

        Assert.Equal(4, requests); // the first few; the rest wait for another time
        Assert.Empty(fab.Listings);
    }

    /// <summary>
    /// Cosmos, as the tests' own. A sign-in in the browser is "not yet" until <paramref name="done"/> (given how often it was
    /// asked after), then hands out the token; the inventory lists one item while <paramref name="accepts"/>; and the
    /// product's page tells of it.
    /// </summary>
    private (MainViewModel Owner, CosmosTokenStore Tokens, List<string> Verifiers, List<string> Opened) WithCosmos(Func<int, bool>? done = null, Func<bool>? accepts = null)
    {
        var tokens = new CosmosTokenStore(Path.Combine(_dir, "cosmos.dat"));
        var verifiers = new List<string>();
        var services = new AppServices(new HttpClient(new FakeFab(request =>
        {
            Assert.Equal("api.cosmos.leartesstudios.com", request.RequestUri!.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/auth/unreal/exchange":
                    Assert.Null(request.Headers.Authorization);
                    using (var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result))
                        verifiers.Add(body.RootElement.GetProperty("code_verifier").GetString()!);
                    return done?.Invoke(verifiers.Count) ?? true ? JSON("""{ "token": "browser-token" }""")
                        : new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"status":"pending"}""", Encoding.UTF8, "application/json") };
                case "/inventory":
                    Assert.Equal("Bearer browser-token", request.Headers.Authorization!.ToString());
                    return accepts?.Invoke() ?? true
                        ? JSON("""{ "data": [ { "id": "c1", "title": "Victorian Street", "slug": "victorian-street", "type": "environment", "service": "leartes" } ], "meta": { "last_page": 1 } }""")
                        : new HttpResponseMessage(HttpStatusCode.Unauthorized);
                case "/products/victorian-street":
                    Assert.Null(request.Headers.Authorization); // public, as the product's page is
                    return JSON("""
                        { "description": "<p>A foggy street.</p>", "tags": ["victorian", "cobblestone"], "updatedAt": "2026-11-18T09:45:51.000+00:00",
                          "details": { "genres": ["historical", "realistic"], "features": "139 Unique Meshes\n\n\n\n16 Blueprints",
                            "ue": { "versions": ["4.27", "5.4"], "collision": true, "images": [ { "url": "https://cdn.cosmos.leartesstudios.com/images/s1.webp" } ],
                                    "mesh_numbers": 310, "texture_number": "512", "texture_resolutions": [2048, 4096] } } }
                        """);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        })), new SessionStore(Path.Combine(_dir, "session.dat")), tokens);
        var owner = Fab(services).Owner;
        owner.Fab.ProductsPath = Path.Combine(_dir, "products.json");
        owner.CosmosSignInPoll = TimeSpan.FromMilliseconds(5);
        var opened = new List<string>();
        owner.OpenPage = url =>
        {
            opened.Add(url!);
            return true; // no browser opens in a test
        };
        return (owner, tokens, verifiers, opened);
    }

    [AvaloniaFact]
    public async Task Signing_in_to_Cosmos_lists_what_is_owned_there_beside_Fabs()
    {
        var (owner, tokens, _, _) = WithCosmos();
        var fab = owner.Fab;
        Assert.Equal((true, "Not signed in."), (owner.CanSignInToCosmos, owner.CosmosStatusText));

        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("google");
        fab.SetItems(Library(), [], []); // with a Fab library beside it

        Assert.Equal((true, "Sign Out of Cosmos", "browser-token"), (owner.IsCosmosSignedIn, owner.CosmosSignInText, tokens.Load()));
        Assert.StartsWith("Signed in.", owner.CosmosStatusText);
        Assert.Equal(4, fab.Items.Count);
        var street = Assert.Single(fab.Items, i => i.Cosmos is not null);
        Assert.Equal(("Victorian Street", "leartes", "environment"), (street.Title, street.SellerText, street.ListingType));
        Assert.Equal((false, false, true), (street.IsInLibrary, street.IsDownloaded, street.IsFromCosmos)); // not Fab's: Download Shown leaves it out

        // Its file in the Cosmos folder, once there, is found at the next look.
        Directory.CreateDirectory(owner.Settings.ResolveCosmosFolder());
        File.WriteAllBytes(Path.Combine(owner.Settings.ResolveCosmosFolder(), "victorian-street__5.4.zip"), new byte[2048]);
        await fab.LoadAsync(includeLibrary: false);
        fab.SetItems(Library(), [], []);
        street = Assert.Single(fab.Items, i => i.Cosmos is not null);
        Assert.Equal((true, true, "In your Cosmos folder: victorian-street__5.4.zip (2.0 KB)"), (street.IsDownloaded, street.HasFolder, street.DownloadedText));
        fab.DownloadedOnly = true;
        Assert.Equal(["Victorian Street"], Titles(fab));
        fab.DownloadedOnly = false;
        Assert.Contains("environment", fab.ListingOptions);
        Assert.Contains("leartes", fab.SellerOptions);

        // Found and filtered with the rest; newest, as it was listed last.
        Assert.Equal("Victorian Street", fab.Items[0].Title);
        Assert.Single(fab.Items, i => i.Matches("victorian-street"));
        fab.SourceIndex = 2;
        Assert.Equal(["Victorian Street"], Titles(fab));
        fab.SourceIndex = 1;
        Assert.Equal(3, fab.Items.Count);
        Assert.DoesNotContain(fab.Items, i => i.Cosmos is not null);
        fab.SourceIndex = 0;

        // Signing out takes them away, and the token with them.
        await owner.LeaveCosmosCommand.ExecuteAsync(null);
        Assert.Equal((false, null), (owner.IsCosmosSignedIn, tokens.Load()));
        Assert.DoesNotContain(fab.Items, i => i.Cosmos is not null);
    }

    /// <summary>The inventory only names an item; its page on Cosmos tells the rest, which is read after it.</summary>
    [AvaloniaFact]
    public async Task What_Cosmos_tells_of_a_product_fills_in_its_item()
    {
        var (owner, _, _, _) = WithCosmos();
        var fab = owner.Fab;
        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("github");
        fab.SetItems(Library(), [], []);

        await fab.ReadProductsAsync(fab.Cosmos!);

        var street = Assert.Single(fab.Items, i => i.Cosmos is not null);
        Assert.Equal(("historical", "UE 4.27, 5.4", "A foggy street."), (street.Category, street.EngineVersionsText, street.DescriptionText));
        Assert.Equal(["victorian", "cobblestone"], street.Tags);
        Assert.Equal(new DateTime(2026, 11, 18), street.UpdatedAt!.Value.UtcDateTime.Date);
        Assert.Equal(["Type", "Category", "Listing Type", "Engine Versions", "Last Updated", "Meshes", "Textures", "Texture Sizes", "Collision"], street.Details.Select(d => d.Label));
        Assert.Equal(["310", "512", "2048, 4096", "Yes"], street.Details.TakeLast(4).Select(d => d.Value));

        // Its preview tells what the pack includes, as Cosmos's page does under Features.
        Assert.Null(street.TechnicalText);
        street.ShowPreviewCommand.Execute(null);
        Assert.Equal("139 Unique Meshes\n\n16 Blueprints", street.TechnicalText);
        street.ClosePreviewCommand.Execute(null);

        // Its genre and engines join the filters, and the search reaches its tags and description.
        Assert.Contains("historical", fab.CategoryOptions);
        Assert.Contains("UE 5.4", fab.EngineOptions);
        fab.EngineOption = "UE 5.4";
        Assert.Equal(["Victorian Street"], Titles(fab));
        fab.EngineOption = "All Engines";
        Assert.Single(fab.Items, i => i.Matches("cobble"));
        Assert.Single(fab.Items, i => i.Matches("foggy"));

        // Kept: not asked for again, now or at the next start.
        Assert.False(CosmosProductCache.IsDue(CosmosProductCache.Load(fab.ProductsPath), "victorian-street"));
    }

    /// <summary>A Cosmos pack is a project's files, so once Cosmos has told its engines it's handled as Fab's packs are.</summary>
    [AvaloniaFact]
    public async Task A_Cosmos_pack_goes_into_projects_and_becomes_one_as_Fabs_do()
    {
        var (owner, _, _, _) = WithCosmos();
        var fab = owner.Fab;
        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("google");
        await fab.ReadProductsAsync(fab.Cosmos!);
        var street = Assert.Single(fab.Items);
        Assert.Equal((FabItemKind.AssetPack, "Asset Pack", "Add to Project", true), (street.Kind, street.KindLabel, street.PrimaryText, street.ShowsCreateProject));
        Assert.Equal(["UE_4.27", "UE_5.4"], street.Versions.Select(v => v.EngineAppName));
        fab.KindIndex = 2; // asset packs
        Assert.Equal(["Victorian Street"], Titles(fab));
        fab.KindIndex = 0;

        // Its button opens the projects to add it to: the version for each project's engine, as for a Fab pack.
        street.PrimaryCommand.Execute(null);
        var dialog = Assert.IsType<FabActionViewModel>(owner.Dialog);
        Assert.Equal(FabActionMode.AddToProject, dialog.Mode);
        UnrealProject Project(string name, string engine) => new(name, Path.Combine(_dir, "Projects", name, name + ".uproject"), engine, null);
        dialog.ShowProjects([Project("Arena", "5.4"), Project("Harbor", "5.7"), Project("Relic", "4.26")]);
        FabTargetViewModel Row(string name) => dialog.Targets.Single(t => t.Title == name);
        Assert.Equal((true, "UE_5.4", null), (Row("Arena").IsUsable, Row("Arena").Version?.EngineAppName, Row("Arena").Badge));
        Assert.Equal((true, "UE_5.4"), (Row("Harbor").IsUsable, Row("Harbor").Version?.EngineAppName)); // the newest made for an older engine
        Assert.False(Row("Relic").IsUsable); // every version is for a newer engine
        Row("Arena").IsChecked = true;
        Assert.Equal("Downloads into your Cosmos folder and adds it to the project.", dialog.Note);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));

        // With its zip for that version in the Cosmos folder already, nothing is downloaded.
        Directory.CreateDirectory(owner.Settings.ResolveCosmosFolder());
        File.WriteAllBytes(Path.Combine(owner.Settings.ResolveCosmosFolder(), "victorian-street__5.4.zip"), [0]);
        await fab.LoadAsync(includeLibrary: false);
        street = Assert.Single(fab.Items);
        street.CreateProjectCommand.Execute(null);
        dialog = Assert.IsType<FabActionViewModel>(owner.Dialog);
        Assert.Equal(FabActionMode.CreateProject, dialog.Mode);
        Assert.Equal(["UE_5.4", "UE_4.27"], dialog.VersionChoices.Select(c => c.Version.EngineAppName));
        Assert.Equal("Already in your Cosmos folder: nothing to download.", dialog.Note);
        Assert.Equal("VictorianStreet", dialog.ProjectName);
        dialog.SelectedVersion = dialog.VersionChoices[1];
        Assert.Equal("Downloads into your Cosmos folder and creates the project.", dialog.Note);
    }

    [AvaloniaFact]
    public async Task A_sign_in_Cosmos_no_longer_accepts_ends_here_too()
    {
        bool accepted = true;
        var (owner, tokens, _, _) = WithCosmos(accepts: () => accepted);
        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("google");
        Assert.Single(owner.Fab.Items);

        accepted = false;
        await owner.Fab.LoadAsync(includeLibrary: true);

        Assert.Equal((false, null), (owner.IsCosmosSignedIn, tokens.Load()));
        Assert.Empty(owner.Fab.Items);
        Assert.Contains("Cosmos", owner.Notice);
    }

    [AvaloniaFact]
    public async Task Signing_in_to_Cosmos_in_the_browser_waits_until_Cosmos_says_its_done()
    {
        var (owner, tokens, verifiers, opened) = WithCosmos(done: asked => asked == 3);

        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("google");

        // The browser got the hash of what only the app holds, so the page there can't be used to take the sign-in.
        const string Start = "https://api.cosmos.leartesstudios.com/auth/google?unreal=true&code_challenge=";
        string page = Assert.Single(opened);
        Assert.StartsWith(Start, page);
        Assert.Equal(3, verifiers.Count);
        Assert.Single(verifiers.Distinct());
        Assert.Equal(page[Start.Length..], System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifiers[0]))));
        Assert.DoesNotContain(verifiers[0], page);

        Assert.Equal((true, false, "browser-token", null), (owner.IsCosmosSignedIn, owner.IsCosmosSigningIn, tokens.Load(), owner.Notice));
        Assert.Single(owner.Fab.Items, i => i.Cosmos is not null);
    }

    [AvaloniaFact]
    public async Task A_Cosmos_sign_in_in_the_browser_can_be_cancelled_and_isnt_waited_for_forever()
    {
        var (owner, tokens, _, _) = WithCosmos(done: _ => false);

        var waiting = owner.SignInToCosmosInBrowserCommand.ExecuteAsync("github");
        Assert.Equal((true, false, "Cancel Cosmos Sign-In"), (owner.IsCosmosSigningIn, owner.CanSignInToCosmos, owner.CosmosSignInText));
        Assert.Contains("browser", owner.CosmosStatusText);
        await owner.LeaveCosmosCommand.ExecuteAsync(null);
        await waiting;
        Assert.Equal((false, true, null, null), (owner.IsCosmosSigningIn, owner.CanSignInToCosmos, owner.Notice, tokens.Load()));

        owner.CosmosSignInWait = TimeSpan.FromMilliseconds(80);
        await owner.SignInToCosmosInBrowserCommand.ExecuteAsync("github");
        Assert.Equal((false, false), (owner.IsCosmosSigningIn, owner.IsCosmosSignedIn));
        Assert.Contains("in time", owner.Notice);
    }

    /// <summary>As with the project categories: whatever is wrong with the file, the marks in it aren't lost.</summary>
    [Fact]
    public void A_marks_file_that_is_damaged_or_cant_be_read_is_never_saved_over()
    {
        string file = Path.Combine(_dir, "marks.json");
        const string Damaged = "{ \"Favorites\": [ \"fab:fab/Norway Map";
        File.WriteAllText(file, Damaged);

        var marks = LibraryMarks.Load(file);
        Assert.Empty(marks.Favorites);
        marks.Hidden.Add("fab:fab/Greybox Tools");
        marks.Save(file);
        Assert.Equal(["fab:fab/Greybox Tools"], LibraryMarks.Load(file).Hidden);
        Assert.Equal(Damaged, File.ReadAllText(file + ".bad")); // set aside, to be recovered

        // Another program has it open: nothing wrong with it, so it's left alone.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            marks = LibraryMarks.Load(file);
        marks.Favorites.Add("fab:fab/Hillside Village");
        marks.Save(file);
        Assert.Equal((0, 1), (LibraryMarks.Load(file).Favorites.Count, LibraryMarks.Load(file).Hidden.Count));

        // Edited by hand, with lists left out.
        File.WriteAllText(file, """{ "Favorites": null }""");
        Assert.Equal((0, 0, 0), (LibraryMarks.Load(file).Favorites.Count, LibraryMarks.Load(file).Hidden.Count, LibraryMarks.Load(file).FirstSeen.Count));
    }

    private static HttpResponseMessage JSON(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeFab(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
