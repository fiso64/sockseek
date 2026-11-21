using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;
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
        public JobSourceInfo? DescribeSource(ExtractJob source, Job item)
            => ReferenceEquals(source, item) ? new(Detail: "YouTube import", PreviewDetail: "YouTube import") : null;

        private readonly YouTubeSettings _yt;

        public YouTubeExtractor(YouTubeSettings yt) { _yt = yt; }

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && (input.Contains("youtu.be") || input.Contains("youtube.com"));
        }

        public async Task<Job> ExtractAsync(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;
            YouTube.ApiKey = _yt.ApiKey ?? "";

            string name;
            List<(string Url, SongJob Song)>? deleted = null;
            List<Job> songs = [];

            if (_yt.GetDeleted || _yt.DeletedOnly)
            {
                context.Log.Info("Getting deleted videos..");
                using var archive = new YouTube.YouTubeArchiveRetriever(context.Log);
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
                    var playlist = await YouTube.GetSongsYtExplode(input, max, off, context.Log);
                    name = playlist.Title;
                    songs.AddRange(playlist.Songs);
                }
            }
            else
            {
                name = await YouTube.GetPlaylistTitle(input);
            }

            if (deleted is { Count: > 0 })
            {
                // The Data API includes a position for private videos. Put recovered
                // metadata there when available, including repeated playlist entries.
                var archived = deleted.ToLookup(entry => entry.Url, entry => entry.Song);
                var restored = new HashSet<string>();
                for (int i = 0; i < songs.Count; i++)
                {
                    if (songs[i] is not ExtractJob missing || archived[missing.Input].FirstOrDefault() is not { } song) continue;
                    songs[i] = new SongJob(song.Query) { ItemNumber = missing.ItemNumber, ExtractorInfo = song.ExtractorInfo };
                    restored.Add(missing.Input);
                }
                songs.InsertRange(0, deleted.Where(entry => !restored.Contains(entry.Url)).Select(entry => entry.Song));
            }

            YouTube.StopService();

            var slj = new JobList { ItemName = name, EnablesIndexByDefault = true };
            foreach (var s in songs)
                slj.Add(s);

            if (reverse)
            {
                slj.ReverseMembers();
                slj.RemoveRange(0, Math.Min(offset, slj.Jobs.Count));
                if (slj.Jobs.Count > maxTracks)
                    slj.RemoveRange(maxTracks, slj.Jobs.Count - maxTracks);
            }

            return slj;
        }
    }


    public static partial class YouTube
    {
        private static readonly YoutubeClient youtube = new YoutubeClient();
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

        public static async Task<(string, List<Job>)> GetSongsApi(string url, int max = int.MaxValue, int offset = 0, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            StartService();
            var service = RequireService();

            string playlistId = await UrlToId(url);

            var playlistRequest = service.Playlists.List("snippet");
            playlistRequest.Id = playlistId;
            var playlistResponse = await playlistRequest.ExecuteAsync();
            if (playlistResponse.Items.Count == 0)
                throw new InvalidOperationException($"Could not retrieve YouTube playlist '{playlistId}'.");

            string playlistName = playlistResponse.Items[0].Snippet.Title;

            var playlistItemsRequest = service.PlaylistItems.List("snippet,contentDetails");
            playlistItemsRequest.PlaylistId = playlistId;
            const int pageSize = 50;
            long end = (long)offset + max;
            playlistItemsRequest.MaxResults = Math.Min(end, pageSize);

            var (_, playable) = await GetSongsYtExplode(url, max, offset, log);
            var songsDict = new Dictionary<string, SongJob>();
            foreach (var song in playable) songsDict[song.Query.URI] = song;
            var songs = new List<Job>();
            int count = 0;

            while (count < end)
            {
                var page = await playlistItemsRequest.ExecuteAsync();
                foreach (var item in page.Items)
                {
                    if (count >= end) break;
                    int position = ++count;
                    if (position <= offset) continue;

                    var id = item.Snippet.ResourceId.VideoId;
                    if (songsDict.TryGetValue(id, out var cached))
                    {
                        songs.Add(new SongJob(cached.Query) { ItemNumber = position, ExtractorInfo = cached.ExtractorInfo });
                        continue;
                    }

                    var request = service.Videos.List("contentDetails,snippet");
                    request.Id = id;
                    var response = await request.ExecuteAsync();
                    if (response.Items.Count == 0)
                    {
                        songs.Add(ExtractJob.UnavailablePlaylistEntry($"https://www.youtube.com/watch?v={id}", InputType.YouTube, position));
                        continue;
                    }

                    var video = response.Items[0];
                    var length = (int)XmlConvert.ToTimeSpan(video.ContentDetails.Duration).TotalSeconds;
                    var song = await ParseSongInfo(item.Snippet.Title, video.Snippet.ChannelTitle,
                        id, length, video.Snippet.Description, log: log);
                    song.ItemNumber = position;
                    songs.Add(song);
                }

                if (page.NextPageToken == null || count >= end) break;
                playlistItemsRequest.PageToken = page.NextPageToken;
                playlistItemsRequest.MaxResults = Math.Min(end - count, pageSize);
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

            return new SongJob(query) { ExtractorInfo = other };
        }


        public static async Task<(string title, string uploader, int length, string desc)> GetVideoInfo(string id)
        {
            (string title, string uploader, int length, string desc) o = ("", "", -1, "");
            try
            {
                var vid = await youtube.Videos.GetAsync(id);
                o.title = vid.Title;
                o.uploader = vid.Author.ChannelTitle;
                o.desc = vid.Description;
                o.length = vid.Duration.HasValue ? (int)vid.Duration.Value.TotalSeconds : -1;
            }
            catch
            {
                if (ApiKey.Length > 0)
                {
                    try
                    {
                        StartService();
                        var service = RequireService();
                        var videoRequest = service.Videos.List("contentDetails,snippet");
                        videoRequest.Id = id;
                        var videoResponse = await videoRequest.ExecuteAsync();
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

        private static YouTubeService RequireService()
            => youtubeService ?? throw new InvalidOperationException("YouTube API service was not started.");

        public static void StopService()
        {
            youtubeService = null;
        }

        public static async Task<string> GetPlaylistTitle(string url)
        {
            using var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            return playlist.Title;
        }

        public static async Task<(string Title, List<SongJob> Songs)> GetSongsYtExplode(string url, int max = int.MaxValue, int offset = 0, IJobLog? log = null)
        {
            log ??= ExtractorContext.None.Log;
            using var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            var playlistTitle = playlist.Title;
            var songs = new List<SongJob>();
            int count = 0;

            await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
            {
                if (count >= offset && count < (long)offset + max)
                {
                    var title = video.Title;
                    var uploader = video.Author.ChannelTitle;
                    var ytId = video.Id.Value;
                    var length = video.Duration.HasValue ? (int)video.Duration.Value.TotalSeconds : -1;

                    var song = await ParseSongInfo(title, uploader, ytId, length, log: log);
                    song.ItemNumber = count + 1;
                    songs.Add(song);
                }

                if (++count >= (long)offset + max) break;
            }

            return (playlistTitle, songs);
        }



        public static async Task<string> UrlToId(string url)
        {
            var playlist = await youtube.Playlists.GetAsync(url);
            return playlist.Id.ToString();
        }

        [GeneratedRegex(@"document\.title\s*=\s*""(.+?) - YouTube"";")]
        private static partial Regex DocumentTitleRegex();

        public class YouTubeArchiveRetriever : IDisposable
        {
            private readonly HttpClient _client;
            private readonly IJobLog _log;

            public YouTubeArchiveRetriever(IJobLog? log = null)
            {
                _log = log ?? ExtractorContext.None.Log;
                _client = new HttpClient();
                _client.Timeout = TimeSpan.FromSeconds(10);
            }

            public async Task<List<(string Url, SongJob Song)>> RetrieveDeleted(string url, bool printFailed = true)
            {
                int totalCount = 0;
                int archivedCount = 0;
                var songs = new ConcurrentBag<(string Url, SongJob Song)>();
                var noArchive = new ConcurrentBag<string>();
                var failRetrieve = new ConcurrentBag<string>();

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--ignore-no-formats-error --no-warn --match-filter \"!uploader\" --print webpage_url {url}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                try
                {
                    // EOF, not Process.Exited, ends URL collection: final output
                    // can still be arriving after the child exits.
                    await Parallel.ForEachAsync(process.ReadAllLinesAsync(),
                        new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (line, _) =>
                    {
                        if (line.StandardError) { _log.Info(line.Content); return; }
                        if (string.IsNullOrWhiteSpace(line.Content)) return;
                        string videoUrl = line.Content;
                        Interlocked.Increment(ref totalCount);
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
                                    song.ExtractorInfo = $"{{\"t\":\"{title.Trim()}\",\"u\":\"{uploader.Trim()}\"}}";
                                    songs.Add((videoUrl, song));
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
                    });
                    await process.WaitForExitAsync();
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        await process.WaitForExitAsync();
                    }
                }
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

            public void Dispose()
            {
                _client.Dispose();
                GC.SuppressFinalize(this);
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
