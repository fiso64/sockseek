using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using System.Collections.Generic;
using System.Linq;

namespace Tests.Jobs
{
    [TestClass]
    public class JobUpgradeTests
    {
        [TestMethod]
        public void SongJob_UpgradeToAlbum_ReturnsAlbumJob()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Title" }) { ItemNumber = 5, LineNumber = 10 };
            var results = ((IUpgradeable)song).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AlbumJob));
            var album = (AlbumJob)results[0];
            Assert.AreEqual("Artist", album.Query.Artist);
            Assert.AreEqual("Album", album.Query.Album);
            Assert.AreEqual("", album.Query.SearchHint);
            Assert.IsNotNull(album.ExtractorFolderCond);
            CollectionAssert.AreEqual(new[] { "Title" }, album.ExtractorFolderCond.RequiredTrackTitles);
            Assert.AreEqual(1, album.UpgradeSources.Count);
            Assert.AreEqual(5, album.ItemNumber);
            Assert.AreEqual(10, album.LineNumber);
        }

        [TestMethod]
        public void SongJob_UpgradeToAggregate_ReturnsAggregateJob()
        {
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var results = ((IUpgradeable)song).Upgrade(album: false, aggregate: true).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AggregateJob));
            var agg = (AggregateJob)results[0];
            Assert.AreEqual("Artist", agg.Query.Artist);
            Assert.AreEqual("Title", agg.Query.Title);
            Assert.IsNotNull(agg.ItemName);
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_CreatesNestedList()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            list.Add(new SongJob(new SongQuery { Artist = "A2", Title = "T2" }));

            var results = ((IUpgradeable)list).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(JobList));
            var resultList = (JobList)results[0];
            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.IsInstanceOfType(resultList.Jobs[0], typeof(AlbumJob));
            Assert.IsInstanceOfType(resultList.Jobs[1], typeof(AlbumJob));
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DeduplicatesSongUpgradedAlbumsAndMergesRequiredTracks()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 1" }));
            list.Add(new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 2" }));
            list.Add(new SongJob(new SongQuery { Artist = "Artist", Album = "Other Album", Title = "Other Track" }));

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
            var album = (AlbumJob)resultList.Jobs[0];
            Assert.AreEqual("Album", album.Query.Album);
            CollectionAssert.AreEqual(new[] { "Track 1", "Track 2" }, album.ExtractorFolderCond!.RequiredTrackTitles);
            Assert.AreEqual(2, album.UpgradeSources.Count);
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DoesNotMergeDifferentArtists()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "Artist 1", Album = "Album", Title = "Track 1" }));
            list.Add(new SongJob(new SongQuery { Artist = "Artist 2", Album = "Album", Title = "Track 2" }));

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DoesNotMergeExplicitAlbumJobs()
        {
            var list = new JobList("My List");
            list.Add(new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));
            list.Add(new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.IsTrue(resultList.Jobs.OfType<AlbumJob>().All(a => a.UpgradeSources.Count == 0));
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DoesNotMergeSongUpgradeWithExplicitAlbumJob()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track" }));
            list.Add(new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.AreEqual(1, resultList.Jobs.OfType<AlbumJob>().Count(a => a.UpgradeSources.Count == 1));
            Assert.AreEqual(1, resultList.Jobs.OfType<AlbumJob>().Count(a => a.UpgradeSources.Count == 0));
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DoesNotMergeSongUpgradesWithDifferentTrackCountConditions()
        {
            var first = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 1" })
            {
                ExtractorFolderCond = new FolderConditionPatch { MinTrackCount = 5, MaxTrackCount = 5 }
            };
            var second = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 2" })
            {
                ExtractorFolderCond = new FolderConditionPatch { MinTrackCount = 6, MaxTrackCount = 6 }
            };
            var third = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 3" })
            {
                ExtractorFolderCond = new FolderConditionPatch { MinTrackCount = 5, MaxTrackCount = 5 }
            };
            var list = new JobList("My List", new Job[] { first, second, third });

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
            var fiveTrackAlbum = resultList.Jobs.OfType<AlbumJob>().Single(a => a.ExtractorFolderCond!.MinTrackCount == 5);
            CollectionAssert.AreEqual(new[] { "Track 1", "Track 3" }, fiveTrackAlbum.ExtractorFolderCond!.RequiredTrackTitles);
            var sixTrackAlbum = resultList.Jobs.OfType<AlbumJob>().Single(a => a.ExtractorFolderCond!.MinTrackCount == 6);
            CollectionAssert.AreEqual(new[] { "Track 2" }, sixTrackAlbum.ExtractorFolderCond!.RequiredTrackTitles);
        }

        [TestMethod]
        public void JobList_UpgradeToAlbum_DoesNotMergeSongUpgradesWithDifferentFileConditions()
        {
            var first = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 1" })
            {
                ExtractorCond = new FileConditionPatch { Formats = ["flac"] }
            };
            var second = new SongJob(new SongQuery { Artist = "Artist", Album = "Album", Title = "Track 2" })
            {
                ExtractorCond = new FileConditionPatch { Formats = ["mp3"] }
            };
            var list = new JobList("My List", new Job[] { first, second });

            var resultList = (JobList)((IUpgradeable)list).Upgrade(album: true, aggregate: false).Single();

            Assert.AreEqual(2, resultList.Jobs.Count);
        }

        [TestMethod]
        public void JobList_UpgradeToAggregate_FlattensList()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            list.Add(new SongJob(new SongQuery { Artist = "A2", Title = "T2" }));

            var results = ((IUpgradeable)list).Upgrade(album: false, aggregate: true).ToList();

            Assert.AreEqual(2, results.Count);
            Assert.IsInstanceOfType(results[0], typeof(AggregateJob));
            Assert.IsInstanceOfType(results[1], typeof(AggregateJob));
        }

        [TestMethod]
        public void JobList_MixedContent_PreservesNonUpgradeable()
        {
            var list = new JobList("My List");
            list.Add(new SongJob(new SongQuery { Artist = "A1", Title = "T1" }));
            var ej = new ExtractJob("some-input");
            list.Add(ej);

            var results = ((IUpgradeable)list).Upgrade(album: true, aggregate: false).ToList();

            Assert.AreEqual(1, results.Count);
            var resultList = (JobList)results[0];
            Assert.AreEqual(2, resultList.Jobs.Count);
            Assert.IsInstanceOfType(resultList.Jobs[0], typeof(AlbumJob));
            Assert.AreSame(ej, resultList.Jobs[1]);
        }
    }
}
