using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UnVault.Core.Cosmos;

/// <summary>
/// Leartes' Cosmos store. None of this is a documented API: it's what the store's own site calls, as read from the site
/// and from Asset Manager Studio (Docs/Cosmos.md). Expect changes.
/// </summary>
public static class CosmosEndpoints
{
    public const string Site = "https://cosmos.leartesstudios.com";
    public const string API = "https://api.cosmos.leartesstudios.com";

    public static string ProductURL(string slug) => $"{Site}/product/{Uri.EscapeDataString(slug)}";
}

/// <summary>Cosmos turned a request down; <see cref="StatusCode"/> 401 or 403 means the sign-in is no longer good.</summary>
public sealed class CosmosException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public bool IsSignedOut => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>One page of GET api.cosmos.leartesstudios.com/inventory.</summary>
internal sealed class CosmosPage
{
    [JsonPropertyName("data")] public List<CosmosItem?>? Data { get; set; }
    [JsonPropertyName("meta")] public CosmosMeta? Meta { get; set; }
}

internal sealed class CosmosMeta
{
    [JsonPropertyName("last_page")] public int LastPage { get; set; }
}

/// <summary>Something the account owns on Cosmos.</summary>
public sealed class CosmosItem
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>Its name in links: cosmos.leartesstudios.com/product/{slug}.</summary>
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";

    /// <summary>What Cosmos sells it as. A "bundle" stands for other products rather than files of its own.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>Who it's from on Cosmos, e.g. "leartes".</summary>
    [JsonPropertyName("service")] public string? Service { get; set; }

    [JsonPropertyName("cover_image")] public CosmosImage? CoverImage { get; set; }
}

public sealed class CosmosImage
{
    [JsonPropertyName("url")] public string? URL { get; set; }
}

internal sealed class CosmosExchangeRequest
{
    [JsonPropertyName("code_verifier")] public string CodeVerifier { get; set; } = "";
}

internal sealed class CosmosExchangeResponse
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}

/// <summary>
/// A sign-in under way in the user's own browser, as Leartes' Unreal Engine plugin does it: the browser is sent to
/// <paramref name="URL"/>, and Cosmos is then asked now and then whether whoever holds <paramref name="Verifier"/> has
/// signed in. Cosmos was given only the verifier's hash, so the page in the browser can't be used to take the sign-in.
/// </summary>
public sealed record CosmosBrowserSignIn(string URL, string Verifier);

[JsonSerializable(typeof(CosmosPage))]
[JsonSerializable(typeof(CosmosExchangeRequest))]
[JsonSerializable(typeof(CosmosExchangeResponse))]
[JsonSerializable(typeof(Dictionary<string, CosmosProduct>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CosmosJSONContext : JsonSerializerContext;

/// <summary>Cosmos calls for a signed-in account. The token comes from Leartes' own sign-in page.</summary>
public sealed class CosmosClient(HttpClient http)
{
    /// <summary>No inventory has this many pages: a listing that says so is wrong, and isn't followed forever.</summary>
    private const int MaxPages = 2000;

    /// <summary>Everything the account owns, bundles left out (their products are listed themselves).</summary>
    /// <param name="progress">Receives the number of items read so far, after each page.</param>
    public async Task<List<CosmosItem>> GetInventoryAsync(string token, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<CosmosItem>();
        for (int page = 1, last = 1; page <= last; page++)
        {
            var read = await GetAsync($"{CosmosEndpoints.API}/inventory?page={page}", token, cancellationToken);
            items.AddRange((read.Data ?? []).OfType<CosmosItem>().Where(i => i.ID.Length > 0 && !string.Equals(i.Type, "bundle", StringComparison.OrdinalIgnoreCase)));
            progress?.Report(items.Count);
            last = Math.Min(read.Meta?.LastPage ?? page, MaxPages);
        }
        return items;
    }

    /// <summary>A product's details. Cosmos gives them to anyone, as it does the product's page, so no sign-in is sent.</summary>
    public async Task<CosmosProduct> GetProductAsync(string slug, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CosmosEndpoints.API}/products/{Uri.EscapeDataString(slug)}");
        using var response = await SendAsync(request, cancellationToken);
        var product = CosmosProduct.Parse(await ReadAsync(response, CosmosJSONContext.Default.JsonElement, cancellationToken));
        product.FetchedAt = DateTimeOffset.UtcNow;
        product.Fields = CosmosProductCache.Fields;
        return product;
    }

    /// <summary>The ways to sign in that Cosmos hands to the browser: accounts made with Google or GitHub. Cosmos has no such sign-in for one made with an email address and a password.</summary>
    public static IReadOnlyList<string> BrowserSignInProviders { get; } = ["google", "github"];

    /// <summary>Starts a sign-in to finish in the browser; send the browser to its URL, then ask <see cref="TryFinishBrowserSignInAsync"/>.</summary>
    /// <param name="provider">One of <see cref="BrowserSignInProviders"/>.</param>
    public static CosmosBrowserSignIn StartBrowserSignIn(string provider)
    {
        if (!BrowserSignInProviders.Contains(provider))
            throw new ArgumentException($"Cosmos has no browser sign-in with {provider}.", nameof(provider));
        string verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new($"{CosmosEndpoints.API}/auth/{provider}?unreal=true&code_challenge={challenge}", verifier);
    }

    /// <summary>Asks Cosmos whether the sign-in started in the browser is done.</summary>
    /// <returns>The account's token; null while the user hasn't signed in there yet.</returns>
    public async Task<string?> TryFinishBrowserSignInAsync(CosmosBrowserSignIn signIn, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CosmosEndpoints.API}/auth/unreal/exchange")
        {
            Content = JsonContent.Create(new CosmosExchangeRequest { CodeVerifier = signIn.Verifier }, CosmosJSONContext.Default.CosmosExchangeRequest),
        };
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return null; // Cosmos's "not yet"
        var answer = await ReadAsync(response, CosmosJSONContext.Default.CosmosExchangeResponse, cancellationToken);
        return answer.Token is { Length: > 0 } token ? token : throw new CosmosException("Cosmos finished the sign-in without a token.");
    }

    private async Task<CosmosPage> GetAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadAsync(response, CosmosJSONContext.Default.CosmosPage, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Under its own name: the client's default is the Epic Games Launcher's, which is for Epic's services.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UnVaultLauncher", ProductInfo.Version));
        var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw new CosmosException($"{(int)response.StatusCode} {response.ReasonPhrase} from Cosmos", response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
                ?? throw new CosmosException("Cosmos sent an empty answer.");
        }
        catch (JsonException ex)
        {
            // Leartes changing an answer's shape should read as a Cosmos problem, not crash whoever asked.
            throw new CosmosException($"Unexpected answer from Cosmos: {ex.Message}");
        }
    }
}
