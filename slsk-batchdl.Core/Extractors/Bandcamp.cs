using Sldl.Core.Models;
using Sldl.Core.Jobs;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;
using Sldl.Core.Settings;

namespace Sldl.Core.Extractors;
    public partial class BandcampExtractor : IExtractor, IInputMatcher
    {
        private readonly BandcampSettings _bandcamp;

        public BandcampExtractor(BandcampSettings bandcamp) { _bandcamp = bandcamp; }

        [GeneratedRegex(@"band_id=(\d+)&")]
        private static partial Regex BandIdRegex();

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && input.Contains("bandcamp.com");
        }

        public async Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var maxTracks = extraction.MaxTracks;
            var offset    = extraction.Offset;
            var reverse   = extraction.Reverse;

            var jobs = new List<Job>();  // temporary; always ends up with exactly one item
            bool isTrack = input.Contains("/track/");
            bool isAlbum = !isTrack && input.Contains("/album/");
            bool isWishlist = !isTrack && !isAlbum && input.Contains("/wishlist");
            bool isArtist = !isTrack && !isAlbum && !isWishlist;

            if (isWishlist)
            {
                SldlLog.Info("Retrieving bandcamp wishlist..");
                HtmlDocument doc;

                if (!string.IsNullOrEmpty(_bandcamp.HtmlFromFile))
                {
                    doc = new HtmlDocument();
                    doc.Load(_bandcamp.HtmlFromFile);
                }
                else
                {
                    var web = new HtmlWeb();
                    doc = await web.LoadFromWebAsync(input);
                }

                var items = doc.DocumentNode.SelectNodes("//li[contains(@class, 'collection-item-container')]");

                if (items != null)
                {
                    var albumList = new JobList { ItemName = "Bandcamp Wishlist", EnablesIndexByDefault = true };
                    int num = 1;
                    foreach (var item in items)
                    {
                        var titleNode  = item.SelectSingleNode(".//div[contains(@class, 'collection-item-title')]");
                        var artistNode = item.SelectSingleNode(".//div[contains(@class, 'collection-item-artist')]");

                        if (titleNode != null && artistNode != null)
                        {
                            string album  = titleNode.InnerText.UnHtmlString().Trim();
                            string artist = artistNode.InnerText.UnHtmlString().Trim();

                            if (artist.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
                                artist = artist.Substring(3).Trim();

                            albumList.Jobs.Add(new AlbumJob(new AlbumQuery { Album = album, Artist = artist })
                            {
                                ItemNumber = num++,
                            });
                        }
                    }
                    jobs.Add(albumList);
                }
            }
            else if (isArtist)
            {
                SldlLog.Info("Retrieving bandcamp artist discography..");
                using var httpClient = new HttpClient();
                string response;

                if (!string.IsNullOrEmpty(_bandcamp.HtmlFromFile))
                {
                    response = await File.ReadAllTextAsync(_bandcamp.HtmlFromFile);
                }
                else
                {
                    string artistUrl = input.TrimEnd('/');
                    if (!artistUrl.EndsWith("/music"))
                        artistUrl += "/music";
                    response = await httpClient.GetStringAsync(artistUrl);
                }

                var match = BandIdRegex().Match(response);
                var id = match.Groups[1].Value;

                var address = $"http://bandcamp.com/api/mobile/24/band_details?band_id={id}";
                var responseString = await httpClient.GetStringAsync(address);
                var jsonDocument = JsonDocument.Parse(responseString);
                var root = jsonDocument.RootElement;

                string artistName = root.GetProperty("name").GetString();

                var albumList = new JobList { ItemName = artistName, EnablesIndexByDefault = true };
                int num = 1;
                foreach (var item in root.GetProperty("discography").EnumerateArray())
                {
                    string albumTitle  = item.GetProperty("title").GetString();
                    string albumArtist = item.GetProperty("artist_name").GetString()
                                     ?? item.GetProperty("band_name").GetString();

                    albumList.Jobs.Add(new AlbumJob(new AlbumQuery { Album = albumTitle, Artist = albumArtist })
                    {
                        ItemNumber = num++,
                    });
                }
                jobs.Add(albumList);
            }
            else
            {
                SldlLog.Info("Retrieving bandcamp item..");
                HtmlDocument doc;

                if (!string.IsNullOrEmpty(_bandcamp.HtmlFromFile))
                {
                    doc = new HtmlDocument();
                    doc.Load(_bandcamp.HtmlFromFile);
                }
                else
                {
                    var web = new HtmlWeb();
                    doc = await web.LoadFromWebAsync(input);
                }

                var nameSection = doc.DocumentNode.SelectSingleNode("//div[@id='name-section']");
                var name = nameSection.SelectSingleNode(".//h2[contains(@class, 'trackTitle')]").InnerText.UnHtmlString().Trim();

                if (isAlbum)
                {
                    var artist = nameSection.SelectSingleNode(".//h3/span/a").InnerText.UnHtmlString().Trim();
                    var query  = new AlbumQuery { Artist = artist, Album = name };
                    var folderCond = new FolderConditionPatch();

                    if (extraction.SetAlbumMinTrackCount || extraction.SetAlbumMaxTrackCount)
                    {
                        var trackTable = doc.DocumentNode.SelectSingleNode("//*[@id='track_table']");
                        int n = trackTable.SelectNodes(".//tr").Count;

                        if (extraction.SetAlbumMinTrackCount) folderCond.MinTrackCount = n;
                        if (extraction.SetAlbumMaxTrackCount) folderCond.MaxTrackCount = n;
                    }

                    jobs.Add(new AlbumJob(query) { ExtractorFolderCond = folderCond });
                }
                else
                {
                    var album  = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span/a").InnerText.UnHtmlString().Trim();
                    var artist = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span[last()]/a").InnerText.UnHtmlString().Trim();
                    var songQuery = new SongQuery { Artist = artist, Title = name, Album = album };
                    jobs.Add(new SongJob(songQuery));
                }
            }

            var result = jobs[0];

            if (reverse && result is JobList jl)
            {
                jl.Jobs.Reverse();
                if (jl.Jobs.Count > offset) jl.Jobs.RemoveRange(0, offset);
                else jl.Jobs.Clear();
                if (jl.Jobs.Count > maxTracks) jl.Jobs.RemoveRange(maxTracks, jl.Jobs.Count - maxTracks);
            }

            return result;
        }
    }
