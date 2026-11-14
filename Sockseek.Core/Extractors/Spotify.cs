using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;
using System.Text.Json;

using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class SpotifyExtractor : IExtractor, IInputMatcher
    {
        private enum SourceKind { Playlist, Album, LikedSongs, LikedAlbums, Artist }
        private (string? Input, SourceKind? Kind) describedSource;

        public JobSourceInfo? DescribeSource(ExtractJob source, Job item)
        {
            if (describedSource.Input != source.Input)
                describedSource = (source.Input, ClassifyInput(source.Input));
            SourceKind? kind = describedSource.Kind;
            string label = kind switch
            {
                SourceKind.Playlist => "Spotify playlist",
                SourceKind.Album => "Spotify album",
                SourceKind.LikedSongs => "Spotify liked songs",
                SourceKind.LikedAlbums => "Spotify liked albums",
                SourceKind.Artist => "Spotify artist",
                _ => "Spotify",
            };
            // TODO [v4]: Manual code like this is concerning. Maybe handle it more generally for extractors in presentation.
            if (ReferenceEquals(source, item))
            {
                string? unit = null;
                if (kind is SourceKind.Playlist or SourceKind.LikedSongs or SourceKind.LikedAlbums)
                {
                    unit = kind == SourceKind.LikedAlbums ? "album" : "song";
                    if (source.Config.Extraction.UpgradeToAlbum) unit = "album";
                    if (source.Config.Search.GroupResults) unit = "job";
                }
                return new(Detail: label, UseResultName: true,
                    PreviewTitle: label, PreviewDetail: source.Input,
                    CollectionLabel: label, CollectionItemName: unit);
            }
            if (item is JobList list) return new(Detail: label, PreviewDetail: $"{list.Count} jobs");
            if (item.ItemNumber <= 0) return null;
            if (item is SongJob) return new(Detail: $"Spotify · track {item.ItemNumber}",
                PreviewDetail: kind == SourceKind.LikedSongs ? $"Liked song {item.ItemNumber}"
                    : kind == SourceKind.Playlist ? $"Track {item.ItemNumber}" : $"Spotify · track {item.ItemNumber}");
            if (item is AlbumJob && kind == SourceKind.LikedAlbums)
                return new(Detail: $"Spotify · album {item.ItemNumber}", PreviewDetail: $"Liked album {item.ItemNumber}");
            return null;
        }

        private readonly SpotifySettings _spotify;
        private Spotify? spotifyClient;
        public string playlistUri = "";

        public SpotifyExtractor(SpotifySettings spotify) { _spotify = spotify; }

        public static bool InputMatches(string input)
        {
            return input.Equals("spotify-likes", StringComparison.OrdinalIgnoreCase)
                || input.Equals("spotify-albums", StringComparison.OrdinalIgnoreCase)
                || input.IsInternetUrl() && input.Contains("spotify.com", StringComparison.OrdinalIgnoreCase);
        }

        // Collection identity is independent of its editable name.
        private static SourceKind? ClassifyInput(string input)
        {
            if (input.Equals("spotify-likes", StringComparison.OrdinalIgnoreCase)) return SourceKind.LikedSongs;
            if (input.Equals("spotify-albums", StringComparison.OrdinalIgnoreCase)) return SourceKind.LikedAlbums;
            string path;
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
            else
            {
                int suffix = input.AsSpan().IndexOfAny('?', '#');
                path = suffix < 0 ? input : input[..suffix];
            }
            // Query/fragment text is not the resource being extracted.
            return path.Contains("/album/", StringComparison.Ordinal) ? SourceKind.Album
                : path.Contains("/artist/", StringComparison.Ordinal) ? SourceKind.Artist
                : path.Contains("/playlist/", StringComparison.Ordinal) ? SourceKind.Playlist : null;
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            context ??= ExtractorContext.None;
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            SourceKind? sourceKind = ClassifyInput(input);
            bool needLogin = sourceKind is SourceKind.LikedSongs or SourceKind.LikedAlbums
                || extraction.RemoveTracksFromSource;

            if (string.IsNullOrEmpty(_spotify.ClientId) || string.IsNullOrEmpty(_spotify.ClientSecret))
                throw new Exception("Spotify client ID and secret are required. Create a Spotify developer app and pass --spotify-id and --spotify-secret.");

            using var spotifySession = new Spotify(
                _spotify.ClientId ?? "",
                _spotify.ClientSecret ?? "",
                _spotify.Token ?? "",
                _spotify.Refresh ?? "",
                context.Log,
                context.SensitiveOutput);
            spotifyClient = spotifySession;
            await spotifyClient.Authorize(needLogin, extraction.RemoveTracksFromSource);

            Job result;

            if (sourceKind == SourceKind.LikedSongs)
            {
                context.Log.Info("Loading likes..");
                var songs = await spotifyClient.GetLikes(max, off);
                var slj   = new JobList { ItemName = "Spotify Likes", EnablesIndexByDefault = true };
                foreach (var s in songs) slj.Add(s);
                result = slj;
            }
            else if (sourceKind == SourceKind.LikedAlbums)
            {
                context.Log.Info("Loading liked albums..");
                var albumList = await spotifyClient.GetAlbums(max, off);
                albumList.ItemName              = "Spotify Liked Albums";
                albumList.EnablesIndexByDefault = true;
                result = albumList;
            }
            else if (sourceKind == SourceKind.Album)
            {
                context.Log.Info("Loading album..");
                result = await spotifyClient.GetAlbumJob(input, extraction);
            }
            else if (sourceKind == SourceKind.Artist)
            {
                throw new Exception("Spotify artist download currently not supported.");
            }
            else
            {
                var songs = new List<SongJob>();
                string? playlistName = null;

                try
                {
                    context.Log.Info("Loading playlist");
                    (playlistName, playlistUri, songs) = await spotifyClient.GetPlaylist(input, max, off);
                }
                catch (APIException ex)
                {
                    if (!needLogin)
                    {
                        await spotifyClient.Authorize(true, extraction.RemoveTracksFromSource);
                        try
                        {
                            (playlistName, playlistUri, songs) = await spotifyClient.GetPlaylist(input, max, off);
                        }
                        catch (APIException retryEx)
                        {
                            throw SpotifyApiRequestException.Create("Spotify playlist request after user authorization", retryEx);
                        }
                    }
                    else throw SpotifyApiRequestException.Create("Spotify playlist request", ex);
                }

                if (!string.IsNullOrWhiteSpace(playlistUri))
                {
                    foreach (var s in songs.Where(s => !string.IsNullOrWhiteSpace(s.Query.URI)))
                        s.SourceMutation = SourceMutation.RemoveSpotifyPlaylistTrack(playlistUri, s.Query.URI, s.ItemNumber);
                }

                var slj = new JobList { ItemName = playlistName, EnablesIndexByDefault = true };
                foreach (var s in songs) slj.Add(s);
                result = slj;
            }

            if (reverse && result is JobList jl)
            {
                jl.ReverseMembers();
                if (jl.Jobs.Count > offset)
                    jl.RemoveRange(0, offset);
                else
                    jl.Clear();

                if (jl.Jobs.Count > maxTracks)
                    jl.RemoveRange(maxTracks, jl.Jobs.Count - maxTracks);
            }

            return result;
        }

    }


    public sealed class SpotifyApiRequestException : Exception
    {
        private readonly APIException apiException;

        private SpotifyApiRequestException(string message, APIException apiException)
            : base(message)
        {
            this.apiException = apiException;
        }

        public static SpotifyApiRequestException Create(string operation, APIException apiException)
            => new($"{operation} failed: {Describe(apiException)}", apiException);

        public override string ToString()
            => $"{base.ToString()}{Environment.NewLine}{Environment.NewLine}Original Spotify API exception:{Environment.NewLine}{apiException}";

        private static string Describe(APIException exception)
        {
            if (exception.Response == null)
                return exception.Message;

            var parts = new List<string>
            {
                $"HTTP {(int)exception.Response.StatusCode} {exception.Response.StatusCode}",
            };

            if (!string.IsNullOrWhiteSpace(exception.Response.ContentType))
                parts.Add($"content-type={exception.Response.ContentType}");

            var body = FormatBody(exception.Response.Body);
            if (!string.IsNullOrWhiteSpace(body))
                parts.Add($"body={body}");

            if (exception.Response.Headers != null
                && exception.Response.Headers.TryGetValue("Retry-After", out var retryAfter)
                && !string.IsNullOrWhiteSpace(retryAfter))
            {
                parts.Add($"retry-after={retryAfter}");
            }

            return string.Join("; ", parts);
        }

        private static string? FormatBody(object? body)
        {
            if (body == null)
                return null;
            if (body is string text)
                return text;

            try
            {
                return JsonSerializer.Serialize(body);
            }
            catch
            {
                return body.ToString();
            }
        }
    }


    public class Spotify : IDisposable
    {
        private EmbedIOAuthServer? _server;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private string _clientToken;
        private string _clientRefreshToken;
        private SpotifyClient? _client;
        private readonly IJobLog _log;
        private readonly ISensitiveOutput _sensitiveOutput;
        private bool loggedIn = false;

        public Spotify(
            string clientId = "",
            string clientSecret = "",
            string token = "",
            string refreshToken = "",
            IJobLog? log = null,
            ISensitiveOutput? sensitiveOutput = null)
        {
            _clientId           = clientId ?? "";
            _clientSecret       = clientSecret ?? "";
            _clientToken        = token ?? "";
            _clientRefreshToken = refreshToken ?? "";
            _log                = log ?? ExtractorContext.None.Log;
            _sensitiveOutput    = sensitiveOutput ?? NullSensitiveOutput.Instance;
        }

        public async Task Authorize(bool login = false, bool needModify = false)
        {
            _client = null;
            _log.Debug($"Authorizing (login={login}, modify={needModify})");

            if (!login)
            {
                var config   = SpotifyClientConfig.CreateDefault();
                var request  = new ClientCredentialsRequest(_clientId, _clientSecret);
                var response = await new OAuthClient(config).RequestToken(request);
                _client = new SpotifyClient(config.WithToken(response.AccessToken));
            }
            else
            {
                Swan.Logging.Logger.NoLogging();
                _server = new EmbedIOAuthServer(new Uri("http://127.0.0.1:48721/callback"), 48721);
                await _server.Start();
                _log.Debug("Auth server started");

                var existingOk = false;
                if (_clientToken.Length != 0 || _clientRefreshToken.Length != 0)
                {
                    existingOk = await this.TryExistingToken();
                    loggedIn   = true;
                }

                if (!existingOk)
                {
                    _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                    _server.ErrorReceived             += OnErrorReceived;

                    var scope = new List<string>
                    {
                        Scopes.UserLibraryRead, Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative
                    };

                    if (needModify)
                    {
                        scope.Add(Scopes.PlaylistModifyPublic);
                        scope.Add(Scopes.PlaylistModifyPrivate);
                    }

                    var request = new LoginRequest(_server.BaseUri, _clientId, LoginRequest.ResponseType.Code) { Scope = scope };
                    try { BrowserUtil.Open(request.ToUri()); }
                    catch (Exception) { _log.Info($"Unable to open URL, manually open: {request.ToUri()}"); }
                }

                await IsClientReady();
            }
        }

        private async Task<bool> TryExistingToken()
        {
            if (_clientToken.Length != 0)
            {
                _log.Debug("Testing access with existing token...");
                var client = new SpotifyClient(_clientToken);
                try
                {
                    var me = await client.UserProfile.Current();
                    _log.Debug("Access is good!");
                    _client = client;
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Info($"Could not make an API call with existing token: {ex.Message}");
                }
            }

            if (_clientRefreshToken.Length != 0)
            {
                _log.Info("Trying to renew access with refresh token...");
                var refreshRequest = new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, _clientRefreshToken);
                try
                {
                    var oauthClient    = new OAuthClient();
                    var refreshResponse = await oauthClient.RequestToken(refreshRequest);
                    _log.Debug("Received refreshed access token.");
                    _clientToken = refreshResponse.AccessToken;
                    _client      = new SpotifyClient(_clientToken);
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Info($"Could not refresh access token with refresh token: {ex}");
                }
            }
            else
            {
                _log.Info("No refresh token present, cannot refresh existing access");
            }

            _log.Info("Not possible to access API without login! Falling back to login flow...");
            return false;
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            _log.Debug("Authorization code received");
            if (_server != null)
                await _server.Stop();

            var config        = SpotifyClientConfig.CreateDefault();
            _log.Debug("Getting token response..");
            var tokenResponse = await new OAuthClient(config).RequestToken(
                new AuthorizationCodeTokenRequest(_clientId, _clientSecret, response.Code, new Uri("http://127.0.0.1:48721/callback")));

            _log.Debug("Got token");
            _sensitiveOutput.WriteLine("spotify-token=" + tokenResponse.AccessToken);
            _clientToken = tokenResponse.AccessToken;
            _sensitiveOutput.WriteLine("");
            _sensitiveOutput.WriteLine("spotify-refresh=" + tokenResponse.RefreshToken);
            _sensitiveOutput.WriteLine("");
            _clientRefreshToken = tokenResponse.RefreshToken;
            _client             = new SpotifyClient(tokenResponse.AccessToken);
            loggedIn            = true;
        }

        private async Task OnErrorReceived(object sender, string error, string? state)
        {
            _log.Debug($"Auth error: {error}");
            if (_server != null)
                await _server.Stop();
            throw new Exception($"Aborting authorization, error received: {error}");
        }

        public async Task<bool> IsClientReady()
        {
            while (_client == null)
                await Task.Delay(1000);
            return true;
        }

        private SpotifyClient RequireClient()
            => _client ?? throw new InvalidOperationException("Spotify client is not authorized.");

        private static object? ReadPropertyValue(object? source, string propertyName)
            => source?.ReadProperty(propertyName);

        private static string ReadStringProperty(object? source, string propertyName)
            => ReadPropertyValue(source, propertyName) as string ?? "";

        private static int ReadIntProperty(object? source, string propertyName)
            => ReadPropertyValue(source, propertyName) switch
            {
                int value => value,
                long value => checked((int)value),
                double value => checked((int)value),
                _ => -1,
            };

        private static string ReadNestedStringProperty(object? source, params string[] propertyPath)
        {
            object? current = source;
            foreach (var propertyName in propertyPath)
                current = ReadPropertyValue(current, propertyName);
            return current as string ?? "";
        }

        private static string[] ReadArtists(object? track)
        {
            var artists = ReadPropertyValue(track, "artists") as IEnumerable<object>;
            return artists?
                .Select(artist => ReadStringProperty(artist, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray() ?? [];
        }

        private static bool TryCreateTrackQuery(object? track, out SongQuery query)
        {
            var artists = ReadArtists(track);
            var title = ReadStringProperty(track, "name");

            if (artists.Length == 0 || string.IsNullOrWhiteSpace(title))
            {
                query = new SongQuery();
                return false;
            }

            var durationMs = ReadIntProperty(track, "durationMs");
            query = new SongQuery
            {
                Artist = artists[0],
                Album  = ReadNestedStringProperty(track, "album", "name"),
                Title  = title,
                Length = durationMs >= 0 ? durationMs / 1000 : -1,
                URI    = ReadStringProperty(track, "uri"),
            };
            return true;
        }

        public async Task<List<SongJob>> GetLikes(int max = int.MaxValue, int offset = 0)
        {
            if (!loggedIn)
                throw new Exception("Can't get liked music as user is not logged in");

            var client = RequireClient();
            var songs = new List<SongJob>();
            int limit = Math.Min(max, 50);
            int num   = offset + 1;

            while (true)
            {
                var tracks = await client.Library.GetTracks(new LibraryTracksRequest { Limit = limit, Offset = offset });

                var items = tracks.Items;
                if (items == null)
                    break;

                foreach (var track in items)
                {
                    if (TryCreateTrackQuery(track.Track, out var query))
                        songs.Add(new SongJob(query) { ItemNumber = num++ });
                }

                if (items.Count < limit || songs.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - songs.Count, 50);
            }

            return songs;
        }

        public async Task<JobList> GetAlbums(int max = int.MaxValue, int offset = 0)
        {
            if (!loggedIn)
                throw new Exception("Can't get liked albums as user is not logged in");

            var client = RequireClient();
            var queue = new JobList();
            int limit = Math.Min(max, 50);
            int num   = offset + 1;

            while (true)
            {
                var albums = await client.Library.GetAlbums(new LibraryAlbumsRequest { Limit = limit, Offset = offset });

                var items = albums.Items;
                if (items == null)
                    break;

                foreach (var savedAlbum in items)
                {
                    var album  = savedAlbum.Album;
                    if (album == null || string.IsNullOrWhiteSpace(album.Name))
                        continue;

                    string artist = album.Artists?
                        .Select(a => a.Name)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";

                    var query = new AlbumQuery
                    {
                        Album         = album.Name,
                        Artist        = artist,
                    };

                    var job = new AlbumJob(query)
                    {
                        ItemNumber            = num++,
                        EnablesIndexByDefault = true,
                        ExtractorFolderCond = new FolderConditionPatch { MinTrackCount = album.TotalTracks },
                    };
                    queue.Add(job);
                }

                if (items.Count < limit || queue.Jobs.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - queue.Jobs.Count, 50);
            }

            _log.Info($"Found {queue.Jobs.Count} liked albums");
            return queue;
        }

        public async Task RemoveTrackFromPlaylist(string playlistId, string trackUri)
        {
            var client = RequireClient();
            var request = new PlaylistRemoveItemsRequestV2
            {
                Items = [new PlaylistRemoveItemsRequestV2.Item { Uri = trackUri }],
            };
            try { await client.Playlists.RemovePlaylistItems(playlistId, request); }
            catch { }
        }

        public async Task<(string Name, string Id, List<SongJob> Songs)> GetPlaylist(string url, int max = int.MaxValue, int offset = 0)
        {
            var client = RequireClient();
            var playlistId = GetPlaylistIdFromUrl(url);
            var p          = await client.Playlists.Get(playlistId);

            var songs = new List<SongJob>();
            int limit = Math.Min(max, 50);
            int num   = offset + 1;

            while (true)
            {
                var tracks = await client.Playlists.GetPlaylistItems(playlistId, new PlaylistGetItemsRequest { Limit = limit, Offset = offset });

                var items = tracks.Items;
                if (items == null)
                    break;

                foreach (var track in items)
                {
                    try
                    {
                        if (TryCreateTrackQuery(track.Item, out var query))
                            songs.Add(new SongJob(query) { ItemNumber = num++ });
                    }
                    catch { continue; }
                }

                if (items.Count < limit || songs.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - songs.Count, 50);
            }

            return (p.Name ?? "", p.Id ?? playlistId, songs);
        }

        private static string GetPlaylistIdFromUrl(string url)
        {
            var uri      = new Uri(url);
            var segments = uri.Segments;
            return segments[segments.Length - 1].TrimEnd('/');
        }

        public async Task<AlbumJob> GetAlbumJob(string url, ExtractionSettings extraction)
        {
            var client = RequireClient();
            var albumId = GetAlbumIdFromUrl(url);
            var album   = await client.Albums.Get(albumId);
            if (album?.Tracks?.Items == null)
                throw new Exception("Could not retrieve Spotify album tracks.");

            var trackCount = album.Tracks.Items.Count;

            var albumQuery = new AlbumQuery
            {
                Album  = album.Name ?? "",
                Artist = album.Artists?
                    .Select(a => a.Name)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "",
            };

            var albumJob = new AlbumJob(albumQuery);
            if (extraction.SetAlbumMinTrackCount || extraction.SetAlbumMaxTrackCount)
            {
                albumJob.ExtractorFolderCond = new FolderConditionPatch
                {
                    MinTrackCount = extraction.SetAlbumMinTrackCount ? trackCount : null,
                    MaxTrackCount = extraction.SetAlbumMaxTrackCount ? trackCount : null,
                };
            }

            return albumJob;
        }

        private static string GetAlbumIdFromUrl(string url)
        {
            var uri      = new Uri(url);
            var segments = uri.Segments;
            return segments[^1].TrimEnd('/');
        }

        public void Dispose()
        {
            _server?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
