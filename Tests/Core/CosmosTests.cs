using System.Net;
using System.Text;
using UnVault.Core.Cosmos;

namespace UnVault.Core.Tests;

/// <summary>Leartes' Cosmos store: what an account owns there, and the sign-in kept for it.</summary>
public sealed class CosmosTests : IDisposable
{
    private readonly string _tokenPath = Path.Combine(Path.GetTempPath(), $"unvault-cosmos-{Guid.NewGuid():N}.dat");

    public void Dispose() => File.Delete(_tokenPath);

    [Fact]
    public async Task Reads_the_inventory_page_by_page_under_its_own_name_and_leaves_bundles_out()
    {
        var requests = new List<HttpRequestMessage>();
        var http = new HttpClient(new FakeCosmos(request =>
        {
            requests.Add(request);
            return JSON(request.RequestUri!.Query == "?page=1"
                ? """
                  { "data": [
                      { "id": "a1", "title": "1950s Businessman", "slug": "1950s-businessman", "type": "asset", "service": "leartes", "cover_image": { "url": "https://cdn.cosmos.leartesstudios.com/a1.png" } },
                      { "id": "b1", "title": "Mega Bundle", "slug": "mega-bundle", "type": "bundle", "service": "leartes" },
                      null ],
                    "meta": { "current_page": 1, "last_page": 2 } }
                  """
                : """{ "data": [ { "id": "a2", "title": "Victorian Street", "slug": "victorian-street", "type": "environment" } ], "meta": { "last_page": 2 } }""");
        }));
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UELauncher/11.0.1 Windows"); // as the app's client has it, for Epic
        var read = new List<int>();

        var items = await new CosmosClient(http).GetInventoryAsync("the-token", new SynchronousProgress(read.Add));

        Assert.Equal(["a1", "a2"], items.Select(i => i.ID));
        Assert.Equal(("1950s Businessman", "1950s-businessman", "asset", "leartes", "https://cdn.cosmos.leartesstudios.com/a1.png"),
            (items[0].Title, items[0].Slug, items[0].Type, items[0].Service, items[0].CoverImage?.URL));
        Assert.Equal([1, 2], read);
        Assert.Equal(["https://api.cosmos.leartesstudios.com/inventory?page=1", "https://api.cosmos.leartesstudios.com/inventory?page=2"], requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(requests, r => Assert.Equal("Bearer the-token", r.Headers.Authorization!.ToString()));
        Assert.All(requests, r => Assert.StartsWith("UnVaultLauncher/", r.Headers.UserAgent.ToString())); // not as the Epic Games Launcher
        Assert.Equal("https://cosmos.leartesstudios.com/product/1950s-businessman", CosmosEndpoints.ProductURL(items[0].Slug));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Tells_a_sign_in_Cosmos_turns_down_from_other_trouble(HttpStatusCode status, bool signedOut)
    {
        var http = new HttpClient(new FakeCosmos(_ => new HttpResponseMessage(status)));

        var error = await Assert.ThrowsAsync<CosmosException>(() => new CosmosClient(http).GetInventoryAsync("old-token"));

        Assert.Equal((status, signedOut), (error.StatusCode, error.IsSignedOut));
        Assert.DoesNotContain("old-token", error.Message);
    }

    [Fact]
    public async Task An_answer_of_another_shape_reads_as_a_Cosmos_problem()
    {
        var http = new HttpClient(new FakeCosmos(_ => JSON("""{ "data": "soon" }""")));

        var error = await Assert.ThrowsAsync<CosmosException>(() => new CosmosClient(http).GetInventoryAsync("token"));

        Assert.False(error.IsSignedOut);
    }

    /// <summary>An environment carries its Unreal build's details; a single asset has them beside it. Either is read, and what isn't there is left out.</summary>
    [Fact]
    public async Task Reads_what_a_product_page_tells_without_a_sign_in_whatever_its_shape()
    {
        var http = new HttpClient(new FakeCosmos(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return JSON(request.RequestUri!.ToString() switch
            {
                "https://api.cosmos.leartesstudios.com/products/victorian-street" => """
                    { "title": "Victorian Street", "description": null, "subtitle": "Cobbled and foggy", "tags": ["victorian", 1890, null], "updatedAt": "2026-11-18T09:45:51.000+00:00",
                      "plugin": { "folder": "SB_Victorian_Street" },
                      "details": { "genres": ["historical"], "folder": "ignored_for_the_plugins", "features": "139 Unique Meshes\n\n\n\n16 Blueprints",
                        "ue": { "description": "<p>A foggy street.</p>", "versions": ["4.27", "5.4", "latest"], "mesh_numbers": 310, "texture_number": "512", "texture_resolutions": [2048, 4096],
                                "file_sizes": [ { "size": "1.19", "version": ["5.4"] }, { "size": "", "version": ["4.27"] }, "odd" ],
                                "material_instances": "106", "collision": false,
                                "images": [ { "url": "https://cdn.cosmos.leartesstudios.com/images/s1.webp" }, { "url": "http://plain.example/s2.webp" }, { "name": "no link" } ] } } }
                    """,
                "https://api.cosmos.leartesstudios.com/products/wooden-railing" => """
                    { "description": "A weathered railing.", "tags": ["railing"], "updatedAt": "soon",
                      "details": { "object_type": "prop", "polycount": { "lod0": "8650", "lod1": "6506" }, "texture_resolutions": [512, 1024], "threed_formats": ["fbx", "obj"],
                        "folder": "JG_Wooden_Railing", "lods": ["nanite", "lod0"], "format": "wav",
                        "images": [ { "url": "https://cdn.cosmos.leartesstudios.com/images/r1.webp" }, { "url": "https://cdn.cosmos.leartesstudios.com/images/r1.webp" } ] } }
                    """,
                _ => """{ "details": "none", "tags": {} }""", // nothing as expected: still a product, with nothing known of it
            });
        }));
        var client = new CosmosClient(http);

        var street = await client.GetProductAsync("victorian-street");
        Assert.Equal(("<p>A foggy street.</p>", 310L, 512L, null), (street.Description, street.Meshes, street.Textures, street.Polygons)); // shown as plain text later
        Assert.Equal(["victorian", "1890"], street.Tags);
        Assert.Equal(["historical"], street.Genres);
        Assert.Equal(["4.27", "5.4"], street.EngineVersions); // only what reads as a version
        Assert.Equal(["2048", "4096"], street.TextureResolutions);
        Assert.Equal(["https://cdn.cosmos.leartesstudios.com/images/s1.webp"], street.Images);
        Assert.Equal(new DateTime(2026, 11, 18), street.UpdatedAt!.Value.UtcDateTime.Date);
        Assert.True(DateTimeOffset.UtcNow - street.FetchedAt < TimeSpan.FromMinutes(1));
        Assert.Equal(("SB_Victorian_Street", 1, 1.19, CosmosProductCache.Fields), (street.Folder, street.FileSizes.Count, street.FileSizes[0].Gigabytes, street.Fields));
        Assert.Equal(["5.4"], street.FileSizes[0].Versions);
        Assert.Equal(("139 Unique Meshes\n\n\n\n16 Blueprints", 106L, false), (street.Features, street.MaterialInstances, street.Collision));

        var railing = await client.GetProductAsync("wooden-railing");
        Assert.Equal(("A weathered railing.", "prop", 8650L, null), (railing.Description, railing.ObjectType, railing.Polygons, railing.UpdatedAt));
        Assert.Equal(["512", "1024"], railing.TextureResolutions);
        Assert.Equal(["fbx", "obj"], railing.Formats);
        Assert.Equal(["https://cdn.cosmos.leartesstudios.com/images/r1.webp"], railing.Images); // each once
        Assert.Empty(railing.EngineVersions);
        Assert.Equal("JG_Wooden_Railing", railing.Folder);
        Assert.Equal(["nanite", "lod0"], railing.Lods);
        Assert.Equal(["wav"], railing.AudioFormats);
        Assert.Equal((null, null, null), (railing.Features, railing.MaterialInstances, railing.Collision)); // not said: not shown

        var odd = await client.GetProductAsync("something else");
        Assert.Equal((null, 0, 0), (odd.Description, odd.Tags.Count, odd.Images.Count));
    }

    [Fact]
    public void Product_details_are_kept_and_asked_for_again_only_after_a_while()
    {
        string path = _tokenPath + ".products.json";
        try
        {
            CosmosProductCache.Save(new()
            {
                ["fresh"] = new CosmosProduct
                {
                    Genres = ["historical"], EngineVersions = ["5.4"], Polygons = 8650, Folder = "SB_Street", FetchedAt = DateTimeOffset.UtcNow.AddDays(-1), Fields = CosmosProductCache.Fields,
                    FileSizes = [new CosmosFileSize { Gigabytes = 1.5, Versions = ["5.4"] }, new CosmosFileSize { Gigabytes = 2, Versions = ["5.5", "5.6"] }],
                },
                ["old"] = new CosmosProduct { FetchedAt = DateTimeOffset.UtcNow - CosmosProductCache.MaxAge - TimeSpan.FromHours(1), Fields = CosmosProductCache.Fields },
                ["thinner"] = new CosmosProduct { FetchedAt = DateTimeOffset.UtcNow.AddDays(-1) }, // read before what a download asks by was kept
            }, path);

            var products = CosmosProductCache.Load(path);

            Assert.Equal(["historical"], products["fresh"].Genres);
            Assert.Equal(["5.4"], products["fresh"].EngineVersions);
            Assert.Equal(8650L, products["fresh"].Polygons);
            Assert.Equal("SB_Street", products["fresh"].Folder);
            // What its files come to: for the engine asked, else the first Cosmos lists; a GB is 1024 MB here as everywhere.
            Assert.Equal((2L << 30, 3L << 29, 3L << 29), (products["fresh"].SizeFor("5.6"), products["fresh"].SizeFor("4.27"), products["fresh"].SizeFor(null)));
            Assert.Null(products["old"].SizeFor("5.4"));
            Assert.Equal((false, true, true, true),
                (CosmosProductCache.IsDue(products, "fresh"), CosmosProductCache.IsDue(products, "old"), CosmosProductCache.IsDue(products, "thinner"), CosmosProductCache.IsDue(products, "never-read")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_sign_in_in_the_browser_is_asked_after_until_Cosmos_has_a_token()
    {
        int asked = 0;
        var http = new HttpClient(new FakeCosmos(request =>
        {
            Assert.Equal("https://api.cosmos.leartesstudios.com/auth/unreal/exchange", request.RequestUri!.ToString());
            return ++asked switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"status":"pending"}""") },
                2 => JSON("""{ "token": "the-token" }"""),
                3 => JSON("""{ "token": "" }"""),
                _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            };
        }));
        var client = new CosmosClient(http);
        var signIn = CosmosClient.StartBrowserSignIn("github");
        Assert.StartsWith("https://api.cosmos.leartesstudios.com/auth/github?unreal=true&code_challenge=", signIn.URL);
        Assert.Equal(64, signIn.Verifier.Length);
        Assert.NotEqual(signIn.Verifier, CosmosClient.StartBrowserSignIn("github").Verifier); // a new one each time

        Assert.Null(await client.TryFinishBrowserSignInAsync(signIn)); // not yet
        Assert.Equal("the-token", await client.TryFinishBrowserSignInAsync(signIn));
        await Assert.ThrowsAsync<CosmosException>(() => client.TryFinishBrowserSignInAsync(signIn)); // done, but with nothing to sign in with
        var refused = await Assert.ThrowsAsync<CosmosException>(() => client.TryFinishBrowserSignInAsync(signIn));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Only the ways Cosmos hands to the browser: nothing else is put into its address.
        Assert.Throws<ArgumentException>(() => CosmosClient.StartBrowserSignIn("google?redirect=https://example.com"));
    }

    [Fact]
    public void The_token_is_kept_out_of_plain_sight_and_gone_after_sign_out()
    {
        var store = new CosmosTokenStore(_tokenPath);
        Assert.Null(store.Load());

        store.Save("eyJ-the-token");

        Assert.Equal("eyJ-the-token", new CosmosTokenStore(_tokenPath).Load());
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("the-token", Encoding.UTF8.GetString(File.ReadAllBytes(_tokenPath)));

        File.WriteAllText(_tokenPath, "not what was saved");
        if (OperatingSystem.IsWindows())
            Assert.Null(store.Load()); // damaged, or another user's: signed out

        store.Delete();
        Assert.False(File.Exists(_tokenPath));
        Assert.Null(store.Load());
    }

    private static HttpResponseMessage JSON(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeCosmos(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Reports at once, where Progress would hand each report to another thread.</summary>
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
