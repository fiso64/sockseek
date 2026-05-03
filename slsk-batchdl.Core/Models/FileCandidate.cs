using Soulseek;

namespace Sldl.Core.Models;
    public class FileCandidate
    {
        public SearchResponse Response { get; }
        public Soulseek.File File { get; }

        public string Username => Response.Username;
        public string Filename => File.Filename;

        public FileCandidate(SearchResponse response, Soulseek.File file)
        {
            Response = response;
            File = file;
        }
    }
