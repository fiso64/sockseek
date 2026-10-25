using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class SoundCloudExtractor : IExtractor, IInputMatcher
    {
        private const string ApiBase = "https://api-v2.soundcloud.com";

        // api-v2 rejects /tracks requests with more ids than this.
        private const int TracksPerRequest = 50;

        private readonly SoundCloudSettings _soundCloud;

        public SoundCloudExtractor(SoundCloudSettings soundCloud) { _soundCloud = soundCloud; }

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && input.Contains("soundcloud.com");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;

            if (string.IsNullOrWhiteSpace(_soundCloud.AuthToken))
                throw new InvalidOperationException("A SoundCloud OAuth token is required. Set it with --soundcloud-token.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", _soundCloud.AuthToken);

            context.Log.Info("Loading item..");
            var resolved = await GetJson(httpClient, $"{ApiBase}/resolve?url={Uri.EscapeDataString(input.Trim())}");

            var kind = resolved.GetProperty("kind").GetString();
            if (kind == "track")
                return ToSongJob(resolved, 1);
            if (kind != "playlist")
                throw new NotSupportedException($"Only SoundCloud tracks and playlists are supported, but the URL points to a {kind}.");

            var entries = resolved.GetProperty("tracks").EnumerateArray()
                .Select((track, index) => (Number: index + 1, Track: track))
                .ToList();

            if (extraction.Reverse)
                entries.Reverse();
            entries = entries.Skip(extraction.Offset).Take(extraction.MaxTracks).ToList();

            // The playlist response only embeds full metadata for the first few tracks; the rest are bare ids.
            var missingIds = entries.Where(e => IsStub(e.Track)).Select(e => Id(e.Track)).ToList();

            var details = new Dictionary<long, JsonElement>();
            foreach (var batch in missingIds.Chunk(TracksPerRequest))
            {
                var tracks = await GetJson(httpClient, $"{ApiBase}/tracks?ids={string.Join(',', batch)}");
                foreach (var track in tracks.EnumerateArray())
                    details[Id(track)] = track;
            }

            var result = new JobList { ItemName = resolved.GetProperty("title").GetString(), EnablesIndexByDefault = true };
            foreach (var (number, entry) in entries)
            {
                JsonElement? track = !IsStub(entry) ? entry
                    : details.TryGetValue(Id(entry), out var fetched) ? fetched : null;
                if (track is not { } full)
                {
                    context.Log.Warn($"Skipping playlist track {number}: SoundCloud returned no details for it (removed or private).");
                    continue;
                }
                result.Jobs.Add(ToSongJob(full, number));
            }

            return result;
        }

        internal static SongJob ToSongJob(JsonElement track, int itemNumber)
        {
            var publisherArtist = track.TryGetProperty("publisher_metadata", out var publisher) && publisher.ValueKind == JsonValueKind.Object
                && publisher.TryGetProperty("artist", out var artist) ? artist.GetString() : null;
            if (string.IsNullOrWhiteSpace(publisherArtist))
                publisherArtist = null;

            var artistName = publisherArtist ?? track.GetProperty("user").GetProperty("username").GetString() ?? "";
            var title = track.GetProperty("title").GetString() ?? "";
            if (artistName.Length > 0 && title.StartsWith(artistName + " - ", StringComparison.OrdinalIgnoreCase))
                title = title[(artistName.Length + 3)..];

            if (publisherArtist == null)
            {
                var (titleArtist, splitTitle) = Utils.SplitArtistAndTitle(title);
                if (titleArtist != null)
                    (artistName, title) = (titleArtist, splitTitle);
            }

            var query = new SongQuery
            {
                Artist = artistName,
                Title = title,
                URI = track.TryGetProperty("permalink_url", out var url) ? url.GetString() ?? "" : "",
                // For snippet-only tracks (e.g. Go+), `duration` is the ~30s snippet; `full_duration` is the whole track.
                Length = track.TryGetProperty("full_duration", out var duration) || track.TryGetProperty("duration", out duration)
                    ? duration.GetInt32() / 1000 : -1,
            };

            return new SongJob(query) { ItemNumber = itemNumber };
        }

        private static bool IsStub(JsonElement track) => !track.TryGetProperty("title", out _);

        private static long Id(JsonElement track) => track.GetProperty("id").GetInt64();

        private static async Task<JsonElement> GetJson(HttpClient httpClient, string url)
        {
            using var response = await httpClient.GetAsync(url);
            EnsureSuccess(response);
            await using var stream = await response.Content.ReadAsStreamAsync();
            return (await JsonDocument.ParseAsync(stream)).RootElement;
        }

        internal static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"SoundCloud rejected the request ({(int)response.StatusCode}); check that the OAuth token is valid.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("SoundCloud is rate limiting requests (429); try again later.");
            response.EnsureSuccessStatusCode();
        }
    }
