using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;
using System.Text.Json;
using YoutubeExplode.Common;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Collections.Concurrent;

using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class YouTubeExtractor : IExtractor, IInputMatcher
    {
        private readonly YouTubeSettings _yt;

        public YouTubeExtractor(YouTubeSettings yt) { _yt = yt; }

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && (input.Contains("youtu.be") || input.Contains("youtube.com"));
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;
            YouTube.ApiKey = _yt.ApiKey ?? "";

            string name;
            List<SongJob>? deleted = null;
            List<SongJob> songs = new();

            if (_yt.GetDeleted)
            {
                context.Log.Info("Getting deleted videos..");
                var archive = new YouTube.YouTubeArchiveRetriever(context.Log);
                deleted = await archive.RetrieveDeleted(input, printFailed: _yt.DeletedOnly);
            }
            if (!_yt.DeletedOnly)
            {
                if (!string.IsNullOrEmpty(YouTube.ApiKey))
                {
                    context.Log.Info("Loading playlist (API)");
                    (name, songs) = await YouTube.GetSongsApi(input, max, off, context.Log);
                }
                else
                {
                    context.Log.Info("Loading playlist");
                    (name, songs) = await YouTube.GetSongsYtExplode(input, max, off, context.Log);
                }
            }
            else
            {
                name = await YouTube.GetPlaylistTitle(input);
            }

            if (deleted != null)
                songs.InsertRange(0, deleted);

            YouTube.StopService();

            var slj = new JobList { ItemName = name, EnablesIndexByDefault = true };
            foreach (var s in songs)
                slj.Jobs.Add(s);

            if (reverse)
            {
                slj.Jobs.Reverse();
                slj.Jobs.RemoveRange(0, Math.Min(offset, slj.Jobs.Count));
                if (slj.Jobs.Count > maxTracks)
                    slj.Jobs.RemoveRange(maxTracks, slj.Jobs.Count - maxTracks);
            }

            return slj;
        }
    }


    public static partial class YouTube
    {
        private static readonly YoutubeClient? youtube = new YoutubeClient();
        private static YouTubeService? youtubeService = null;
        private static string apiKey = "";

        public static string ApiKey
        {
            get => apiKey;
            set
            {
                var newValue = value ?? "";
                if (apiKey == newValue) return;

                apiKey = newValue;
                youtubeService = null;
            }
        }

        public static async Task<(string, List<SongJob>)> GetSongsApi(string url, int max = int.MaxValue, int offset = 0, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            StartService();

            string playlistId = await UrlToId(url);

            var playlistRequest = youtubeService.Playlists.List("snippet");
            playlistRequest.Id = playlistId;
            var playlistResponse = playlistRequest.Execute();

            string playlistName = playlistResponse.Items[0].Snippet.Title;

            var playlistItemsRequest = youtubeService.PlaylistItems.List("snippet,contentDetails");
            playlistItemsRequest.PlaylistId = playlistId;
            playlistItemsRequest.MaxResults = Math.Min(max, 100);

            var songsDict = await GetDictYtExplode(url, max, offset, log);
            var songs = new List<SongJob>();
            int count = 0;

            while (playlistItemsRequest != null && count < max + offset)
            {
                var playlistItemsResponse = playlistItemsRequest.Execute();
                foreach (var playlistItem in playlistItemsResponse.Items)
                {
                    if (count >= offset)
                    {
                        if (songsDict.TryGetValue(playlistItem.Snippet.ResourceId.VideoId, out SongJob? value))
                        {
                            songs.Add(value);
                        }
                        else
                        {
                            var title = "";
                            var uploader = "";
                            var length = 0;
                            var desc = "";

                            var videoRequest = youtubeService.Videos.List("contentDetails,snippet");
                            videoRequest.Id = playlistItem.Snippet.ResourceId.VideoId;
                            var videoResponse = videoRequest.Execute();

                            title = playlistItem.Snippet.Title;
                            if (videoResponse.Items.Count == 0) continue;
                            uploader = videoResponse.Items[0].Snippet.ChannelTitle;
                            length = (int)XmlConvert.ToTimeSpan(videoResponse.Items[0].ContentDetails.Duration).TotalSeconds;
                            desc = videoResponse.Items[0].Snippet.Description;

                            var song = await ParseSongInfo(title, uploader, playlistItem.Snippet.ResourceId.VideoId, length, desc, log: log);
                            song.ItemNumber = count + 1;
                            songs.Add(song);
                        }
                    }

                    if (++count >= max + offset)
                        break;
                }

                playlistItemsRequest.PageToken = playlistItemsResponse.NextPageToken;
                if (playlistItemsRequest.PageToken == null || count >= max + offset)
                    playlistItemsRequest = null;
                else
                    playlistItemsRequest.MaxResults = Math.Min(offset + max - count, 100);
            }

            if (songsDict.Count >= 200)
                log.Info($"Loaded: {songs.Count}");
            return (playlistName, songs);
        }

        // requestInfoIfNeeded=true is way too slow
        public static async Task<SongJob> ParseSongInfo(string title, string uploader, string id, int length, string desc = "", bool requestInfoIfNeeded = false, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            (string title, string uploader, int length, string desc) info = ("", "", -1, "");

            string uri = id;
            bool artistMaybeWrong = false;
            string artist = uploader;
            string trackTitle = title;
            string other = "";

            uploader = uploader.Replace("–", "-").Trim().RemoveConsecutiveWs();
            title = title.Replace("–", "-").Replace(" -- ", " - ").Trim().RemoveConsecutiveWs();
            artist = uploader;
            trackTitle = title;

            if (artist.EndsWith(" - Topic"))
            {
                artist = artist[..^7].Trim();
                trackTitle = title;

                if (artist == "Various Artists")
                {
                    if (desc.Length == 0 && requestInfoIfNeeded && id.Length > 0)
                    {
                        info = await GetVideoInfo(id);
                        desc = info.desc;
                    }

                    if (desc.Length > 0)
                    {
                        var lines = desc.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
                        var dotLine = lines.FirstOrDefault(line => line.Contains(" · "));
                        if (dotLine != null)
                            artist = dotLine.Split([" · "], StringSplitOptions.None)[1];
                    }
                }
            }
            else
            {
                artistMaybeWrong = !title.ContainsWithBoundary(artist, true) && !desc.ContainsWithBoundary(artist, true);

                var split = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (split.Length == 2)
                {
                    artist = split[0];
                    trackTitle = split[1];
                    artistMaybeWrong = false;
                }
                else if (split.Length > 2)
                {
                    int index = Array.FindIndex(split, s => s.ContainsWithBoundary(artist, true));
                    if (index != -1 && index < split.Length - 1)
                    {
                        artist = split[index];
                        trackTitle = String.Join(" - ", split[(index + 1)..]);
                        artistMaybeWrong = false;
                    }
                }

                if (artistMaybeWrong && requestInfoIfNeeded && desc.Length == 0)
                {
                    info = await GetVideoInfo(id);
                    artistMaybeWrong = !info.desc.ContainsWithBoundary(artist, true);
                }
            }

            if (length <= 0 && id.Length > 0 && requestInfoIfNeeded)
            {
                if (info.length > 0)
                    length = info.length;
                else
                {
                    info = await GetVideoInfo(id);
                    length = info.length;
                }
            }

            var query = new SongQuery
            {
                Artist = artist,
                Title = trackTitle,
                URI = uri,
                Length = length,
                ArtistMaybeWrong = artistMaybeWrong,
            };

            return new SongJob(query) { Other = other };
        }

        // Legacy adapter: used by tests and callers that still work with Track shape.
        public static async Task<SongJob> ParseTrackInfo(string title, string uploader, string id, int length, string desc = "", bool requestInfoIfNeeded = false)
            => await ParseSongInfo(title, uploader, id, length, desc, requestInfoIfNeeded);

        public static async Task<(string title, string uploader, int length, string desc)> GetVideoInfo(string id)
        {
            (string title, string uploader, int length, string desc) o = ("", "", -1, "");
            try
            {
                var vid = await youtube.Videos.GetAsync(id);
                o.title = vid.Title;
                o.uploader = vid.Author.ChannelTitle;
                o.desc = vid.Description;
                o.length = (int)vid.Duration.Value.TotalSeconds;
            }
            catch
            {
                if (ApiKey.Length > 0)
                {
                    try
                    {
                        StartService();
                        var videoRequest = youtubeService.Videos.List("contentDetails,snippet");
                        videoRequest.Id = id;
                        var videoResponse = videoRequest.Execute();
                        o.title = videoResponse.Items[0].Snippet.Title;
                        o.uploader = videoResponse.Items[0].Snippet.ChannelTitle;
                        o.length = (int)XmlConvert.ToTimeSpan(videoResponse.Items[0].ContentDetails.Duration).TotalSeconds;
                        o.desc = videoResponse.Items[0].Snippet.Description;
                    }
                    catch { }
                }
            }
            return o;
        }

        public static void StartService()
        {
            if (youtubeService == null)
            {
                if (ApiKey.Length == 0)
                    throw new Exception("No API key");

                youtubeService = new YouTubeService(new BaseClientService.Initializer()
                {
                    ApiKey = ApiKey,
                    ApplicationName = "Sockseek"
                });
            }
        }

        public static void StopService()
        {
            youtubeService = null;
        }

        public static async Task<Dictionary<string, SongJob>> GetDictYtExplode(string url, int max = int.MaxValue, int offset = 0, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            var songs = new Dictionary<string, SongJob>();
            int count = 0;

            await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
            {
                if (count >= offset && count < offset + max)
                {
                    var title = video.Title;
                    var uploader = video.Author.ChannelTitle;
                    var ytId = video.Id.Value;
                    var length = (int)video.Duration.Value.TotalSeconds;

                    var song = await ParseSongInfo(title, uploader, ytId, length, log: log);
                    song.ItemNumber = count + 1;
                    songs[ytId] = song;
                }

                if (count++ >= offset + max) break;
            }

            return songs;
        }

        public static async Task<string> GetPlaylistTitle(string url)
        {
            var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            return playlist.Title;
        }

        public static async Task<(string, List<SongJob>)> GetSongsYtExplode(string url, int max = int.MaxValue, int offset = 0, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            var playlistTitle = playlist.Title;
            var songs = new List<SongJob>();
            int count = 0;

            await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
            {
                if (count >= offset && count < offset + max)
                {
                    var title = video.Title;
                    var uploader = video.Author.ChannelTitle;
                    var ytId = video.Id.Value;
                    var length = (int)video.Duration.Value.TotalSeconds;

                    var song = await ParseSongInfo(title, uploader, ytId, length, log: log);
                    song.ItemNumber = count + 1;
                    songs.Add(song);
                }

                if (count++ >= offset + max) break;
            }

            return (playlistTitle, songs);
        }

        // Legacy adapter for callers that expected (string, List<Track>)
        public static async Task<(string, List<SongJob>)> GetTracksYtExplode(string url, int max = int.MaxValue, int offset = 0)
            => await GetSongsYtExplode(url, max, offset);

        public static async Task<(string, List<SongJob>)> GetTracksApi(string url, int max = int.MaxValue, int offset = 0)
            => await GetSongsApi(url, max, offset);

        public static async Task<string> UrlToId(string url)
        {
            var playlist = await youtube.Playlists.GetAsync(url);
            return playlist.Id.ToString();
        }

        private const string YtdlpDownloadPathPrefix = "sockseek-download-path:";

        [GeneratedRegex(@"document\.title\s*=\s*""(.+?) - YouTube"";")]
        private static partial Regex DocumentTitleRegex();

        public static async Task<List<(int length, string id, string title)>> YtdlpSearch(SongQuery query, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            string search = query.Artist.Length > 0 ? $"{query.Artist} - {query.Title}" : query.Title;
            var result = await RunYtdlpAsync($"\"ytsearch3:{search}\" --dump-json", log);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp search failed with exit code {result.ExitCode}: {string.Join(Environment.NewLine, result.Stderr)}");

            List<(int, string, string)> results = new List<(int, string, string)>();
            foreach (var output in result.Stdout)
            {
                if (!TryParseYtdlpSearchResult(output, out var parsed))
                    continue;

                results.Add(parsed);
            }

            return results;
        }

        internal static bool TryParseYtdlpSearchResult(string json, out (int length, string id, string title) result)
        {
            result = default;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!TryGetString(root, "id", out var id) || !TryGetString(root, "title", out var title))
                    return false;

                var length = TryGetInt(root, "duration", out var duration)
                    ? duration
                    : -1;
                result = (length, id, title);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }

            static bool TryGetString(JsonElement element, string propertyName, out string value)
            {
                value = "";
                if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                    return false;

                value = property.GetString() ?? "";
                return value.Length > 0;
            }

            static bool TryGetInt(JsonElement element, string propertyName, out int value)
            {
                value = 0;
                if (!element.TryGetProperty(propertyName, out var property))
                    return false;

                if (property.ValueKind == JsonValueKind.Number)
                    return property.TryGetInt32(out value)
                        || (property.TryGetDouble(out var number) && TryRound(number, out value));

                if (property.ValueKind == JsonValueKind.String)
                    return int.TryParse(property.GetString(), out value);

                return false;

                static bool TryRound(double number, out int value)
                {
                    value = 0;
                    if (number < int.MinValue || number > int.MaxValue)
                        return false;

                    value = (int)Math.Round(number);
                    return true;
                }
            }
        }

        public static async Task<string> YtdlpDownload(string id, string savePathNoExt, string ytdlpArgument = "", IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            if (ytdlpArgument.Length == 0)
                ytdlpArgument = "\"{id}\" -f bestaudio/best -ci -o \"{savepath-noext}.%(ext)s\" -x";

            var arguments = ytdlpArgument
                .Replace("{id}", id)
                .Replace("{savepath}", savePathNoExt)
                .Replace("{savepath-noext}", savePathNoExt)
                .Replace("{savedir}", Path.GetDirectoryName(savePathNoExt))
                + $" --print \"after_move:{YtdlpDownloadPathPrefix}%(filepath)j\"";

            var result = await RunYtdlpAsync(arguments, log);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp download failed with exit code {result.ExitCode}: {string.Join(Environment.NewLine, result.Stderr)}");

            var outputPath = result.Stdout
                .Select(TryParseYtdlpDownloadPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("yt-dlp did not report an output path.");

            if (!File.Exists(outputPath))
                throw new FileNotFoundException($"yt-dlp reported output path '{outputPath}', but the file does not exist.", outputPath);

            return outputPath;
        }

        internal static string? TryParseYtdlpDownloadPath(string line)
        {
            if (!line.StartsWith(YtdlpDownloadPathPrefix, StringComparison.Ordinal))
                return null;

            var rawPath = line[YtdlpDownloadPathPrefix.Length..];
            try
            {
                using var doc = JsonDocument.Parse(rawPath);
                return doc.RootElement.ValueKind == JsonValueKind.String
                    ? doc.RootElement.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed record YtdlpProcessResult(int ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr);

        private static async Task<YtdlpProcessResult> RunYtdlpAsync(string arguments, IJobLog log)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            log.Debug($"{process.StartInfo.FileName} {process.StartInfo.Arguments}");
            process.Start();

            var stdoutTask = ReadLinesAsync(process.StandardOutput);
            var stderrTask = ReadLinesAsync(process.StandardError);
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            foreach (var line in stderr)
                log.Info(line);

            return new YtdlpProcessResult(process.ExitCode, stdout, stderr);
        }

        private static async Task<List<string>> ReadLinesAsync(TextReader reader)
        {
            var lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                lines.Add(line);
            return lines;
        }

        public class YouTubeArchiveRetriever
        {
            private readonly HttpClient _client;
            private readonly IJobLog _log;

            public YouTubeArchiveRetriever(IJobLog? log = null)
            {
                _log = log ?? ExtractorContext.None.Log;
                _client = new HttpClient();
                _client.Timeout = TimeSpan.FromSeconds(10);
            }

            public async Task<List<SongJob>> RetrieveDeleted(string url, bool printFailed = true)
            {
                var deletedVideoUrls = new BlockingCollection<string>();

                int totalCount = 0;
                int archivedCount = 0;
                var songs = new ConcurrentBag<SongJob>();
                var noArchive = new ConcurrentBag<string>();
                var failRetrieve = new ConcurrentBag<string>();

                int workerCount = 4;
                var workers = new List<Task>();

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "yt-dlp",
                        Arguments = $"--ignore-no-formats-error --no-warn --match-filter \"!uploader\" --print webpage_url {url}",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true
                };
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        deletedVideoUrls.Add(e.Data);
                        Interlocked.Increment(ref totalCount);
                    }
                };
                process.Exited += (sender, e) => { deletedVideoUrls.CompleteAdding(); };

                process.Start();
                process.BeginOutputReadLine();

                for (int i = 0; i < workerCount; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        foreach (var videoUrl in deletedVideoUrls.GetConsumingEnumerable())
                        {
                            var waybackUrls = await GetOldestArchiveUrls(videoUrl, limit: 2);
                            if (waybackUrls != null && waybackUrls.Count > 0)
                            {
                                Interlocked.Increment(ref archivedCount);

                                bool good = false;
                                foreach (var waybackUrl in waybackUrls)
                                {
                                    var (title, uploader, duration) = await GetVideoDetails(waybackUrl);
                                    if (!string.IsNullOrWhiteSpace(title))
                                    {
                                        var song = await ParseSongInfo(title, uploader, waybackUrl, duration, log: _log);
                                        song.Other = $"{{\"t\":\"{title.Trim()}\",\"u\":\"{uploader.Trim()}\"}}";
                                        songs.Add(song);
                                        good = true;
                                        break;
                                    }
                                }

                                if (!good)
                                    failRetrieve.Add(waybackUrls[0]);
                            }
                            else
                            {
                                noArchive.Add(videoUrl);
                            }

                        }
                    }));
                }

                await Task.WhenAll(workers);
                process.WaitForExit();
                deletedVideoUrls.CompleteAdding();
                _log.Info($"Deleted metadata total/archived/retrieved: {totalCount}/{archivedCount}/{songs.Count}");

                if (printFailed)
                {
                    if (archivedCount < totalCount)
                    {
                        _log.Info("No archived version found for the following:");
                        foreach (var x in noArchive) _log.Info($"  {x}");
                        _log.Info("");
                    }
                    if (songs.Count < archivedCount)
                    {
                        _log.Info("Failed to parse archived version for the following:");
                        foreach (var x in failRetrieve) _log.Info($"  {x}");
                        _log.Info("");
                    }
                }

                return songs.ToList();
            }

            private async Task<List<string>?> GetOldestArchiveUrls(string url, int limit)
            {
                var url2 = $"http://web.archive.org/cdx/search/cdx?url={url}&fl=timestamp,original&filter=statuscode:200&sort=timestamp:asc&limit={limit}";
                HttpResponseMessage? response = null;
                for (int i = 0; i < 3; i++)
                {
                    try { response = await _client.GetAsync(url2); break; }
                    catch { }
                }
                if (response == null) return null;

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var lines = content.Split("\n").Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
                    if (lines.Count > 0)
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var parts = lines[i].Split(" ");
                            var timestamp = parts[0];
                            var originalUrl = parts[1];
                            lines[i] = $"http://web.archive.org/web/{timestamp}/{originalUrl}";
                        }
                        return lines;
                    }
                }
                return null;
            }

            public static async Task<(string title, string uploader, int duration)> GetVideoDetails(string url)
            {
                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(url);

                var titlePatterns = new[]
                {
                    "//h1[@id='video_title']",
                    "//meta[@name='title']",
                };

                var usernamePatterns = new[]
                {
                    "//div[@id='userInfoDiv']/b/a",
                    "//a[contains(@class, 'contributor')]",
                    "//a[@id='watch-username']",
                    "//a[contains(@class, 'author')]",
                    "//div[@class='yt-user-info']/a",
                    "//div[@id='upload-info']//yt-formatted-string/a",
                    "//span[@itemprop='author']//link[@itemprop='name']",
                    "//a[contains(@class, 'yt-user-name')]",
                };

                string getItem(string[] patterns)
                {
                    foreach (var pattern in patterns)
                    {
                        var node = doc.DocumentNode.SelectSingleNode(pattern);
                        if (node != null)
                        {
                            var res = "";
                            if (pattern.StartsWith("//meta") || pattern.Contains("@itemprop"))
                                res = node.GetAttributeValue("content", "");
                            else
                                res = node.InnerText;
                            if (!string.IsNullOrEmpty(res))
                                return Utils.UnHtmlString(res);
                        }
                    }
                    return "";
                }

                var title = getItem(titlePatterns);

                if (string.IsNullOrEmpty(title))
                {
                    var match = DocumentTitleRegex().Match(doc.Text);
                    if (match.Success)
                        title = match.Groups[1].Value;
                }

                var username = getItem(usernamePatterns);

                int duration = -1;
                var node2 = doc.DocumentNode.SelectSingleNode("//meta[@itemprop='duration']");
                if (node2 != null)
                {
                    try { duration = (int)XmlConvert.ToTimeSpan(node2.GetAttributeValue("content", "")).TotalSeconds; }
                    catch { }
                }

                return (title, username, duration);
            }
        }
    }
