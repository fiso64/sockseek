using Sldl.Core.Models;
using Sldl.Core.Jobs;
using System.Web;
using Soulseek;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public class SoulseekExtractor : IExtractor, IInputMatcher
    {
        public static bool InputMatches(string input)
        {
            return input.StartsWith("slsk://", StringComparison.OrdinalIgnoreCase);
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var uri = HttpUtility.UrlDecode(input);

            if (input.EndsWith('/') || extraction.IsAlbum)
            {
                var (username, path) = ParseSoulseekUri(uri);
                var directory = path.TrimEnd('\\');
                var query = new AlbumQuery
                {
                    Album = Path.GetFileName(directory),
                    URI = uri,
                };
                var folder = new AlbumFolder(username, directory, []);
                return Task.FromResult<Job>(new AlbumJob(query)
                {
                    CanBeSkippedOverride = false,
                    ResolvedTarget = folder,
                    ResolvedTargetNeedsInitialFolderRetrieval = true,
                    AllowBrowseResolvedTarget = false,
                });
            }
            else
            {
                // Direct-link single file: pre-populate Candidates so search is skipped.
                var (username, path) = ParseSoulseekUri(uri);

                var response = new SearchResponse(username, -1, false, -1, -1, null);
                var file = new Soulseek.File(-1, path, -1, Path.GetExtension(path));
                var candidate = new FileCandidate(response, file);

                var query = new SongQuery { Title = Path.GetFileNameWithoutExtension(path), URI = uri };
                var song = new SongJob(query) { ResolvedTarget = candidate, Candidates = new List<FileCandidate> { candidate } };
                return Task.FromResult<Job>(song);
            }
        }

        private static (string Username, string Path) ParseSoulseekUri(string uri)
        {
            var parts = uri["slsk://".Length..].Split('/', 2);
            var username = parts[0];
            var path = parts.Length > 1 ? parts[1].TrimEnd('/').Replace('/', '\\') : "";
            return (username, path);
        }
    }
