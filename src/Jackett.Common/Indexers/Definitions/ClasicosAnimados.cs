using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AngleSharp.Html.Parser;
using Jackett.Common.Extensions;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Definitions
{
    [ExcludeFromCodeCoverage]
    public class ClasicosAnimados : IndexerBase
    {
        public override string Id => "clasicosanimados";
        public override string Name => "ClasicosAnimados";
        public override string Description => "Clásicos Animados is a Public site for animated series/movies in Latin Spanish hosted on Mega/MediaFire via classicpaste/gourlpaste.";
        public override string SiteLink { get; protected set; } = "https://clasicosanimados.net/";
        public override string Language => "es-419";
        public override string Type => "public";

        private const int MaxSearchPageLimit = 3;
        private const int MaxPostsPerSearch = 4;

        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                TvSearchParams = new List<TvSearchParam> { TvSearchParam.Q, TvSearchParam.Season },
                MovieSearchParams = new List<MovieSearchParam> { MovieSearchParam.Q },
                SupportsRawSearch = true,
            };
            caps.Categories.AddCategoryMapping(1, TorznabCatType.TVAnime);
            caps.Categories.AddCategoryMapping(2, TorznabCatType.MoviesHD);
            return caps;
        }

        public ClasicosAnimados(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps,
                               ICacheService cs)
            : base(configService: configService,
                   client: wc,
                   logger: l,
                   p: ps,
                   cacheService: cs,
                   configData: new ConfigurationData())
        {
            var tmdbApiKey = new ConfigurationData.StringConfigurationItem("TMDB API Key (optional, for better English title matching)")
            {
                Value = ""
            };
            configData.AddDynamic("TmdbApiKey", tmdbApiKey);
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());
            await ConfigureIfOK(string.Empty, releases.Any(), () => throw new("Could not find release from this URL."));
            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            var searchTerm = query.GetQueryString()?.Trim();
            var isSearch = !string.IsNullOrWhiteSpace(searchTerm);

            var searchTerms = new List<string>();
            if (isSearch)
            {
                var cleaned = Regex.Replace(searchTerm, @"\s+[Ss]\d{1,2}([Ee]\d{1,2})?$", "").Trim();
                cleaned = Regex.Replace(cleaned, @"\b(19\d{2}|20\d{2})\b", "").Trim();
                searchTerms.Add(cleaned);
                var words = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 2)
                {
                    searchTerms.Add(string.Join(" ", words.Take(2)));
                }

                var tmdbTitle = await GetSpanishTitleFromTmdb(cleaned);
                if (!string.IsNullOrWhiteSpace(tmdbTitle))
                {
                    searchTerms.Insert(0, tmdbTitle);
                }
            }
            else
            {
                searchTerms.Add("");
            }

            var parser = new HtmlParser();
            var seenPosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in searchTerms)
            {
                for (var page = 1; page <= MaxSearchPageLimit; page++)
                {
                    var url = isSearch
                        ? $"{SiteLink}page/{page}/?s={WebUtilityHelpers.UrlEncode(term, Encoding.UTF8)}"
                        : $"{SiteLink}page/{page}/";
                    if (page == 1)
                    {
                        url = isSearch ? $"{SiteLink}?s={WebUtilityHelpers.UrlEncode(term, Encoding.UTF8)}" : SiteLink;
                    }

                    var response = await RequestWithCookiesAndRetryAsync(url, referer: SiteLink);
                    using var dom = await parser.ParseDocumentAsync(response.ContentString);

                    // Restrict selector strictly to main to avoid capturing sidebar popular items
                    var posts = dom.QuerySelectorAll("main .PdP_card")
                                  .Select(card => new
                                  {
                                      Link = card.QuerySelector("a[href^='https://clasicosanimados.net/']")?.GetAttribute("href"),
                                      Title = card.QuerySelector(".card_titulo")?.TextContent?.Trim() ??
                                              card.QuerySelector("img")?.GetAttribute("title")?.Trim(),
                                      Poster = card.QuerySelector("img")?.GetAttribute("src")
                                  })
                                  .Where(p => !string.IsNullOrWhiteSpace(p.Link) && !string.IsNullOrWhiteSpace(p.Title))
                                  .Where(p => !p.Link.Contains("/tag/") && !p.Link.Contains("/category/") && !p.Link.Contains("/page/"))
                                  .ToList();

                    if (posts.Count == 0)
                    {
                        break;
                    }

                    var postsToFetch = posts.Take(MaxPostsPerSearch).ToList();
                    using var semaphore = new SemaphoreSlim(3);
                    var postLinks = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool IsMovie, List<(string Url, string Hoster)> Links)>();

                    await Task.WhenAll(postsToFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            postLinks[p.Link] = await GetPostLinksAsync(p.Link, parser);
                        }
                        catch (Exception ex)
                        {
                            logger.Debug($"{Name}: failed to fetch post {p.Link}: {ex.Message}");
                            postLinks[p.Link] = (false, new List<(string, string)>());
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));

                    foreach (var p in postsToFetch)
                    {
                        if (!seenPosts.Add(p.Link))
                        {
                            continue;
                        }

                        if (isSearch && !searchTerms.Any(t => TitleMatches(t, p.Title)))
                        {
                            continue;
                        }

                        var (isMovie, links) = postLinks.TryGetValue(p.Link, out var fetched)
                            ? fetched
                            : (false, new List<(string, string)>());

                        var category = isMovie ? TorznabCatType.MoviesHD.ID : TorznabCatType.TVAnime.ID;

                        foreach (var (targetUrl, hoster) in links)
                        {
                            var uri = new Uri(targetUrl);
                            releases.Add(new ReleaseInfo
                            {
                                Guid = uri,
                                Details = new Uri(p.Link),
                                MagnetUri = uri,
                                Title = $"{p.Title.Replace(' ', '.')}.1080p.Latino.{hoster}-ClasicosAnimados",
                                Category = new List<int> { category },
                                Poster = !string.IsNullOrWhiteSpace(p.Poster) ? new Uri(p.Poster) : null,
                                Size = 1.Gigabytes(),
                                Seeders = 1,
                                Peers = 1,
                                DownloadVolumeFactor = 0,
                                UploadVolumeFactor = 1,
                                PublishDate = DateTime.Today
                            });
                        }
                    }
                }

                if (releases.Any())
                {
                    break;
                }
            }

            return releases;
        }

        private async Task<(bool IsMovie, List<(string Url, string Hoster)> Links)> GetPostLinksAsync(string postUrl, HtmlParser parser)
        {
            var result = new List<(string, string)>();
            var response = await RequestWithCookiesAndRetryAsync(postUrl, referer: SiteLink);
            using var dom = await parser.ParseDocumentAsync(response.ContentString);

            var section = dom.QuerySelector("meta[property='article:section']")?.GetAttribute("content") ?? "";
            var isMovie = section.IndexOf("película", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          section.IndexOf("pelicula", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          postUrl.Contains("peli");

            var pasteUrl = dom.QuerySelector("a[href*='classicpaste.org/p/']")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(pasteUrl))
            {
                return (isMovie, result);
            }

            var pasteResponse = await RequestWithCookiesAndRetryAsync(pasteUrl, referer: postUrl);
            using var pasteDom = await parser.ParseDocumentAsync(pasteResponse.ContentString);

            foreach (var a in pasteDom.QuerySelectorAll("a[href*='gourlpaste.com/o.php']"))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                try
                {
                    var uri = new Uri(href);
                    var qs = HttpUtility.ParseQueryString(uri.Query);
                    var l = qs["l"];
                    if (string.IsNullOrWhiteSpace(l))
                    {
                        continue;
                    }

                    var decoded = DecodeGourlpaste(l);
                    if (!string.IsNullOrWhiteSpace(decoded) && decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        var hoster = "LINK";
                        if (decoded.Contains("mediafire.com"))
                        {
                            hoster = "MEDIAFIRE";
                        }
                        else if (decoded.Contains("mega.nz") || decoded.Contains("mega.co.nz"))
                        {
                            hoster = "MEGA";
                        }
                        else if (decoded.Contains("drive.google.com"))
                        {
                            hoster = "GDRIVE";
                        }

                        result.Add((decoded, hoster));
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug($"{Name}: error parsing gourl link {href}: {ex.Message}");
                }
            }

            return (isMovie, result);
        }

        private static string DecodeGourlpaste(string encoded)
        {
            var current = encoded;
            for (var i = 0; i < 8; i++)
            {
                if (current.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    current.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                try
                {
                    var remainder = current.Length % 4;
                    if (remainder > 0)
                    {
                        current += new string('=', 4 - remainder);
                    }

                    var bytes = Convert.FromBase64String(current);
                    current = Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    break;
                }
            }

            return current.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? current : null;
        }

        private static bool TitleMatches(string query, string title)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            var titleWords = new HashSet<string>(
                Regex.Matches(title.ToLowerInvariant(), @"\b[\w']+\b").Cast<Match>()
                     .Select(m => m.Value)
                     .Where(w => w.Length > 1));
            var queryWords = Regex.Matches(query.ToLowerInvariant(), @"\b[\w']+\b").Cast<Match>()
                                  .Select(m => m.Value)
                                  .Where(w => w.Length > 3 || char.IsDigit(w[0]))
                                  .ToList();
            if (queryWords.Count == 0)
            {
                return true;
            }

            var matched = queryWords.Count(titleWords.Contains);
            return matched >= Math.Max(1, (int)Math.Ceiling(queryWords.Count * 0.6));
        }

        private async Task<string> GetSpanishTitleFromTmdb(string englishTitle)
        {
            var tmdbApiKey = ((ConfigurationData.StringConfigurationItem)configData.GetDynamic("TmdbApiKey")).Value;
            if (string.IsNullOrWhiteSpace(tmdbApiKey) || string.IsNullOrWhiteSpace(englishTitle))
            {
                return null;
            }

            try
            {
                var searchUrl = $"https://api.themoviedb.org/3/search/multi?api_key={tmdbApiKey}&query={Uri.EscapeDataString(englishTitle)}&language=es-MX";
                var response = await RequestWithCookiesAsync(searchUrl);
                if (response.Status != System.Net.HttpStatusCode.OK)
                {
                    return null;
                }

                var json = JObject.Parse(response.ContentString);
                var results = json["results"] as JArray;
                if (results == null || results.Count == 0)
                {
                    return null;
                }

                var first = results[0];
                var title = first["title"]?.ToString() ?? first["name"]?.ToString();
                var original = first["original_title"]?.ToString() ?? first["original_name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Equals(original, StringComparison.OrdinalIgnoreCase))
                {
                    return title;
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"{Name}: TMDB lookup failed: {ex.Message}");
            }

            return null;
        }
    }
}
