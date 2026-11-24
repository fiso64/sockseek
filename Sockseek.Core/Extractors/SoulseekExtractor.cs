using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
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
        public static string SourceName => "Soulseek link";

        public JobSourceInfo? DescribeSource(ExtractJob source, Job item)
        {
            if (ReferenceEquals(source, item)) return new(
                Detail: source.SourceInputType == InputType.List ? source.Input : "Soulseek import",
                PreviewTitle: "Soulseek link",
                NestedPreviewTitle: ClassifyLink(source.Input, source.Config?.Extraction.RequestedMode ?? source.RequestedModeOverride) == SoulseekLinkInterpretation.RemoteDirectory
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

        public static string FormatLink(string username, string remotePath, bool directory = false)
        {
            var components = remotePath.Replace('\\', '/').TrimEnd('/').Split('/');
            string path = string.Join("/", components.Select(Uri.EscapeDataString));
            return $"slsk://{Uri.EscapeDataString(username)}/{path}" + (directory ? "/" : "");
        }

        public static SoulseekLinkInterpretation ClassifyLink(
            string input,
            ExtractionMode? requestedMode)
        {
            bool directoryLink = IsDirectoryLink(input);

            return requestedMode switch
            {
                ExtractionMode.Album => SoulseekLinkInterpretation.MusicAlbum,
                ExtractionMode.Song => SoulseekLinkInterpretation.MusicTrack,
                _ when directoryLink => SoulseekLinkInterpretation.RemoteDirectory,
                _ => SoulseekLinkInterpretation.RemoteFile,
            };
        }

        public Task<Job> ExtractAsync(string input, ExtractionSettings extraction, ExtractorContext? context = null)
        {
            var (username, path) = ParseSoulseekUri(input);
            var interpretation = ClassifyLink(input, extraction.RequestedMode);

            if (interpretation == SoulseekLinkInterpretation.MusicAlbum)
            {
                var directory = path.TrimEnd('\\');
                var query = new AlbumQuery
                {
                    Album = Utils.GetFileNameSlsk(directory),
                    URI = input,
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
                if (IsDirectoryLink(input))
                    throw new ArgumentException("A directory Soulseek URI cannot be interpreted as one music track.");

                var query = new SongQuery { Title = Utils.GetFileNameWithoutExtSlsk(path), URI = input };
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

        private static bool IsDirectoryLink(string input)
            => input.EndsWith('/') || input.EndsWith("%2F", StringComparison.OrdinalIgnoreCase);

        private static PeerFileTarget CreateTarget(string username, string path)
            => new(
                new PeerFileIdentity(username, path),
                size: null,
                extension: Path.GetExtension(path));

        private static (string Username, string Path) ParseSoulseekUri(string uri)
        {
            var parts = uri["slsk://".Length..].Split('/', 2);
            var username = Uri.UnescapeDataString(parts[0]);
            var path = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]).Replace('/', '\\') : "";

            if (username.Length == 0)
                throw new ArgumentException("Invalid Soulseek URI: missing username.");
            if (path.Length == 0 || path.Trim('\\').Length == 0)
                throw new ArgumentException("Invalid Soulseek URI: missing path.");

            return (username, path);
        }
    }
