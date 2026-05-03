using System.Collections.Concurrent;
using SearchResponse = Soulseek.SearchResponse;

namespace Sldl.Core.Models;

public class SearchInfo
{
    public ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results;
    public Task? Task { get; set; }

    public SearchInfo(ConcurrentDictionary<string, (SearchResponse, Soulseek.File)> results)
    {
        this.results = results;
    }
}
