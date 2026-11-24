using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnVault.Core.Install;
using UnVault.Core.Util;

namespace UnVault.Core.Cosmos;

/// <summary>
/// One way of asking Cosmos for a product's files: the body its own site sends to products/download/{slug}. A product
/// comes in only some of them, and nothing lists which, so they're tried in the site's order until Cosmos takes one.
/// </summary>
public sealed record CosmosDownloadOption(string? Name = null, string? Type = null, string? Platform = null, string? Version = null,
    string? Quality = null, long? Resolution = null, string? Format = null)
{
    /// <summary>What sets this one apart, for the file's name and for telling the user: "5.5", "nanite", "4096", "wav"; none when there's one way only.</summary>
    public string? Variant => Version ?? Quality ?? Resolution?.ToString() ?? Format;

    internal string ToJSON()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            foreach (var (name, value) in new[] { ("name", Name), ("type", Type), ("platform", Platform), ("version", Version), ("quality", Quality) })
            {
                if (value is not null)
                    json.WriteString(name, value);
            }
            if (Resolution is { } resolution)
                json.WriteNumber("resolution", resolution);
            if (Format is not null)
                json.WriteString("format", Format);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>A product's file in the Cosmos folder: which product, and where.</summary>
public sealed record CosmosFile(string Slug, string Path, long Size);

/// <summary>Getting a product's files from Cosmos, for Unreal Engine, as its site does for a signed-in account.</summary>
public static class CosmosDownloads
{
    /// <summary>Between a product's name in links and what sets its file apart, in the file's name; neither has it.</summary>
    private const string Separator = "__";

    /// <summary>
    /// The ways to ask for a product's files for Unreal Engine, best first, as Cosmos's site makes them: an environment by
    /// engine version, a single asset by level of detail, a material by texture size, a sound by format, anything else as it is.
    /// </summary>
    /// <param name="engineVersion">The engine an environment is wanted for, as "5.5"; null for its newest.</param>
    public static IReadOnlyList<CosmosDownloadOption> OptionsFor(CosmosItem item, CosmosProduct? product, string? engineVersion = null)
    {
        string name = product?.Folder ?? item.Slug;
        var plain = new CosmosDownloadOption(name, item.Type, "ue");
        switch (item.Type)
        {
            case "environment":
                var versions = (product?.EngineVersions ?? []).OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version()).ToList();
                string? version = engineVersion is not null && versions.Contains(engineVersion) ? engineVersion : versions.FirstOrDefault();
                return version is null ? [plain] : [new CosmosDownloadOption(Platform: "ue", Version: version), plain];
            case "asset":
                // Nanite where it has it, then from the most detail down; the site's names for them.
                var lods = product?.Lods ?? [];
                string[] qualities = ["nanite", "high", "medium", "low"];
                string[] theirLods = ["nanite", "lod0", "lod1", "lod2"];
                var offered = qualities.Where((_, i) => lods.Count == 0 || lods.Contains(theirLods[i], StringComparer.OrdinalIgnoreCase)).ToList();
                return [.. (offered.Count > 0 ? offered : [.. qualities]).Select(quality => plain with { Quality = quality })];
            case "material":
                var sizes = (product?.TextureResolutions ?? []).Select(r => long.TryParse(r, out long size) ? size : 0).Where(size => size > 0).OrderDescending().ToList();
                return sizes.Count == 0 ? [plain] : [.. sizes.Select(size => plain with { Resolution = size })];
            case "sound":
                var formats = (product?.AudioFormats ?? []).OrderByDescending(f => f.Equals("wav", StringComparison.OrdinalIgnoreCase)).ToList();
                return [.. (formats.Count > 0 ? formats : ["wav", "mp3"]).Select(format => new CosmosDownloadOption(name, item.Type, Format: format))];
            default:
                return [plain];
        }
    }

    /// <summary>
    /// Asks Cosmos for a link to a product's files, by the first of <paramref name="options"/> it takes. A way it doesn't
    /// have is turned down and the next tried; a refusal that's about the account (not signed in, not owned, too many
    /// requests) ends it there.
    /// </summary>
    /// <returns>The link, good for a while, and the option it's for.</returns>
    public static async Task<(Uri Link, CosmosDownloadOption Option)> RequestAsync(
        HttpClient http, string token, string slug, IReadOnlyList<CosmosDownloadOption> options, CancellationToken cancellationToken = default)
    {
        CosmosException? last = null;
        foreach (var option in options)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{CosmosEndpoints.API}/products/download/{Uri.EscapeDataString(slug)}")
            {
                Content = new StringContent(option.ToJSON(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UnVaultLauncher", ProductInfo.Version));
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                last = new CosmosException(Refusal(response.StatusCode), response.StatusCode);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw last;
                continue;
            }

            string? link;
            try
            {
                using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                link = answer.RootElement.ValueKind == JsonValueKind.Object && answer.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                    ? url.GetString() : null;
            }
            catch (JsonException)
            {
                link = null;
            }
            // Over https only, as everything else is fetched: nobody on the way can swap the files.
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return (uri, option);
            last = new CosmosException("Cosmos answered without a link to the files.");
        }
        throw last ?? new CosmosException("Cosmos offers no download of this for Unreal Engine.");

        static string Refusal(HttpStatusCode status) => status switch
        {
            HttpStatusCode.PaymentRequired => "Cosmos says this isn't yours to download: it needs buying, or a subscription.",
            HttpStatusCode.TooManyRequests => "Cosmos says that's too many downloads for now. Try again later.",
            _ => $"{(int)status} {status} from Cosmos",
        };
    }

    /// <summary>The name a product's file gets in the Cosmos folder, so it's found again: {slug}__{variant}.{extension of the link}.</summary>
    public static string FileName(string slug, CosmosDownloadOption option, Uri link)
    {
        string extension = Path.GetExtension(link.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length is 0 or > 5 || !extension.All(char.IsAsciiLetterOrDigit))
            extension = "zip";
        string name = option.Variant is { } variant ? $"{slug}{Separator}{variant}.{extension}" : $"{slug}.{extension}";
        // Both parts come from Cosmos: nothing of them may name another folder, or a device.
        return FileNames.IsPlain(name) && !name.Contains("..") ? name : throw new CosmosException($"Cosmos named this product's file in a way that can't be a file name ({slug}).");
    }

    /// <summary>
    /// Downloads a product's file into the Cosmos folder. Written beside its place as {name}.part first; one left by a
    /// download that was stopped is gone on with where the server allows, else started anew.
    /// </summary>
    /// <returns>The file's path.</returns>
    public static async Task<string> DownloadAsync(HttpClient http, Uri link, string folder, string fileName, InstallStatus status, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName), part = path + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UnVaultLauncher", ProductInfo.Version));
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        // No sign-in is sent: the link carries its own, and goes to a file server.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // What's there is the whole file already, or no part of this one: started anew.
            File.Delete(part);
            return await DownloadAsync(http, link, folder, fileName, status, cancellationToken);
        }
        if (!response.IsSuccessStatusCode)
            throw new CosmosException($"{(int)response.StatusCode} {response.ReasonPhrase} from Cosmos's file server", response.StatusCode);

        bool resumed = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
        if (!resumed)
            have = 0;
        status.DownloadTotal = have + (response.Content.Headers.ContentLength ?? 0);
        status.AddDownloaded(have);

        await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            byte[] buffer = new byte[1 << 20];
            for (int read; (read = await body.ReadAsync(buffer, cancellationToken)) > 0;)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                status.AddDownloaded(read);
            }
        }

        // Cut short without an error (the connection closed): not the file, and kept to go on with.
        if (response.Content.Headers.ContentLength is { } length && new FileInfo(part).Length != (resumed ? have : 0) + length)
            throw new IOException("The download ended before the whole file arrived. Resume to go on with it.");
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A product's file for an engine version, among those in the Cosmos folder: the one downloaded for that version, else
    /// the one Cosmos handed out without telling a version. Null when neither is there.
    /// </summary>
    public static CosmosFile? FileFor(IEnumerable<CosmosFile> files, string slug, string? engineVersion)
    {
        var ofProduct = files.Where(f => string.Equals(f.Slug, slug, StringComparison.OrdinalIgnoreCase)).ToList();
        return ofProduct.FirstOrDefault(f => engineVersion is not null && Named(f, $"{slug}{Separator}{engineVersion}"))
            ?? ofProduct.FirstOrDefault(f => Named(f, slug));

        static bool Named(CosmosFile file, string name) => string.Equals(Path.GetFileNameWithoutExtension(file.Path), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies the Content of a product's zip into a project, as Add to Project does a Fab asset pack's: only content, as
    /// the pack's Config or .uproject would overwrite the project's own. Returns the number of files.
    /// </summary>
    public static async Task<int> AddToProjectAsync(string zipPath, string projectDirectory, InstallStatus status, CancellationToken cancellationToken)
    {
        using var zip = OpenZip(zipPath);
        var files = Files(zip).Where(f => f.Path.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
            throw new InstallException($"{Path.GetFileName(zipPath)} has no Content folder to add to a project.");
        await ExtractAsync(files, projectDirectory, status, cancellationToken);
        return files.Count;
    }

    /// <summary>Unpacks a product's zip into a new folder as a project of its own, as Create Project does a Fab one. Returns the .uproject path.</summary>
    public static async Task<string> CreateProjectAsync(string zipPath, string targetDirectory, InstallStatus status, CancellationToken cancellationToken)
    {
        if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            throw new InstallException($"{targetDirectory} already exists and isn't empty.");

        using var zip = OpenZip(zipPath);
        var files = Files(zip);
        var projectFile = files.FirstOrDefault(f => !f.Path.Contains('/') && f.Path.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)).Path
            ?? throw new InstallException($"{Path.GetFileName(zipPath)} isn't a complete project (no .uproject).");
        await ExtractAsync(files, targetDirectory, status, cancellationToken);
        return Path.Combine(targetDirectory, projectFile);
    }

    private static ZipArchive OpenZip(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InstallException($"{Path.GetFileName(zipPath)} isn't a zip that can be read. Download it again.");
        }
    }

    /// <summary>
    /// A zip's files by their place in the project: paths with forward slashes, from the folder that holds Content (the
    /// zip itself, or the one folder everything in it is wrapped in).
    /// </summary>
    private static List<(string Path, ZipArchiveEntry Entry)> Files(ZipArchive zip)
    {
        var entries = zip.Entries.Where(e => e.Name.Length > 0).Select(e => (Path: e.FullName.Replace('\\', '/').TrimStart('/'), Entry: e)).ToList();
        string[] tops = [.. entries.Select(e => e.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)];
        string wrapper = tops is [var only] && !only.Equals("Content", StringComparison.OrdinalIgnoreCase) && entries.All(e => e.Path.Contains('/')) ? only + "/" : "";
        return [.. entries.Select(e => (e.Path[wrapper.Length..], e.Entry))];
    }

    /// <summary>
    /// Unpacks files into a folder, reporting progress in bytes and files. Each is written beside its place first and then
    /// moved over it, so a cancel or a full disk never leaves a cut-off file there; none may land outside the folder.
    /// </summary>
    private static async Task ExtractAsync(IReadOnlyCollection<(string Path, ZipArchiveEntry Entry)> files, string targetRoot, InstallStatus status, CancellationToken cancellationToken)
    {
        status.WriteTotal = files.Sum(f => f.Entry.Length);
        status.FilesTotal = files.Count;
        string root = Path.GetFullPath(targetRoot);
        byte[] buffer = new byte[1 << 20];

        foreach (var (path, entry) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = ContainedPath.Resolve(root, path, "target folder");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(target, FileAttributes.Normal);

            string temporary = AtomicFile.TemporaryFor(target);
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        status.AddWritten(read);
                    }
                }
                File.Move(temporary, target, overwrite: true);
            }
            catch (InvalidDataException)
            {
                AtomicFile.TryDelete(temporary);
                throw new InstallException($"The downloaded zip is damaged at {path}. Download it again.");
            }
            catch
            {
                AtomicFile.TryDelete(temporary);
                throw;
            }
            status.FileDone();
        }
    }

    /// <summary>The products' files in the Cosmos folder, by the names <see cref="FileName"/> gave them; none when the folder isn't there.</summary>
    public static IReadOnlyList<CosmosFile> Find(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles().Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Select(f => new CosmosFile(Path.GetFileNameWithoutExtension(f.Name).Split(Separator)[0], f.FullName, f.Length))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // not there yet, or not to be read: nothing counts as downloaded
        }
    }
}
