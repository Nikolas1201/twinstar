using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Twinstar;

/// <summary>
/// Finds and downloads cover art, Playnite-style: the store a game came from supplies its own
/// official cover when it can (Epic's launcher cache), then name searches across keyless public
/// catalogs (Steam store + community search, GOG catalog), plus SteamGridDB if the user adds a key.
/// </summary>
static class Artwork
{
    /// <summary>A candidate cover. Url is set when the source gives a direct image link.</summary>
    public sealed record Result(string Name, string Id, string Source, string? Url = null);

    internal static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Twinstar/1.0");
        return c;
    }

    public static string ArtDir => Path.Combine(Settings.Dir, "art");

    /// <param name="forGame">When given, that game's own store cover (if any) is offered first.</param>
    public static async Task<List<Result>> SearchAsync(string term, Settings settings, Game? forGame = null)
    {
        var results = new List<Result>();
        if (forGame?.StoreArtUrl != null && Normalize(term) == Normalize(forGame.Name))
            results.Add(new Result(forGame.Name, "", forGame.Source.ToString(), forGame.StoreArtUrl));
        if (string.IsNullOrWhiteSpace(term)) return results;

        if (!string.IsNullOrWhiteSpace(settings.SteamGridDbKey))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://www.steamgriddb.com/api/v2/search/autocomplete/" + Uri.EscapeDataString(term));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SteamGridDbKey.Trim());
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray().Take(10))
                        results.Add(new Result(item.GetProperty("name").GetString() ?? "", item.GetProperty("id").ToString(), "SteamGridDB"));
                }
                else Log.Write($"SteamGridDB search: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { Log.Write("SteamGridDB search failed: " + ex.Message); }
        }

        // Try the name as typed, then simplified spellings ("3 Complete" -> "III"), until Steam has enough.
        var seen = new HashSet<string>();
        foreach (var variant in QueryVariants(term))
        {
            foreach (var r in await SteamStoreSearch(variant)) if (seen.Add(r.Id)) results.Add(r);
            foreach (var r in await SteamCommunitySearch(variant)) if (seen.Add(r.Id)) results.Add(r);
            if (seen.Count >= 5) break;
        }
        // A GOG install should get GOG's own cover first, like Playnite's per-store metadata.
        var gog = await GogSearch(term);
        if (forGame?.Store == "GOG") results.InsertRange(results.Count > 0 && results[0].Url == forGame.StoreArtUrl && forGame.StoreArtUrl != null ? 1 : 0, gog);
        else results.AddRange(gog);
        return results;
    }

    /// <summary>GOG's public catalog: portrait covers, good for older and DRM-free games.</summary>
    static async Task<List<Result>> GogSearch(string term)
    {
        var list = new List<Result>();
        try
        {
            var json = await Http.GetStringAsync(
                "https://catalog.gog.com/v1/catalog?limit=15&query=like:" + Uri.EscapeDataString(Trademarks.Replace(term, "")));
            using var doc = JsonDocument.Parse(json);
            // GOG's "like" search pads with unrelated games; keep titles containing every word searched.
            var words = Regex.Split(term, @"\s+").Select(Normalize).Where(w => w.Length >= 2).ToList();
            foreach (var p in doc.RootElement.GetProperty("products").EnumerateArray())
            {
                var title = p.GetProperty("title").GetString() ?? "";
                var cover = p.TryGetProperty("coverVertical", out var c) ? c.GetString() : null;
                var nt = Normalize(title);
                if (cover == null || !words.All(w => nt.Contains(w))) continue;
                list.Add(new Result(title, p.GetProperty("id").GetString() ?? "", "GOG", cover));
            }
        }
        catch (Exception ex) { Log.Write("GOG search failed: " + ex.Message); }
        return list;
    }

    static async Task<List<Result>> SteamStoreSearch(string term)
    {
        var list = new List<Result>();
        try
        {
            var json = await Http.GetStringAsync(
                "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(term));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var item in items.EnumerateArray().Take(15))
                    list.Add(new Result(item.GetProperty("name").GetString() ?? "", item.GetProperty("id").ToString(), "Steam"));
        }
        catch (Exception ex) { Log.Write("Steam search failed: " + ex.Message); }
        return list;
    }

    /// <summary>Steam Community's app search also knows some games the store hides.</summary>
    static async Task<List<Result>> SteamCommunitySearch(string term)
    {
        var list = new List<Result>();
        try
        {
            var json = await Http.GetStringAsync("https://steamcommunity.com/actions/SearchApps/" + Uri.EscapeDataString(term));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in doc.RootElement.EnumerateArray().Take(10))
                    list.Add(new Result(item.GetProperty("name").GetString() ?? "", item.GetProperty("appid").ToString(), "Steam"));
        }
        catch (Exception ex) { Log.Write("Steam community search failed: " + ex.Message); }
        return list;
    }

    static readonly Regex Trademarks = new(@"[®™©]", RegexOptions.Compiled);
    static readonly Regex EditionWords = new(
        @"\b(complete|goty|game of the year|(hd|deluxe|definitive|enhanced|ultimate|gold|standard|special|anniversary|complete) edition|edition|remastered|launcher)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
    };

    static IEnumerable<string> QueryVariants(string term)
    {
        var clean = Regex.Replace(Trademarks.Replace(term, ""), @"\s+", " ").Trim();
        var simple = Regex.Replace(EditionWords.Replace(clean, ""), @"[\s:\-–]+$|\s+", " ").Trim(' ', '-', ':');
        var roman = Regex.Replace(simple, @"\b([2-9])\b", m => Roman.First(kv => kv.Value == m.Value).Key.ToUpperInvariant());
        return new[] { clean, simple, roman }.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Downloads the best cover for a result (portrait first, landscape header as fallback).</summary>
    public static async Task<byte[]?> FetchCoverAsync(Result r, Settings settings)
    {
        var urls = new List<string>();
        if (r.Url != null) urls.Add(r.Url);
        else if (r.Source == "SteamGridDB")
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://www.steamgriddb.com/api/v2/grids/game/{r.Id}?dimensions=600x900,342x482,660x930");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SteamGridDbKey.Trim());
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    urls.AddRange(doc.RootElement.GetProperty("data").EnumerateArray().Take(3)
                        .Select(g => g.GetProperty("url").GetString()!).Where(u => u != null));
                }
            }
            catch (Exception ex) { Log.Write("SteamGridDB grids failed: " + ex.Message); }
        }
        else
        {
            urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{r.Id}/library_600x900_2x.jpg");
            urls.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{r.Id}/library_600x900_2x.jpg");
            urls.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{r.Id}/library_600x900.jpg");
            urls.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{r.Id}/header.jpg");
        }

        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 1000) return bytes;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Saves cover bytes for a game and returns the file path.</summary>
    public static string Save(string gameName, byte[] bytes)
    {
        Directory.CreateDirectory(ArtDir);
        var safe = string.Concat(gameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // Unique name so the old cached bitmap/file is never confused with the new one.
        var path = Path.Combine(ArtDir, $"{safe}-{DateTime.Now:yyyyMMddHHmmss}.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Comparable form of a title: no symbols, editions or roman numerals ("III" == "3").</summary>
    public static string Normalize(string s)
    {
        s = EditionWords.Replace(Trademarks.Replace(s.ToLowerInvariant(), ""), " ").Replace("&", " and ");
        s = Regex.Replace(s, @"\b(ii|iii|iv|vi|vii|viii|ix)\b", m => Roman[m.Value]);
        return Regex.Replace(s, @"[^a-z0-9]", "");
    }

    /// <summary>
    /// Picks a result only when its normalized title equals the game's (used for automatic filling).
    /// Partial matches are left to the user: "PvZ GW2" must not get the first PvZ's cover.
    /// </summary>
    public static Result? BestMatch(string gameName, List<Result> results)
    {
        var n = Normalize(gameName);
        return n.Length == 0 ? null : results.FirstOrDefault(r => Normalize(r.Name) == n);
    }
}
