using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Extractors;
using Sockseek.Core.Settings;

namespace Tests.Core;

[TestClass]
public class SoundCloudExtractorTests
{
    [TestMethod]
    public void Track_WithPublisherArtist_UsesItAsArtist()
    {
        var job = SoundCloudExtractor.ToSongJob(Track("""
            {"id": 1, "title": "World On Fire", "duration": 199889, "permalink_url": "https://soundcloud.com/a/b",
             "user": {"username": "uploader"}, "publisher_metadata": {"artist": "The Royal Concept"}}
            """), 3);

        Assert.AreEqual("The Royal Concept", job.Query.Artist);
        Assert.AreEqual("World On Fire", job.Query.Title);
        Assert.AreEqual(199, job.Query.Length);
        Assert.AreEqual("https://soundcloud.com/a/b", job.Query.URI);
        Assert.IsFalse(job.Query.ArtistMaybeWrong);
        Assert.AreEqual(3, job.ItemNumber);
    }

    [TestMethod]
    public void SnippedTrack_UsesFullDuration()
    {
        var job = SoundCloudExtractor.ToSongJob(Track("""
            {"id": 1, "title": "A Song", "duration": 30000, "full_duration": 199959, "policy": "SNIP", "user": {"username": "uploader"}}
            """), 1);

        Assert.AreEqual(199, job.Query.Length);
    }

    [TestMethod]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}, "publisher_metadata": null}""")]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}, "publisher_metadata": {"artist": ""}}""")]
    [DataRow("""{"id": 1, "title": "Gimme Twice", "user": {"username": "uploader"}}""")]
    public void Track_WithoutPublisherArtist_FallsBackToUploader(string json)
    {
        var job = SoundCloudExtractor.ToSongJob(Track(json), 1);

        Assert.AreEqual("uploader", job.Query.Artist);
        Assert.IsFalse(job.Query.ArtistMaybeWrong);
        Assert.AreEqual(-1, job.Query.Length);
    }

    [TestMethod]
    [DataRow("The Royal Concept - Gimme Twice", null, "The Royal Concept", "Gimme Twice")]
    [DataRow("the royal concept - Gimme Twice", null, "The Royal Concept", "Gimme Twice")]
    [DataRow("Rihanna - BBHMM (Lokal Bootleg)", null, "Rihanna", "BBHMM (Lokal Bootleg)")]
    [DataRow("The Royal Concept - Rihanna - BBHMM", null, "Rihanna", "BBHMM")]
    [DataRow("A - B - C", null, "The Royal Concept", "A - B - C")]
    [DataRow("Gorilla (Sammy Virji - Remix)", null, "The Royal Concept", "Gorilla (Sammy Virji - Remix)")]
    [DataRow("Rihanna - BBHMM", "Publisher Artist", "Publisher Artist", "Rihanna - BBHMM")]
    [DataRow("Publisher Artist - BBHMM", "Publisher Artist", "Publisher Artist", "BBHMM")]
    [DataRow("publisher artist - BBHMM", "Publisher Artist", "Publisher Artist", "BBHMM")]
    [DataRow("Publisher Artist & Friend - BBHMM", "Publisher Artist", "Publisher Artist", "Publisher Artist & Friend - BBHMM")]
    [DataRow("The Royal Concept - BBHMM", "Publisher Artist", "Publisher Artist", "The Royal Concept - BBHMM")]
    public void TitleArtist_IsUsedOnlyWithoutPublisherArtist(string title, string? publisherArtist, string expectedArtist, string expectedTitle)
    {
        var publisher = publisherArtist == null ? "null" : $$"""{"artist": "{{publisherArtist}}"}""";
        var job = SoundCloudExtractor.ToSongJob(Track($$"""
            {"id": 1, "title": "{{title}}", "user": {"username": "The Royal Concept"}, "publisher_metadata": {{publisher}}}
            """), 1);

        Assert.AreEqual(expectedArtist, job.Query.Artist);
        Assert.AreEqual(expectedTitle, job.Query.Title);
    }

    [TestMethod]
    public async Task MissingAuthToken_ReportsHowToSetIt()
    {
        var extractor = new SoundCloudExtractor(new SoundCloudSettings());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => extractor.GetTracks("https://soundcloud.com/artist/sets/playlist", new ExtractionSettings()));

        StringAssert.Contains(error.Message, "--soundcloud-token");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, "rate limiting")]
    [DataRow(HttpStatusCode.Unauthorized, "OAuth token")]
    [DataRow(HttpStatusCode.Forbidden, "OAuth token")]
    public void ErrorResponse_ReportsCause(HttpStatusCode status, string expected)
    {
        using var response = new HttpResponseMessage(status);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => SoundCloudExtractor.EnsureSuccess(response));

        StringAssert.Contains(error.Message, expected);
    }

    [TestMethod]
    public void SoundCloudUrl_SelectsSoundCloudExtractor()
    {
        var (type, extractor) = ExtractorRegistry.GetMatchingExtractor(
            "https://soundcloud.com/artist/sets/playlist", InputType.None, new DownloadSettings());

        Assert.AreEqual(InputType.SoundCloud, type);
        Assert.IsInstanceOfType<SoundCloudExtractor>(extractor);
    }

    private static JsonElement Track(string json) => JsonDocument.Parse(json).RootElement;
}
