using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UnVault.Core.Cosmos;
using UnVault.Core.Install;

namespace UnVault.Core.Tests;

/// <summary>Getting a product's files from Cosmos: asked for as its site asks, and downloaded so a stop isn't a loss.</summary>
public sealed class CosmosDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"unvault-cosmos-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static CosmosItem Item(string type) => new() { ID = "c1", Title = "Victorian Street", Slug = "victorian-street", Type = type };

    [Fact]
    public void Each_kind_of_product_is_asked_for_its_own_way()
    {
        var street = new CosmosProduct { Folder = "SB_Victorian_Street", EngineVersions = ["5.3", "5.5", "4.27"] };
        Assert.Equal(["""{"platform":"ue","version":"5.5"}""", """{"name":"SB_Victorian_Street","type":"environment","platform":"ue"}"""],
            CosmosDownloads.OptionsFor(Item("environment"), street).Select(o => o.ToJSON())); // its newest
        Assert.Equal("5.3", CosmosDownloads.OptionsFor(Item("environment"), street, "5.3")[0].Version); // or the one wanted
        Assert.Equal("5.5", CosmosDownloads.OptionsFor(Item("environment"), street, "5.8")[0].Version); // which it has to come in

        var railing = new CosmosProduct { Folder = "JG_Wooden_Railing", Lods = ["lod0", "lod1", "nanite"] };
        Assert.Equal(["nanite", "high", "medium"], CosmosDownloads.OptionsFor(Item("asset"), railing).Select(o => o.Quality));
        Assert.Equal("""{"name":"JG_Wooden_Railing","type":"asset","platform":"ue","quality":"nanite"}""", CosmosDownloads.OptionsFor(Item("asset"), railing)[0].ToJSON());

        var bricks = new CosmosProduct { TextureResolutions = ["1024", "4096", "2048"] };
        Assert.Equal(["""{"name":"victorian-street","type":"material","platform":"ue","resolution":4096}""", "2048", "1024"],
            CosmosDownloads.OptionsFor(Item("material"), bricks).Select((o, i) => i == 0 ? o.ToJSON() : o.Variant)); // its name in links, having no other

        Assert.Equal(["""{"name":"victorian-street","type":"sound","format":"wav"}""", """{"name":"victorian-street","type":"sound","format":"ogg"}"""],
            CosmosDownloads.OptionsFor(Item("sound"), new CosmosProduct { AudioFormats = ["ogg", "wav"] }).Select(o => o.ToJSON()));
        Assert.Equal(["""{"name":"victorian-street","type":"vfx","platform":"ue"}"""], CosmosDownloads.OptionsFor(Item("vfx"), null).Select(o => o.ToJSON()));
    }

    [Fact]
    public async Task Tries_the_next_way_when_Cosmos_hasnt_this_one_and_stops_when_its_about_the_account()
    {
        var bodies = new List<string>();
        HttpStatusCode refusal = HttpStatusCode.NotFound;
        var http = new HttpClient(new FakeCosmos(request =>
        {
            Assert.Equal("https://api.cosmos.leartesstudios.com/products/download/victorian-street", request.RequestUri!.ToString());
            Assert.Equal("Bearer the-token", request.Headers.Authorization!.ToString());
            bodies.Add(request.Content!.ReadAsStringAsync().Result);
            return bodies.Count == 1 ? new HttpResponseMessage(refusal)
                : JSON("""{ "url": "https://files.cosmos.example/packs/SB_Victorian_Street_55.zip?signature=abc" }""");
        }));
        var options = CosmosDownloads.OptionsFor(Item("environment"), new CosmosProduct { EngineVersions = ["5.5"] });

        var (link, option) = await CosmosDownloads.RequestAsync(http, "the-token", "victorian-street", options);

        Assert.Equal(2, bodies.Count);
        Assert.Equal(("files.cosmos.example", null), (link.Host, option.Version)); // the second way, which names no version
        Assert.Equal("victorian-street.zip", CosmosDownloads.FileName("victorian-street", option, link));
        Assert.Equal("victorian-street__5.5.zip", CosmosDownloads.FileName("victorian-street", options[0], link));

        // Not owned, or not signed in: asking another way wouldn't change that.
        foreach (var account in new[] { HttpStatusCode.PaymentRequired, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            bodies.Clear();
            refusal = account;
            var error = await Assert.ThrowsAsync<CosmosException>(() => CosmosDownloads.RequestAsync(http, "the-token", "victorian-street", options));
            Assert.Equal((account, 1), (error.StatusCode, bodies.Count));
            Assert.DoesNotContain("the-token", error.Message);
        }
    }

    [Theory]
    [InlineData("""{ "url": "http://files.cosmos.example/pack.zip" }""")] // not over https
    [InlineData("""{ "url": 5 }""")]
    [InlineData("""{ "message": "soon" }""")]
    [InlineData("not JSON")]
    public async Task An_answer_without_a_usable_link_is_no_download(string answer)
    {
        var http = new HttpClient(new FakeCosmos(_ => JSON(answer)));

        await Assert.ThrowsAsync<CosmosException>(() => CosmosDownloads.RequestAsync(http, "token", "victorian-street", [new CosmosDownloadOption(Platform: "ue")]));
    }

    [Theory]
    [InlineData("..", "5.5")]
    [InlineData("victorian-street", "..\\..\\evil")]
    [InlineData("CON", null)]
    public void A_name_from_Cosmos_that_isnt_a_file_name_is_refused(string slug, string? version) =>
        Assert.Throws<CosmosException>(() => CosmosDownloads.FileName(slug, new CosmosDownloadOption(Version: version), new Uri("https://files.cosmos.example/a.zip")));

    [Fact]
    public async Task A_download_that_was_stopped_goes_on_from_where_it_was()
    {
        byte[] pack = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))];
        var ranges = new List<string?>();
        int cutAt = 2000;
        var http = new HttpClient(new FakeCosmos(request =>
        {
            Assert.Null(request.Headers.Authorization); // the link carries its own; the account's token stays with Cosmos
            ranges.Add(request.Headers.Range?.ToString());
            long from = request.Headers.Range?.Ranges.Single().From ?? 0;
            // The first time the connection closes midway, with the whole length promised.
            var body = new ByteArrayContent(pack, (int)from, (from == 0 ? cutAt : pack.Length) - (int)from);
            body.Headers.ContentLength = pack.Length - from;
            var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = body };
            if (from > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, pack.Length - 1, pack.Length);
            return response;
        }));
        var link = new Uri("https://files.cosmos.example/packs/street.zip?signature=abc");

        var first = new InstallStatus();
        await Assert.ThrowsAnyAsync<Exception>(() => CosmosDownloads.DownloadAsync(http, link, _folder, "victorian-street__5.5.zip", first));
        Assert.False(File.Exists(Path.Combine(_folder, "victorian-street__5.5.zip"))); // not passed off as the file
        Assert.Equal(cutAt, new FileInfo(Path.Combine(_folder, "victorian-street__5.5.zip.part")).Length);
        Assert.Empty(CosmosDownloads.Find(_folder)); // nor counted as downloaded

        var second = new InstallStatus();
        string path = await CosmosDownloads.DownloadAsync(http, link, _folder, "victorian-street__5.5.zip", second);

        Assert.Equal([null, $"bytes={cutAt}-"], ranges);
        Assert.Equal(pack, File.ReadAllBytes(path));
        Assert.Equal((pack.Length, pack.Length), (second.DownloadTotal, second.DownloadedBytes));
        Assert.False(File.Exists(path + ".part"));
        var found = Assert.Single(CosmosDownloads.Find(_folder));
        Assert.Equal(("victorian-street", path, 5000L), (found.Slug, found.Path, found.Size));
    }

    [Fact]
    public async Task A_server_that_cant_go_on_from_the_middle_starts_the_file_anew()
    {
        byte[] pack = [1, 2, 3, 4, 5, 6];
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "street.zip.part"), [9, 9, 9]); // of an older link, say
        var http = new HttpClient(new FakeCosmos(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pack) })); // the range is ignored

        string path = await CosmosDownloads.DownloadAsync(http, new Uri("https://files.cosmos.example/street.zip"), _folder, "street.zip", new InstallStatus());

        Assert.Equal(pack, File.ReadAllBytes(path));
    }

    /// <summary>A pack as Cosmos hands it out: a project's files, here inside one folder as some zips have them.</summary>
    private string Pack(string name, string wrapper = "", bool project = true, params (string Path, string Text)[] more)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var files = new List<(string Path, string Text)>
        {
            ("Config/DefaultEngine.ini", "[engine]"), ("Content/Street/Maps/Demo.umap", "map"), ("Content/Street/Meshes/SM_Lamp.uasset", "lamp"),
        };
        if (project)
            files.Add(("Street.uproject", "{}"));
        zip.CreateEntry(wrapper + "Content/Street/"); // a folder's own entry, as zip tools write them
        foreach (var (file, text) in files.Select(f => (wrapper + f.Path, f.Text)).Concat(more))
        {
            using var writer = new StreamWriter(zip.CreateEntry(file).Open());
            writer.Write(text);
        }
        return path;
    }

    private static string[] FilesIn(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order()];

    [Theory]
    [InlineData("")]
    [InlineData("Victorian_Street_55/")] // everything in one folder: the project is what's inside it
    public async Task Adds_only_a_packs_Content_to_a_project(string wrapper)
    {
        string zip = Pack("victorian-street__5.5.zip", wrapper);
        string project = Directory.CreateDirectory(Path.Combine(_folder, "Hillside")).FullName;
        File.WriteAllText(Path.Combine(project, "Hillside.uproject"), "mine");
        Directory.CreateDirectory(Path.Combine(project, "Config"));
        File.WriteAllText(Path.Combine(project, "Config", "DefaultEngine.ini"), "mine");
        var status = new InstallStatus();

        int added = await CosmosDownloads.AddToProjectAsync(zip, project, status, CancellationToken.None);

        Assert.Equal(2, added);
        Assert.Equal(["Config/DefaultEngine.ini", "Content/Street/Maps/Demo.umap", "Content/Street/Meshes/SM_Lamp.uasset", "Hillside.uproject"], FilesIn(project));
        Assert.Equal(("mine", "lamp"), (File.ReadAllText(Path.Combine(project, "Config", "DefaultEngine.ini")), File.ReadAllText(Path.Combine(project, "Content", "Street", "Meshes", "SM_Lamp.uasset"))));
        Assert.Equal((2, 2, 7L, 7L), (status.FilesTotal, status.FilesDone, status.WriteTotal, status.WrittenBytes));
    }

    [Fact]
    public async Task Unpacks_a_pack_as_a_project_of_its_own()
    {
        string zip = Pack("victorian-street__5.5.zip", "Victorian_Street_55/");
        string target = Path.Combine(_folder, "Projects", "VictorianStreet");

        string uproject = await CosmosDownloads.CreateProjectAsync(zip, target, new InstallStatus(), CancellationToken.None);

        Assert.Equal(Path.Combine(target, "Street.uproject"), uproject);
        Assert.Equal(["Config/DefaultEngine.ini", "Content/Street/Maps/Demo.umap", "Content/Street/Meshes/SM_Lamp.uasset", "Street.uproject"], FilesIn(target));

        // Never over a folder that has something in it; nor from a pack that isn't a project.
        var taken = await Assert.ThrowsAsync<InstallException>(() => CosmosDownloads.CreateProjectAsync(zip, target, new InstallStatus(), CancellationToken.None));
        Assert.Contains("isn't empty", taken.Message);
        var loose = await Assert.ThrowsAsync<InstallException>(() =>
            CosmosDownloads.CreateProjectAsync(Pack("loose.zip", project: false), Path.Combine(_folder, "Projects", "Loose"), new InstallStatus(), CancellationToken.None));
        Assert.Contains("no .uproject", loose.Message);
        Assert.False(Directory.Exists(Path.Combine(_folder, "Projects", "Loose")));
    }

    [Fact]
    public async Task A_zip_cant_write_outside_the_folder_its_unpacked_into()
    {
        string zip = Pack("evil.zip", more: [("Content/../../Outside.txt", "out"), ("../Beside.uproject", "{}")]);
        string project = Directory.CreateDirectory(Path.Combine(_folder, "Inner", "Hillside")).FullName;

        await Assert.ThrowsAsync<IOException>(() => CosmosDownloads.AddToProjectAsync(zip, project, new InstallStatus(), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => CosmosDownloads.CreateProjectAsync(zip, Path.Combine(_folder, "Inner", "New"), new InstallStatus(), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_folder, "Inner", "Outside.txt")));
        Assert.False(File.Exists(Path.Combine(_folder, "Inner", "Beside.uproject")));
        Assert.False(File.Exists(Path.Combine(_folder, "Outside.txt")));
    }

    [Fact]
    public async Task What_isnt_a_zip_or_has_no_Content_says_so()
    {
        Directory.CreateDirectory(_folder);
        string notZip = Path.Combine(_folder, "sound.wav");
        File.WriteAllText(notZip, "RIFF");
        string project = Directory.CreateDirectory(Path.Combine(_folder, "Hillside")).FullName;

        var unreadable = await Assert.ThrowsAsync<InstallException>(() => CosmosDownloads.AddToProjectAsync(notZip, project, new InstallStatus(), CancellationToken.None));
        Assert.Contains("isn't a zip", unreadable.Message);

        string noContent = Path.Combine(_folder, "docs.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(noContent, System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("Readme.txt");
        var empty = await Assert.ThrowsAsync<InstallException>(() => CosmosDownloads.AddToProjectAsync(noContent, project, new InstallStatus(), CancellationToken.None));
        Assert.Contains("no Content folder", empty.Message);
    }

    [Fact]
    public void A_products_file_is_found_by_the_engine_version_its_for()
    {
        CosmosFile[] files =
        [
            new("victorian-street", @"D:\Cosmos\victorian-street__5.4.zip", 1), new("victorian-street", @"D:\Cosmos\victorian-street__5.5.zip", 2),
            new("wooden-railing", @"D:\Cosmos\wooden-railing.zip", 3), new("wooden-railing", @"D:\Cosmos\wooden-railing__nanite.zip", 4),
        ];

        Assert.Equal(2, CosmosDownloads.FileFor(files, "victorian-street", "5.5")?.Size);
        Assert.Null(CosmosDownloads.FileFor(files, "victorian-street", "5.3")); // another version's isn't it
        Assert.Null(CosmosDownloads.FileFor(files, "victorian-street", null));
        Assert.Equal(3, CosmosDownloads.FileFor(files, "wooden-railing", "5.5")?.Size); // handed out without telling a version: the only one there is
        Assert.Null(CosmosDownloads.FileFor(files, "italian-village", "5.5"));
    }

    private static HttpResponseMessage JSON(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeCosmos(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
