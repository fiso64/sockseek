using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;
using YoutubeExplode.Common;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Collections.Concurrent;

using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public class YouTubeExtractor : IExtractor, IInputMatcher
    {
        private readonly YouTubeSettings _yt;

        public YouTubeExtractor(YouTubeSettings yt) { _yt = yt; }

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && (input.Contains("youtu.be") || input.Contains("youtube.com"));
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
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
                Logger.Info("Getting deleted videos..");
                var archive = new YouTube.YouTubeArchiveRetriever();
                deleted = await archive.RetrieveDeleted(input, printFailed: _yt.DeletedOnly);
            }
            if (!_yt.DeletedOnly)
            {
                if (!string.IsNullOrEmpty(YouTube.ApiKey))
                {
                    Logger.Info("Loading YouTube playlist (API)");
                    (name, songs) = await YouTube.GetSongsApi(input, max, off);
                }
                else
                {
                    Logger.Info("Loading YouTube playlist");
                    (name, songs) = await YouTube.GetSongsYtExplode(input, max, off);
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

        public static async Task<(string, List<SongJob>)> GetSongsApi(string url, int max = int.MaxValue, int offset = 0)
        {
            StartService();

            string playlistId = await UrlToId(url);

            var playlistRequest = youtubeService.Playlists.List("snippet");
            playlistRequest.Id = playlistId;
            var playlistResponse = playlistRequest.Execute();

            string playlistName = playlistResponse.Items[0].Snippet.Title;

            var playlistItemsRequest = youtubeService.PlaylistItems.List("snippet,contentDetails");
            playlistItemsRequest.PlaylistId = playlistId;
            playlistItemsRequest.MaxResults = Math.Min(max, 100);

            var songsDict = await GetDictYtExplode(url, max, offset);
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

                            var song = await ParseSongInfo(title, uploader, playlistItem.Snippet.ResourceId.VideoId, length, desc);
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
                Logger.Info($"Loaded: {songs.Count}");
            return (playlistName, songs);
        }

        // requestInfoIfNeeded=true is way too slow
        public static async Task<SongJob> ParseSongInfo(string title, string uploader, string id, int length, string desc = "", bool requestInfoIfNeeded = false)
        {
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
                    ApplicationName = "slsk-batchdl"
                });
            }
        }

        public static void StopService()
        {
            youtubeService = null;
        }

        public static async Task<Dictionary<string, SongJob>> GetDictYtExplode(string url, int max = int.MaxValue, int offset = 0)
        {
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

                    var song = await ParseSongInfo(title, uploader, ytId, length);
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

        public static async Task<(string, List<SongJob>)> GetSongsYtExplode(string url, int max = int.MaxValue, int offset = 0)
        {
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

                    var song = await ParseSongInfo(title, uploader, ytId, length);
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

        [GeneratedRegex(@"^(\d+) === ([\w-]+) === (.+)$")]
        private static partial Regex YtdlpOutputRegex();

        [GeneratedRegex(@"document\.title\s*=\s*""(.+?) - YouTube"";")]
        private static partial Regex DocumentTitleRegex();

        public static async Task<List<(int length, string id, string title)>> YtdlpSearch(SongQuery query)
        {
            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "yt-dlp";
            string search = query.Artist.Length > 0 ? $"{query.Artist} - {query.Title}" : query.Title;
            startInfo.Arguments = $"\"ytsearch3:{search}\" --print \"%(duration>%s)s === %(id)s === %(title)s\"";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            process.StartInfo = startInfo;
            process.OutputDataReceived += (sender, e) => { Logger.Info(e.Data ?? ""); };
            process.ErrorDataReceived += (sender, e) => { Logger.Info(e.Data ?? ""); };

            Logger.Debug($"{startInfo.FileName} {startInfo.Arguments}");
            process.Start();

            List<(int, string, string)> results = new List<(int, string, string)>();
            string output;
            while ((output = process.StandardOutput.ReadLine()) != null)
            {
                Match match = YtdlpOutputRegex().Match(output);
                if (match.Success)
                {
                    int seconds = int.Parse(match.Groups[1].Value);
                    string id = match.Groups[2].Value;
                    string title = match.Groups[3].Value;
                    results.Add((seconds, id, title));
                }
            }

            process.WaitForExit();
            return results;
        }

        public static async Task<string> YtdlpDownload(string id, string savePathNoExt, string ytdlpArgument = "")
        {
            var process = new Process();
            var startInfo = new ProcessStartInfo();

            bool isCustomPath = ytdlpArgument.Length > 0 && !ytdlpArgument.Contains("{savepath-noext}.%(ext)s") && !ytdlpArgument.Contains("{savepath}.%(ext)s");

            if (ytdlpArgument.Length == 0)
                ytdlpArgument = "\"{id}\" -f bestaudio/best -ci -o \"{savepath-noext}.%(ext)s\" -x";

            startInfo.FileName = "yt-dlp";
            startInfo.Arguments = ytdlpArgument
                .Replace("{id}", id)
                .Replace("{savepath}", savePathNoExt)
                .Replace("{savepath-noext}", savePathNoExt)
                .Replace("{savedir}", Path.GetDirectoryName(savePathNoExt));

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            process.StartInfo = startInfo;

            Logger.Debug($"{startInfo.FileName} {startInfo.Arguments}");
            process.Start();
            process.WaitForExit();

            if (File.Exists(savePathNoExt + ".opus"))
                return savePathNoExt + ".opus";

            string parentDirectory = Path.GetDirectoryName(savePathNoExt);
            string fileName = Path.GetFileName(savePathNoExt);

            var musicFiles = Enumerable.Empty<string>();
            try
            {
                musicFiles = Directory.GetFiles(parentDirectory, fileName + ".*", SearchOption.TopDirectoryOnly)
                    .Where(file => Utils.IsMusicFile(file) || Utils.IsVideoFile(file))
                    .OrderByDescending(file => Utils.IsMusicFile(file))
                    .ThenBy(file => Utils.IsVideoFile(file));
            }
            catch (DirectoryNotFoundException) { }

            if (!musicFiles.Any())
            {
                if (isCustomPath)
                    Logger.Debug($"Could not find yt-dlp output file. This is expected if using a custom output path argument.");
                else
                    throw new FileNotFoundException($"Could not find yt-dlp output file after download in {parentDirectory}/{fileName}.*");
            }

            return musicFiles.FirstOrDefault() ?? "";
        }

        public class YouTubeArchiveRetriever
        {
            private readonly HttpClient _client;

            public YouTubeArchiveRetriever()
            {
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
                                        var song = await ParseSongInfo(title, uploader, waybackUrl, duration);
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
                Logger.Info($"Deleted metadata total/archived/retrieved: {totalCount}/{archivedCount}/{songs.Count}");

                if (printFailed)
                {
                    if (archivedCount < totalCount)
                    {
                        Logger.Info("No archived version found for the following:");
                        foreach (var x in noArchive) Logger.Info($"  {x}");
                        Logger.Info("");
                    }
                    if (songs.Count < archivedCount)
                    {
                        Logger.Info("Failed to parse archived version for the following:");
                        foreach (var x in failRetrieve) Logger.Info($"  {x}");
                        Logger.Info("");
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
