using Soulseek;

namespace Sldl.Core.Models;

public sealed record SearchRawResult(
    long Sequence,
    int Revision,
    SearchResponse Response,
    Soulseek.File File)
{
    public string Username => Response.Username;
    public string Filename => File.Filename;
}
