using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    public class MundoToon : IndexerBase
    {
        public override string Id => "mundotoon";
        public override string Name => "MundoToon";
        public override string Description => "MundoToon is a Public site for animated series/movies in Latin Spanish hosted on file hosters (Mega, MediaFire, Google Drive) via todopaste link gates.";
        public override string SiteLink { get; protected set; } = "https://mundotoon.com/";
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

        public MundoToon(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps,
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
                // strip SxxExx / year noise from the term
                var cleaned = Regex.Replace(searchTerm, @"\s+[Ss]\d{1,2}([Ee]\d{1,2})?$", "").Trim();
                cleaned = Regex.Replace(cleaned, @"\b(19\d{2}|20\d{2})\b", "").Trim();
                searchTerms.Add(cleaned);
                var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

                    var posts = dom.QuerySelectorAll("article.article-loop, article")
                                  .Select(a => new
                                  {
                                      Link = a.QuerySelector("a[href^='https://mundotoon.com/']")?.GetAttribute("href"),
                                      Title = a.QuerySelector(".card_titulo")?.TextContent?.Trim() ??
                                              a.QuerySelector("img")?.GetAttribute("alt")?.Trim(),
                                      Type = a.QuerySelector(".card_tipo")?.TextContent?.Trim() ?? "",
                                      Poster = a.QuerySelector("img")?.GetAttribute("src")
                                  })
                                  .Where(p => !string.IsNullOrWhiteSpace(p.Link) && !string.IsNullOrWhiteSpace(p.Title))
                                  .Where(p => !p.Link.Contains("/tag/") && !p.Link.Contains("/category") && !p.Link.Contains("/page/"))
                                  .ToList();

                    if (posts.Count == 0)
                    {
                        break;
                    }

                    var postsToFetch = posts.Take(MaxPostsPerSearch).ToList();
                    using var semaphore = new SemaphoreSlim(3);
                    var gateLinksByPost = new System.Collections.Concurrent.ConcurrentDictionary<string, List<(string GateUrl, string Hoster)>>();
                    await Task.WhenAll(postsToFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            gateLinksByPost[p.Link] = await GetGateLinksAsync(p.Link, parser);
                        }
                        catch (Exception ex)
                        {
                            logger.Debug($"{Name}: failed to fetch post {p.Link}: {ex.Message}");
                            gateLinksByPost[p.Link] = new List<(string, string)>();
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

                        if (isSearch && !searchTerms.Any(term => TitleMatches(term, p.Title)))
                        {
                            // The site's search leaks unrelated posts even as the only
                            // hit, so ranking can't be trusted; the title must match the
                            // query or one of its variants (TMDB es-MX title, first
                            // words) on its own.
                            continue;
                        }

                        var isMovie = p.Type.IndexOf("película", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      p.Type.IndexOf("pelicula", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      p.Link.Contains("pelicula");
                        var category = isMovie ? TorznabCatType.MoviesHD.ID : TorznabCatType.TVAnime.ID;
                        var gates = gateLinksByPost.TryGetValue(p.Link, out var g) ? g : new List<(string, string)>();

                        foreach (var (gateUrl, hoster) in gates)
                        {
                            var gate = new Uri(gateUrl);
                            releases.Add(new ReleaseInfo
                            {
                                Guid = gate,
                                Details = new Uri(p.Link),
                                MagnetUri = gate,
                                Title = $"{p.Title.Replace(' ', '.')}.1080p.Latino.{hoster}-MundoToon",
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

        /// <summary>
        /// Fetch a post page, follow the "ver enlaces" button to the todopaste
        /// paste and extract the /go/ gate links with their hoster labels.
        /// </summary>
        private async Task<List<(string GateUrl, string Hoster)>> GetGateLinksAsync(string postUrl, HtmlParser parser)
        {
            var result = new List<(string, string)>();
            var response = await RequestWithCookiesAndRetryAsync(postUrl, referer: SiteLink);
            using var dom = await parser.ParseDocumentAsync(response.ContentString);

            var pasteUrl = dom.QuerySelector("a.imgb_pub[href*='todopaste.com']")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(pasteUrl))
            {
                return result;
            }

            var pasteResponse = await RequestWithCookiesAndRetryAsync(pasteUrl, referer: postUrl);
            using var pasteDom = await parser.ParseDocumentAsync(pasteResponse.ContentString);

            foreach (var a in pasteDom.QuerySelectorAll("a[href*='/go/']"))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var icon = a.QuerySelector("img")?.GetAttribute("src") ?? "";
                var hoster = icon.Split('/').LastOrDefault()?.Replace(".webp", "").Replace(".png", "").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(hoster))
                {
                    hoster = "LINK";
                }

                result.Add((href, hoster));
            }

            return result;
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
                // Skip unlocalized results (title == original) — they'd just repeat the English query
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
