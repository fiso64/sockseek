using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Core;

[TestClass]
public class PreprocessorTests
{
    [TestMethod]
    public void PreprocessJob_TopLevelSong_AppliesSongPreprocessing()
    {
        var song = new SongJob(new SongQuery
        {
            Artist = "Artist feat. Guest",
            Title = "Song [Live]",
            Album = "Album",
        });

        Preprocessor.PreprocessJob(song, new PreprocessSettings
        {
            RemoveFt = true,
            RemoveBrackets = true,
        });

        Assert.AreEqual("Artist", song.Query.Artist);
        Assert.AreEqual("Song", song.Query.Title);
        Assert.AreEqual("Album", song.Query.Album);
    }
}
