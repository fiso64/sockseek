using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Swan;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public partial class MusicBrainzExtractor : IExtractor, IInputMatcher
    {
        [GeneratedRegex(@"musicbrainz\.org/([a-z\-]+)/([0-9a-f\-]{36})")]
        private static partial Regex MusicBrainzUrlRegex();

        public static bool InputMatches(string input)
        {
            return input.IsInternetUrl() && input.ToLower().Contains("musicbrainz.org");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            var musicBrainzClient = new MusicBrainzClient();

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            var match = MusicBrainzUrlRegex().Match(input);
            if (!match.Success)
                throw new ArgumentException($"Could not parse MusicBrainz URL: {input}");

            var entityType = match.Groups[1].Value;
            var mbid = match.Groups[2].Value;

            switch (entityType)
            {
                case "release":
                    {
                        var queue = await musicBrainzClient.GetReleaseAsAlbum(mbid, max, off, extraction);
                        return queue.Jobs.Count == 1 ? queue.Jobs[0] : queue;
                    }
                case "release-group":
                    {
                        var queue = await musicBrainzClient.GetReleaseGroupAsAlbum(mbid, max, off, extraction);
                        return queue.Jobs.Count == 1 ? queue.Jobs[0] : queue;
                    }
                case "collection":
                    {
                        var queue = await musicBrainzClient.GetCollectionReleases(mbid, max, off);
                        if (reverse)
                        {
                            queue.Jobs.Reverse();
                            queue.Jobs.RemoveRange(0, Math.Min(offset, queue.Jobs.Count));
                            if (queue.Jobs.Count > maxTracks)
                                queue.Jobs.RemoveRange(maxTracks, queue.Jobs.Count - maxTracks);
                        }
                        return queue;
                    }
                case "artist":
                    throw new Exception("MusicBrainz artist download currently not supported.");
                default:
                    throw new ArgumentException($"Unsupported MusicBrainz entity type: {entityType}");
            }
        }
    }

    public class MusicBrainzClient
    {
        private readonly HttpClient _httpClient;

        public MusicBrainzClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("slsk-batchdl/1.0 ( https://github.com/fiso64/slsk-batchdl )");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<JobList> GetReleaseAsAlbum(string mbid, int max, int offset, ExtractionSettings extraction, bool fromReleaseGroup = false)
        {
            var queue = new JobList();
            if (offset > 0 || max == 0)
                return queue;

            SldlLog.Info("Loading MusicBrainz release...");
            var url = $"https://musicbrainz.org/ws/2/release/{mbid}?inc=artist-credits+media&fmt=json";
            var response = await _httpClient.GetStringAsync(url);
            var release = JsonDocument.Parse(response).RootElement;

            var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
            var albumTitle = release.GetProperty("title").GetString();

            int totalTracks = 0;
            if (release.TryGetProperty("media", out var media))
            {
                foreach (var medium in media.EnumerateArray())
                {
                    if (medium.TryGetProperty("track-count", out var trackCount))
                        totalTracks += trackCount.GetInt32();
                }
            }

            var query = new AlbumQuery
            {
                Artist = artistCredit,
                Album = albumTitle,
            };

            queue.Jobs.Add(new AlbumJob(query)
            {
                ExtractorFolderCond = new FolderConditionPatch
                {
                    MinTrackCount = totalTracks,
                    MaxTrackCount = (!fromReleaseGroup || extraction.SetAlbumMaxTrackCount) ? totalTracks : null,
                }
            });
            return queue;
        }

        public async Task<JobList> GetReleaseGroupAsAlbum(string mbid, int max, int offset, ExtractionSettings extraction)
        {
            SldlLog.Info("Loading MusicBrainz release group...");
            var url = $"https://musicbrainz.org/ws/2/release-group/{mbid}?inc=releases&fmt=json";
            var response = await _httpClient.GetStringAsync(url);
            var releaseGroup = JsonDocument.Parse(response).RootElement;

            var releases = releaseGroup.GetProperty("releases").EnumerateArray().ToList();
            if (releases.Count == 0)
            {
                SldlLog.Info("Release group contains no releases.");
                return new JobList();
            }

            var bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("status", out var s) && s.GetString() == "Official");
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
                bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("primary-type", out var pt) && pt.GetString() == "Album");
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
                bestRelease = releases.First();

            var releaseMbid = bestRelease.GetProperty("id").GetString();
            SldlLog.Info($"Found release '{bestRelease.GetProperty("title").GetString()}' ({releaseMbid}) in release group. Getting album info...");
            return await GetReleaseAsAlbum(releaseMbid, max, offset, extraction, true);
        }

        public async Task<JobList> GetCollectionReleases(string mbid, int max, int offset)
        {
            var collectionInfoUrl = $"https://musicbrainz.org/ws/2/collection/{mbid}?fmt=json";
            var collectionInfoResponse = await _httpClient.GetStringAsync(collectionInfoUrl);
            var collectionInfo = JsonDocument.Parse(collectionInfoResponse).RootElement;
            var collectionName = collectionInfo.GetProperty("name").GetString();
            SldlLog.Info($"Loading releases from MusicBrainz collection '{collectionName}'...");

            var queue = new JobList();
            int limit = Math.Min(max, 100);
            int currentOffset = offset;
            int count = 0;

            while (count < max)
            {
                var url = $"https://musicbrainz.org/ws/2/collection/{mbid}/releases?limit={limit}&offset={currentOffset}&fmt=json";
                var response = await _httpClient.GetStringAsync(url);
                var collectionData = JsonDocument.Parse(response).RootElement;

                var releases = collectionData.GetProperty("releases").EnumerateArray().ToList();
                if (releases.Count == 0) break;

                foreach (var release in releases)
                {
                    if (count >= max) break;

                    var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
                    var albumTitle = release.GetProperty("title").GetString();
                    var trackCount = release.GetProperty("track-count").GetInt32();
                    var releaseId = release.GetProperty("id").GetString();

                    var query = new AlbumQuery
                    {
                        Artist = artistCredit,
                        Album = albumTitle,
                        URI = releaseId,
                    };

                    var job = new AlbumJob(query)
                    {
                        ItemNumber = offset + count + 1,
                        EnablesIndexByDefault = true,
                        ExtractorFolderCond = new FolderConditionPatch { MinTrackCount = trackCount, MaxTrackCount = trackCount },
                    };
                    queue.Jobs.Add(job);
                    count++;
                }

                if (releases.Count < limit) break;
                currentOffset += limit;
            }

            SldlLog.Info($"Found {queue.Jobs.Count} releases in MusicBrainz collection '{collectionName}'");
            return queue;
        }
    }
