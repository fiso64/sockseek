using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using System.Web;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Extractors;
    public enum SoulseekLinkInterpretation
    {
        RemoteFile,
        RemoteDirectory,
        MusicTrack,
        MusicAlbum,
    }

    public class SoulseekExtractor : IExtractor, IInputMatcher
    {
        public JobSourceInfo? DescribeSource(ExtractJob source, Job item)
        {
            if (ReferenceEquals(source, item)) return new(
                Detail: source.SourceInputType == InputType.List ? source.Input : "Soulseek import",
                PreviewTitle: "Soulseek link",
                NestedPreviewTitle: ClassifyLink(source.Input, source.RequestedModeOverride) == SoulseekLinkInterpretation.RemoteDirectory
                    ? "Soulseek directory" : "Soulseek link",
                PreviewDetail: source.Input);
            return item switch
            {
                RemoteFileJob => new(Detail: "Soulseek link", PreviewDetail: "Soulseek link"),
                RemoteDirectoryJob => new(Detail: "Soulseek directory", PreviewDetail: "Exact remote directory"),
                _ => null,
            };
        }

        public static bool InputMatches(string input)
        {
            return input.StartsWith("slsk://", StringComparison.OrdinalIgnoreCase);
        }

        public static SoulseekLinkInterpretation ClassifyLink(
            string input,
            ExtractionMode? requestedMode)
        {
            var uri = HttpUtility.UrlDecode(input);
            bool directoryLink = uri.EndsWith('/');

            return requestedMode switch
            {
                ExtractionMode.Album => SoulseekLinkInterpretation.MusicAlbum,
                ExtractionMode.Song => SoulseekLinkInterpretation.MusicTrack,
                _ when directoryLink => SoulseekLinkInterpretation.RemoteDirectory,
                _ => SoulseekLinkInterpretation.RemoteFile,
            };
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            var uri = HttpUtility.UrlDecode(input);

            bool directoryLink = uri.EndsWith('/');
            var (username, path) = ParseSoulseekUri(uri, directoryLink);
            var interpretation = ClassifyLink(input, extraction.RequestedMode);

            if (interpretation == SoulseekLinkInterpretation.MusicAlbum)
            {
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
                    DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.RetrieveBeforeSelection,
                });
            }

            if (interpretation == SoulseekLinkInterpretation.MusicTrack)
            {
                if (directoryLink)
                    throw new ArgumentException("A directory Soulseek URI cannot be interpreted as one music track.");

                var query = new SongQuery { Title = Path.GetFileNameWithoutExtension(path), URI = uri };
                return Task.FromResult<Job>(new SongJob(query)
                {
                    ExactTarget = CreateTarget(username, path),
                });
            }

            // Soulseek is a file-sharing network. An ordinary remote transfer is
            // therefore the unqualified default unless music intent is explicit.
            if (interpretation == SoulseekLinkInterpretation.RemoteDirectory)
            {
                return Task.FromResult<Job>(new RemoteDirectoryJob(
                    new RemoteDirectorySource.PeerDirectory(
                        new PeerDirectoryIdentity(username, path.TrimEnd('\\')))));
            }

            return Task.FromResult<Job>(new RemoteFileJob(CreateTarget(username, path)));
        }

        private static PeerFileTarget CreateTarget(string username, string path)
            => new(
                new PeerFileIdentity(username, path),
                size: null,
                extension: Path.GetExtension(path));

        private static (string Username, string Path) ParseSoulseekUri(string uri, bool directoryLink)
        {
            var parts = uri["slsk://".Length..].Split('/', 2);
            var username = parts[0];
            var path = parts.Length > 1 ? parts[1].Replace('/', '\\') : "";
            if (directoryLink)
                path = path.TrimEnd('\\');

            if (username.Length == 0)
                throw new ArgumentException("Invalid Soulseek URI: missing username.");
            if (path.Length == 0 || path.Trim('\\').Length == 0)
                throw new ArgumentException("Invalid Soulseek URI: missing path.");

            return (username, path);
        }
    }
