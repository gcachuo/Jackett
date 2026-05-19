using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common.Indexers.Definitions.Abstract;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Newtonsoft.Json.Linq;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

namespace Jackett.Common.Indexers.Definitions
{
    public class PelisPanda : PublicSpanishIndexerBase
    {
        public override string Id => "pelispanda";
        public override string Name => "PelisPanda";
        public override string SiteLink { get; protected set; } = "https://pelispanda.org/";

        public PelisPanda(IIndexerConfigurationService configService, WebClient wc, Logger l,
                          IProtectionService ps, ICacheService cs)
            : base(configService, wc, l, ps, cs)
        {
            var tmdbApiKey = new ConfigurationData.StringConfigurationItem("TMDB API Key (optional, for better English title matching)")
            {
                Value = ""
            };
            configData.AddDynamic("TmdbApiKey", tmdbApiKey);
        }

        private PelisPandaParser _parser;

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            _parser = new PelisPandaParser(webclient, logger, SiteLink);
            return new PelisPandaRequestGenerator(SiteLink, configData, webclient, logger, _parser);
        }

        public override IParseIndexerResponse GetParser() => _parser;
    }

    public class PelisPandaRequestGenerator : IIndexerRequestGenerator
    {
        private const int PostsPerPage = 500;
        private const int Page = 1;

        private readonly string _siteLink;
        private readonly ConfigurationData _configData;
        private readonly WebClient _webclient;
        private readonly Logger _logger;
        private readonly PelisPandaParser _parser;

        public PelisPandaRequestGenerator(string siteLink, ConfigurationData configData, WebClient webclient, Logger logger, PelisPandaParser parser)
        {
            _siteLink = siteLink;
            _configData = configData;
            _webclient = webclient;
            _logger = logger;
            _parser = parser;
        }

        public IndexerPageableRequestChain GetSearchRequests(TorznabQuery query)
        {
            var chain = new IndexerPageableRequestChain();

            // Store query in parser for filtering
            _parser.SetQuery(query);

            var term = query.SearchTerm ?? string.Empty;
            if (string.IsNullOrWhiteSpace(term))
                return chain;

            // Extract year from search term
            int? searchYear = null;
            var yearMatch = Regex.Match(term, @"\b(19\d{2}|20\d{2})\b");
            if (yearMatch.Success)
            {
                searchYear = int.Parse(yearMatch.Value);
                term = term.Replace(yearMatch.Value, "").Trim();
            }

            // Remove episode patterns from search term (e.g., "Show Name S01E01" -> "Show Name")
            // but keep the episode info in query object for filtering
            if (!string.IsNullOrWhiteSpace(term))
            {
                // Remove patterns like S01E01, s01e01
                term = Regex.Replace(term, @"\s+[Ss]\d{1,2}[Ee]\d{1,2}$", "").Trim();
                // Remove patterns like 1x01, 1X01
                term = Regex.Replace(term, @"\s+\d{1,2}[xX]\d{1,2}$", "").Trim();
            }

            // Note: We do NOT add season/episode to the search term
            // The API search is broader, and we filter results in the parser

            // Try original term first
            var url = $"{_siteLink}wp-json/wpreact/v1/search" +
                      $"?query={WebUtility.UrlEncode(term)}" +
                      $"&posts_per_page={PostsPerPage}" +
                      $"&page={Page}";
            chain.Add(new[] { new IndexerRequest(url) });

            // Try TMDB translation as fallback
            var tmdbTranslated = GetSpanishTitleFromTmdbAsync(term, searchYear).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(tmdbTranslated) && !tmdbTranslated.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                var yearInfo = searchYear.HasValue ? $" ({searchYear.Value})" : "";
                _logger?.Info($"TMDB translated '{term}'{yearInfo} to '{tmdbTranslated}'");
                var tmdbUrl = $"{_siteLink}wp-json/wpreact/v1/search" +
                              $"?query={WebUtility.UrlEncode(tmdbTranslated)}" +
                              $"&posts_per_page={PostsPerPage}" +
                              $"&page={Page}";
                chain.Add(new[] { new IndexerRequest(tmdbUrl) });
            }

            return chain;
        }

        private async Task<string> GetSpanishTitleFromTmdbAsync(string englishTitle, int? year)
        {
            var tmdbApiKey = ((ConfigurationData.StringConfigurationItem)_configData.GetDynamic("TmdbApiKey"))?.Value;
            if (string.IsNullOrWhiteSpace(tmdbApiKey))
                return null;

            try
            {
                // Try movie search first (more common for PelisPanda)
                var movieUrl = $"https://api.themoviedb.org/3/search/movie?api_key={tmdbApiKey}&query={Uri.EscapeDataString(englishTitle)}&language=es-MX";
                if (year.HasValue)
                    movieUrl += $"&year={year.Value}";

                _logger?.Debug($"TMDB movie search: query='{englishTitle}', year={year?.ToString() ?? "null"}");

                var movieRequest = new WebRequest(movieUrl);
                var movieResponse = await _webclient.GetResultAsync(movieRequest).ConfigureAwait(false);

                if (movieResponse.Status == HttpStatusCode.OK)
                {
                    var movieJson = JsonSerializer.Deserialize<TmdbSearchResponse>(movieResponse.ContentString);
                    if (movieJson?.Results != null && movieJson.Results.Count > 0)
                    {
                        // If year is specified, prefer exact year match
                        if (year.HasValue)
                        {
                            var yearMatch = movieJson.Results.FirstOrDefault(r =>
                            {
                                var releaseYear = r.ReleaseDate?.Substring(0, 4);
                                return releaseYear != null && int.TryParse(releaseYear, out var ry) && ry == year.Value;
                            });
                            if (yearMatch != null)
                                return yearMatch.Title ?? yearMatch.Name;
                        }
                        // Return first movie result
                        return movieJson.Results[0].Title ?? movieJson.Results[0].Name;
                    }
                }

                // Try TV search if no movie results
                var tvUrl = $"https://api.themoviedb.org/3/search/tv?api_key={tmdbApiKey}&query={Uri.EscapeDataString(englishTitle)}&language=es-MX";
                if (year.HasValue)
                    tvUrl += $"&first_air_date_year={year.Value}";

                _logger?.Debug($"TMDB TV search: query='{englishTitle}', year={year?.ToString() ?? "null"}");

                var tvRequest = new WebRequest(tvUrl);
                var tvResponse = await _webclient.GetResultAsync(tvRequest).ConfigureAwait(false);

                if (tvResponse.Status == HttpStatusCode.OK)
                {
                    var tvJson = JsonSerializer.Deserialize<TmdbSearchResponse>(tvResponse.ContentString);
                    if (tvJson?.Results != null && tvJson.Results.Count > 0)
                    {
                        // If year is specified, ONLY return results that match the year
                        if (year.HasValue)
                        {
                            var yearMatch = tvJson.Results.FirstOrDefault(r =>
                            {
                                var firstAirYear = r.FirstAirDate?.Substring(0, 4);
                                return firstAirYear != null && int.TryParse(firstAirYear, out var fay) && fay == year.Value;
                            });
                            // Only return if we found a year match, otherwise return null
                            return yearMatch != null ? (yearMatch.Title ?? yearMatch.Name) : null;
                        }
                        // If no year specified, return first TV result
                        return tvJson.Results[0].Title ?? tvJson.Results[0].Name;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Error fetching TMDB data: {ex.Message}");
                return null;
            }
        }
    }

    public class PelisPandaParser : IParseIndexerResponse
    {
        private const int MaxConcurrentRequests = 2;

        private const long EstimateBytes720p = 1L * 1024 * 1024 * 1024;
        private const long EstimateBytes1080p = (long)(2.5 * 1024 * 1024 * 1024);
        private const long EstimateBytes2160p = 5L * 1024 * 1024 * 1024;
        private const long EstimateBytesDefault = 512L * 1024 * 1024;

        private readonly WebClient _webclient;
        private readonly Logger _logger;
        private readonly string _siteLink;
        private TorznabQuery _currentQuery;

        public PelisPandaParser(WebClient webclient, Logger logger, string siteLink)
        {
            _webclient = webclient;
            _logger = logger;
            _siteLink = siteLink;
        }

        public void SetQuery(TorznabQuery query)
        {
            _currentQuery = query;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            if (indexerResponse == null || string.IsNullOrWhiteSpace(indexerResponse.Content))
            {
                _logger?.Warn("PelisPanda: search response was empty or missing; returning no releases");
                return new List<ReleaseInfo>();
            }

            JObject json;
            try
            {
                json = JObject.Parse(indexerResponse.Content);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "PelisPanda: failed to parse search response as JSON; returning no releases");
                return new List<ReleaseInfo>();
            }
            var results = json["results"] as JArray ?? new JArray();

            var items = new List<(int Index, JObject Item, string DetailUrl, string Type)>();
            foreach (var raw in results.OfType<JObject>())
            {
                var type = (string)raw["type"];
                var slug = (string)raw["slug"];
                var detailUrl = BuildDetailUrl(type, slug);
                if (detailUrl == null)
                {
                    _logger?.Warn($"PelisPanda: skipping unknown type '{type}' for slug '{slug}'");
                    continue;
                }
                items.Add((items.Count, raw, detailUrl, type));
            }

            var details = FetchDetailsAsync(items.Select(i => i.DetailUrl).Distinct().ToList())
                .GetAwaiter().GetResult();

            var seenGuids = new HashSet<string>();
            var releases = new List<ReleaseInfo>();
            foreach (var entry in items)
            {
                if (!details.TryGetValue(entry.DetailUrl, out var detail) || detail == null)
                    continue;
                BuildReleasesForItem(entry.Item, entry.Type, detail, seenGuids, releases, _currentQuery);
            }
            return releases;
        }

        private string BuildDetailUrl(string type, string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return null;
            return type switch
            {
                "pelicula" => $"{_siteLink}wp-json/wpreact/v1/movie/{slug}",
                "anime" => $"{_siteLink}wp-json/wpreact/v1/anime/{slug}",
                "serie" => $"{_siteLink}wp-json/wpreact/v1/serie/{slug}/related",
                _ => null
            };
        }

        private async Task<Dictionary<string, JObject>> FetchDetailsAsync(IList<string> urls)
        {
            var result = new Dictionary<string, JObject>();
            if (urls.Count == 0)
                return result;

            using var semaphore = new SemaphoreSlim(MaxConcurrentRequests);
            var tasks = urls.Select(async url =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var resp = await _webclient.GetResultAsync(new WebRequest(url)).ConfigureAwait(false);
                    if (resp.Status != HttpStatusCode.OK)
                    {
                        _logger?.Warn($"PelisPanda: detail {url} returned HTTP {(int)resp.Status}; skipping");
                        return (url, (JObject)null);
                    }
                    try
                    {
                        return (url, JObject.Parse(resp.ContentString ?? string.Empty));
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"PelisPanda: detail {url} JSON parse failed: {ex.Message}; skipping");
                        return (url, (JObject)null);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"PelisPanda: detail {url} fetch failed: {ex.Message}; skipping");
                    return (url, (JObject)null);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            foreach (var (url, doc) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                result[url] = doc;
            }
            return result;
        }

        private void BuildReleasesForItem(JObject item, string type,
            JObject detail, HashSet<string> seenGuids, List<ReleaseInfo> releases, TorznabQuery query)
        {
            var downloads = detail["downloads"] as JArray;
            if (downloads == null || downloads.Count == 0)
                return;

            var slug = (string)item["slug"] ?? string.Empty;
            var titleBase = FirstNonEmpty(
                (string)item["original_title"],
                (string)item["title"],
                slug);

            int? year = null;
            if (item["year"]?.Type == JTokenType.Integer)
            {
                year = (int?)item["year"];
            }
            else if (item["year"]?.Type == JTokenType.String &&
                     int.TryParse((string)item["year"], out var parsedYear))
            {
                year = parsedYear;
            }

            var category = MapCategory(type);
            var detailsUri = new Uri($"{_siteLink}{type}/{slug}");

            foreach (var dl in downloads.OfType<JObject>())
            {
                var rawLink = (string)dl["download_link"];
                if (string.IsNullOrWhiteSpace(rawLink))
                    continue;

                var quality = (string)dl["quality"];
                var language = (string)dl["language"];
                var sizeStr = (string)dl["size"];
                var subsFlag = (int?)dl["subs"] ?? 0;
                var dateStr = (string)dl["date"];
                var season = (int?)dl["season"];
                var episode = (int?)dl["episode"];

                // Filter by episode if specified in query
                if (query != null && (query.Season > 0 || !string.IsNullOrWhiteSpace(query.Episode)))
                {
                    // If query has season/episode requirements, check if this download matches
                    var seasonMatch = query.Season == 0 || (season.HasValue && query.Season == season.Value);
                    var episodeMatch = string.IsNullOrWhiteSpace(query.Episode) ||
                                      (episode.HasValue && query.Episode == episode.Value.ToString());

                    if (!seasonMatch || !episodeMatch)
                        continue; // Skip this download if it doesn't match the requested episode
                }


                Uri magnetUri = null;
                Uri linkUri = null;
                if (rawLink.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(rawLink, UriKind.Absolute, out magnetUri))
                    {
                        _logger?.Warn($"PelisPanda: malformed magnet link in '{slug}'; skipping row");
                        continue;
                    }
                }
                else if (!Uri.TryCreate(rawLink, UriKind.Absolute, out linkUri))
                {
                    _logger?.Warn($"PelisPanda: malformed download link in '{slug}'; skipping row");
                    continue;
                }
                var guidUri = magnetUri ?? linkUri;
                if (!seenGuids.Add(guidUri.AbsoluteUri))
                    continue;

                var release = new ReleaseInfo
                {
                    Title = FormatTitle(titleBase, year, quality, language, season, episode),
                    Category = new List<int> { category },
                    Size = ResolveSize(sizeStr, quality),
                    Languages = TranslateLanguages(language),
                    Subs = subsFlag == 1 ? new[] { "Subtitulado" } : null,
                    MagnetUri = magnetUri,
                    Link = linkUri,
                    Guid = guidUri,
                    Details = detailsUri,
                    Year = year ?? DateTime.Now.Year,
                    PublishDate = ResolvePublishDate(dateStr, year),
                    Seeders = 1,
                    DownloadVolumeFactor = 0,
                    UploadVolumeFactor = 1
                };
                releases.Add(release);
            }
        }

        internal static string[] TranslateLanguages(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return null;

            var languages = language.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(lang => lang.Trim())
                .Select(lang => lang switch
                {
                    "Inglés" => "English",
                    "Castellano" => "Spanish",
                    "Latino" => "Latino",
                    "Español" => "Spanish",
                    _ => lang
                })
                .Where(lang => !string.IsNullOrWhiteSpace(lang))
                .ToArray();

            return languages.Length > 0 ? languages : null;
        }

        internal static int MapCategory(string type) => type switch
        {
            "pelicula" => TorznabCatType.Movies.ID,
            "anime" => TorznabCatType.TVAnime.ID,
            "serie" => TorznabCatType.TV.ID,
            _ => 0
        };

        internal static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        internal static string FormatTitle(string titleBase, int? year, string quality, string language,
            int? season, int? episode)
        {
            var parts = new List<string> { titleBase };
            if (year.HasValue && year.Value > 0)
            {
                parts.Add($"({year.Value})");
            }

            if (season.HasValue && episode.HasValue)
            {
                parts.Add($"S{season.Value:00}E{episode.Value:00}");
            }

            var formattedQuality = !string.IsNullOrWhiteSpace(quality) ? quality : "";

            var formattedLanguage = !string.IsNullOrWhiteSpace(language)
                ? Regex.Replace(
                    language.Replace("/", "."),
                    @"\bInglés\b|\bCastellano\b",
                    m => m.Value switch
                    {
                        "Inglés" => "English",
                        "Castellano" => "Spanish",
                        _ => m.Value
                    })
                : "";

            if (!string.IsNullOrWhiteSpace(formattedQuality))
            {
                parts.Add(formattedQuality);
            }

            if (!string.IsNullOrWhiteSpace(formattedLanguage))
            {
                parts.Add(formattedLanguage);
            }

            return $"{string.Join(".", parts.Where(p => !string.IsNullOrWhiteSpace(p)))}-PelisPanda";
        }

        internal static long ResolveSize(string sizeStr, string quality)
        {
            if (!string.IsNullOrWhiteSpace(sizeStr))
                return ParseUtil.GetBytes(sizeStr);
            return EstimateSizeFromQuality(quality);
        }

        internal static long EstimateSizeFromQuality(string quality) => quality switch
        {
            "720p" => EstimateBytes720p,
            "1080p" => EstimateBytes1080p,
            "2160p" => EstimateBytes2160p,
            _ => EstimateBytesDefault
        };

        internal static DateTime ResolvePublishDate(string dateStr, int? year)
        {
            if (!string.IsNullOrWhiteSpace(dateStr) &&
                DateTime.TryParseExact(dateStr, "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;

            if (year is int y && y >= 1 && y <= 9999)
                return new DateTime(y, 1, 1);

            return DateTime.Today;
        }
    }

    public class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbResult> Results { get; set; }
    }

    public class TmdbResult
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string FirstAirDate { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }
    }
}
