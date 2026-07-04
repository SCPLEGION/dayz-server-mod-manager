using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Net.Http;

namespace DayZModManager;

internal static class SteamWorkshopClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Http = new();

    public static async Task<PublishedFileDetailsResponseItem?> GetPublishedFileDetailsAsync(ulong publishedFileId, string? authKey)
    {
        // ISteamRemoteStorage/GetPublishedFileDetails
        var url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

        var form = new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = publishedFileId.ToString()
        };

        if (!string.IsNullOrWhiteSpace(authKey))
            form["key"] = authKey;

        using var content = new FormUrlEncodedContent(form);

        using var resp = await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<GetPublishedFileDetailsResponse>(stream, JsonOptions);
        return payload?.Response?.PublishedFileDetails?.FirstOrDefault();
    }

    public static async Task<List<WorkshopSearchResultItem>> QueryFilesAsync(string apiKey, string searchText, uint appid, uint creatorAppid, uint numPerPage)
    {
        // Steamworks QueryFiles: search by title/description text, returning short_description.
        // See IPublishedFileService/QueryFiles/v1.
        var url =
            "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/?" +
            $"key={Uri.EscapeDataString(apiKey)}" +
            $"&query_type=12" + // k_PublishedFileQueryType_RankedByTextSearch
            $"&page=1" +
            $"&cursor=%2A" + // first page
            $"&numperpage={numPerPage}" +
            $"&creator_appid={creatorAppid}" +
            $"&appid={appid}" +
            $"&requiredtags={Uri.EscapeDataString("")}" +
            $"&excludedtags={Uri.EscapeDataString("")}" +
            $"&required_flags={Uri.EscapeDataString("")}" +
            $"&omitted_flags={Uri.EscapeDataString("")}" +
            $"&search_text={Uri.EscapeDataString(searchText)}" +
            $"&filetype=0" + // items
            $"&return_short_description=true" +
            $"&return_metadata=false" +
            $"&language=english";

        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<QueryFilesResponse>(stream, JsonOptions);

        var items = payload?.Response?.PublishedFileDetails ?? new List<WorkshopSearchResultItemResponseItem>();
        return items.Select(x => new WorkshopSearchResultItem
        {
            PublishedFileId = x.PublishedFileId,
            Title = x.Title,
            Description = x.ShortDescription
        }).ToList();
    }

    public static async Task<List<ulong>> GetCollectionChildrenAsync(ulong collectionId, string? authKey)
    {
        // ISteamRemoteStorage/GetCollectionDetails returns every item directly inside a Workshop
        // Collection (not their dependencies - callers should still run those through the normal
        // dependency-closure resolver).
        var url = "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

        var form = new Dictionary<string, string>
        {
            ["collectioncount"] = "1",
            ["publishedfileids[0]"] = collectionId.ToString()
        };

        if (!string.IsNullOrWhiteSpace(authKey))
            form["key"] = authKey;

        using var content = new FormUrlEncodedContent(form);

        using var resp = await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<GetCollectionDetailsResponse>(stream, JsonOptions);

        var details = payload?.Response?.CollectionDetails?.FirstOrDefault();
        var children = details?.Children ?? new List<PublishedFileChild>();
        return children.Select(c => c.PublishedFileId).ToList();
    }

    public static async Task<List<ulong>> GetChildrenPublishedFileIdsAsync(ulong publishedFileId, string apiKey)
    {
        // IPublishedFileService/GetDetails with includechildren=true returns dependency tree nodes in "children".
        // This is used to auto-add required dependencies when writing mods.txt.
        var url =
            "https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?" +
            $"key={Uri.EscapeDataString(apiKey)}" +
            $"&includechildren=true" +
            $"&publishedfileids[0]={publishedFileId}";

        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<GetDetailsResponse>(stream, JsonOptions);

        var details = payload?.Response?.PublishedFileDetails?.FirstOrDefault();
        var children = details?.Children ?? new List<PublishedFileChild>();
        return children.Select(c => c.PublishedFileId).ToList();
    }
}

internal sealed class GetCollectionDetailsResponse
{
    [JsonPropertyName("response")]
    public GetCollectionDetailsResponseInner? Response { get; init; }
}

internal sealed class GetCollectionDetailsResponseInner
{
    [JsonPropertyName("collectiondetails")]
    public List<CollectionDetailsResponseItem>? CollectionDetails { get; init; }
}

internal sealed class CollectionDetailsResponseItem
{
    [JsonPropertyName("children")]
    public List<PublishedFileChild>? Children { get; init; }
}

internal sealed class GetDetailsResponse
{
    [JsonPropertyName("response")]
    public GetDetailsResponseInner? Response { get; init; }
}

internal sealed class GetDetailsResponseInner
{
    [JsonPropertyName("publishedfiledetails")]
    public List<PublishedFileDetailsWithChildren>? PublishedFileDetails { get; init; }
}

internal sealed class PublishedFileDetailsWithChildren
{
    [JsonPropertyName("children")]
    public List<PublishedFileChild>? Children { get; init; }
}

internal sealed class PublishedFileChild
{
    [JsonPropertyName("publishedfileid")]
    public ulong PublishedFileId { get; init; }
}

internal sealed class QueryFilesResponse
{
    [JsonPropertyName("response")]
    public QueryFilesResponseInner? Response { get; init; }
}

internal sealed class QueryFilesResponseInner
{
    [JsonPropertyName("publishedfiledetails")]
    public List<WorkshopSearchResultItemResponseItem>? PublishedFileDetails { get; init; }
}

internal sealed class WorkshopSearchResultItemResponseItem
{
    [JsonPropertyName("publishedfileid")]
    public ulong PublishedFileId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("short_description")]
    public string? ShortDescription { get; init; }

    [JsonPropertyName("file_description")]
    public string? FileDescription { get; init; }
}

internal sealed class GetPublishedFileDetailsResponse
{
    [JsonPropertyName("response")]
    public GetPublishedFileDetailsResponseInner? Response { get; init; }
}

internal sealed class GetPublishedFileDetailsResponseInner
{
    [JsonPropertyName("publishedfiledetails")]
    public List<PublishedFileDetailsResponseItem>? PublishedFileDetails { get; init; }
}

internal sealed class PublishedFileDetailsResponseItem
{
    [JsonPropertyName("publishedfileid")]
    public string? PublishedFileId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}
