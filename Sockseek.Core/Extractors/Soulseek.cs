using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Web;
using Soulseek;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public class SoulseekExtractor : IExtractor, IInputMatcher
    {
        public static bool InputMatches(string input)
        {
            return input.StartsWith("slsk://", StringComparison.OrdinalIgnoreCase);
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            var uri = HttpUtility.UrlDecode(input);

            if (input.EndsWith('/') || extraction.RequestedMode == ExtractionMode.Album)
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

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Invalid Soulseek URI: missing username.");
            if (string.IsNullOrWhiteSpace(path) || path.Trim('\\').Length == 0)
                throw new ArgumentException("Invalid Soulseek URI: missing path.");

            return (username, path);
        }
    }
