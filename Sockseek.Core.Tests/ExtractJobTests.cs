using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Unit;

[TestClass]
public class ExtractJobTests
{
    private static async Task<ExtractJob> RunExtractOnlyAsync(
        string input,
        InputType inputType,
        DownloadSettings downloadSettings,
        ExtractionMode? requestedModeOverride = null)
    {
        var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
        var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
        var engine = new DownloadEngine(engineSettings, clientManager);
        var extractJob = new ExtractJob(input, inputType)
        {
            AutoProcessResult = false,
            RequestedModeOverride = requestedModeOverride,
        };

        engine.Enqueue(extractJob, downloadSettings);
        engine.CompleteEnqueue();

        await engine.RunAsync(CancellationToken.None);
        return extractJob;
    }

    [TestMethod]
    public async Task ExtractJob_AutoProcessResultFalse_StopsAfterExtraction()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile, "\"Artist - Track\"");

            var (engineSettings, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;
            engineSettings.Username = "test_user";
            engineSettings.Password = "test_pass";

            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List) { AutoProcessResult = false };

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, extractJob.TerminalOutcome);
            Assert.IsNotNull(extractJob.Result);
            Assert.IsInstanceOfType(extractJob.Result, typeof(JobList));

            var extractedList = (JobList)extractJob.Result;
            var childExtract = extractedList.Jobs.OfType<ExtractJob>().Single();
            Assert.IsNull(childExtract.Result, "Detached extraction should not recurse into the extracted result.");
            Assert.AreEqual(JobLifecycleState.Pending, childExtract.LifecycleState);
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ResultSubtree_InheritsWorkflowId()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile, "\"Artist - Track\"");

            var (engineSettings, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;
            engineSettings.Username = "test_user";
            engineSettings.Password = "test_pass";

            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var workflowId = Guid.NewGuid();
            var extractJob = new ExtractJob(listFile, InputType.List)
            {
                AutoProcessResult = false,
                WorkflowId = workflowId,
            };

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            Assert.AreEqual(workflowId, extractJob.Result.WorkflowId);

            if (extractJob.Result is JobList extractedList)
                Assert.IsTrue(extractedList.Jobs.All(job => job.WorkflowId == workflowId));
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_CsvSongRows_DefaultMode_RemainSongs()
    {
        var csvFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(csvFile, "artist,title\nArtist,Track\n");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = csvFile;
            downloadSettings.Extraction.InputType = InputType.CSV;

            var extractJob = await RunExtractOnlyAsync(csvFile, InputType.CSV, downloadSettings);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            Assert.IsInstanceOfType(list.Jobs.Single(), typeof(SongJob));
        }
        finally
        {
            if (File.Exists(csvFile))
                File.Delete(csvFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_CsvSongRows_ExplicitAlbumMode_RemainSongs()
    {
        var csvFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(csvFile, "artist,title\nArtist,Track\n");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = csvFile;
            downloadSettings.Extraction.InputType = InputType.CSV;
            downloadSettings.Extraction.RequestedMode = ExtractionMode.Album;

            var extractJob = await RunExtractOnlyAsync(csvFile, InputType.CSV, downloadSettings);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            Assert.IsInstanceOfType(list.Jobs.Single(), typeof(SongJob));
        }
        finally
        {
            if (File.Exists(csvFile))
                File.Delete(csvFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_CsvSongRows_UpgradeToAlbum_UpgradesToAlbums()
    {
        var csvFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(csvFile, "artist,title\nArtist,Track\n");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = csvFile;
            downloadSettings.Extraction.InputType = InputType.CSV;
            downloadSettings.Extraction.UpgradeToAlbum = true;

            var extractJob = await RunExtractOnlyAsync(csvFile, InputType.CSV, downloadSettings);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            Assert.IsInstanceOfType(list.Jobs.Single(), typeof(AlbumJob));
        }
        finally
        {
            if (File.Exists(csvFile))
                File.Delete(csvFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListStringPrefixes_OverrideDefaultStringAlbumMode()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile,
                "s:\"Artist - Track\"\n" +
                "a:\"Artist - Album\"");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            var childExtracts = list.Jobs.OfType<ExtractJob>().ToList();

            Assert.AreEqual(2, childExtracts.Count);
            Assert.IsInstanceOfType(childExtracts[0].Result, typeof(SongJob));
            Assert.IsInstanceOfType(childExtracts[1].Result, typeof(AlbumJob));
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListKeyedTitle_DefaultsToSongButPrefixesOverride()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile,
                "\"artist=Default Artist, title=Default Track\"\n" +
                "a:\"artist=Album Artist, title=Album Hint\"\n" +
                "s:\"artist=Song Artist, title=Song Track\"");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            var childExtracts = list.Jobs.OfType<ExtractJob>().ToList();

            Assert.AreEqual(3, childExtracts.Count);

            Assert.IsInstanceOfType(childExtracts[0].Result, typeof(SongJob));
            var defaultSong = (SongJob)childExtracts[0].Result!;
            Assert.AreEqual("Default Artist", defaultSong.Query.Artist);
            Assert.AreEqual("Default Track", defaultSong.Query.Title);

            Assert.IsInstanceOfType(childExtracts[1].Result, typeof(AlbumJob));
            var albumOverride = (AlbumJob)childExtracts[1].Result!;
            Assert.AreEqual("Album Artist", albumOverride.Query.Artist);
            Assert.AreEqual("", albumOverride.Query.Album);
            Assert.AreEqual("Album Hint", albumOverride.Query.SearchHint);

            Assert.IsInstanceOfType(childExtracts[2].Result, typeof(SongJob));
            var songOverride = (SongJob)childExtracts[2].Result!;
            Assert.AreEqual("Song Artist", songOverride.Query.Artist);
            Assert.AreEqual("Song Track", songOverride.Query.Title);
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListKeyedTitle_GlobalAlbumModeCanBeOverriddenBySongPrefix()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile,
                "\"artist=Global Album Artist, title=Global Album Hint\"\n" +
                "s:\"artist=Song Artist, title=Song Track\"");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;
            downloadSettings.Extraction.RequestedMode = ExtractionMode.Album;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            var childExtracts = list.Jobs.OfType<ExtractJob>().ToList();

            Assert.AreEqual(2, childExtracts.Count);

            Assert.IsInstanceOfType(childExtracts[0].Result, typeof(AlbumJob));
            var globalAlbum = (AlbumJob)childExtracts[0].Result!;
            Assert.AreEqual("Global Album Artist", globalAlbum.Query.Artist);
            Assert.AreEqual("", globalAlbum.Query.Album);
            Assert.AreEqual("Global Album Hint", globalAlbum.Query.SearchHint);

            Assert.IsInstanceOfType(childExtracts[1].Result, typeof(SongJob));
            var songOverride = (SongJob)childExtracts[1].Result!;
            Assert.AreEqual("Song Artist", songOverride.Query.Artist);
            Assert.AreEqual("Song Track", songOverride.Query.Title);
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListKeyedTitle_GlobalSongModeCanBeOverriddenByAlbumPrefix()
    {
        var listFile = Path.GetTempFileName();

        try
        {
            File.WriteAllText(listFile,
                "\"artist=Global Song Artist, title=Global Song Track\"\n" +
                "a:\"artist=Album Artist, title=Album Hint\"");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;
            downloadSettings.Extraction.RequestedMode = ExtractionMode.Song;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var list = (JobList)extractJob.Result;
            var childExtracts = list.Jobs.OfType<ExtractJob>().ToList();

            Assert.AreEqual(2, childExtracts.Count);

            Assert.IsInstanceOfType(childExtracts[0].Result, typeof(SongJob));
            var globalSong = (SongJob)childExtracts[0].Result!;
            Assert.AreEqual("Global Song Artist", globalSong.Query.Artist);
            Assert.AreEqual("Global Song Track", globalSong.Query.Title);

            Assert.IsInstanceOfType(childExtracts[1].Result, typeof(AlbumJob));
            var albumOverride = (AlbumJob)childExtracts[1].Result!;
            Assert.AreEqual("Album Artist", albumOverride.Query.Artist);
            Assert.AreEqual("", albumOverride.Query.Album);
            Assert.AreEqual("Album Hint", albumOverride.Query.SearchHint);
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListNestedCsv_AlbumPrefixDoesNotUpgradeSongRows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-list-csv-mode-" + Guid.NewGuid());
        var csvFile = Path.Combine(tempDir, "songs.csv");
        var listFile = Path.Combine(tempDir, "list.txt");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(csvFile, "artist,title\nArtist,Track\n");
            File.WriteAllText(listFile, $"a:\"{csvFile}\"\n");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var rootList = (JobList)extractJob.Result;
            var nestedExtract = rootList.Jobs.OfType<ExtractJob>().Single();
            Assert.IsNotNull(nestedExtract.Result);
            var nestedList = (JobList)nestedExtract.Result;

            Assert.IsInstanceOfType(nestedList.Jobs.Single(), typeof(SongJob));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExtractJob_ListNestedCsv_UpgradeToAlbumUpgradesSongRows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sockseek-list-csv-upgrade-" + Guid.NewGuid());
        var csvFile = Path.Combine(tempDir, "songs.csv");
        var listFile = Path.Combine(tempDir, "list.txt");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(csvFile, "artist,title\nArtist,Track\n");
            File.WriteAllText(listFile, $"\"{csvFile}\"\n");

            var (_, downloadSettings) = TestHelpers.CreateDefaultSettings();
            downloadSettings.Extraction.Input = listFile;
            downloadSettings.Extraction.InputType = InputType.List;
            downloadSettings.Extraction.UpgradeToAlbum = true;

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var clientManager = TestHelpers.CreateMockClientManager(new ClientTests.MockSoulseekClient([]), engineSettings);
            var engine = new DownloadEngine(engineSettings, clientManager);
            var extractJob = new ExtractJob(listFile, InputType.List);

            engine.Enqueue(extractJob, downloadSettings);
            engine.CompleteEnqueue();

            await engine.RunAsync(CancellationToken.None);

            Assert.IsNotNull(extractJob.Result);
            var rootList = (JobList)extractJob.Result;
            var nestedExtract = rootList.Jobs.OfType<ExtractJob>().Single();
            Assert.IsNotNull(nestedExtract.Result);
            var nestedList = (JobList)nestedExtract.Result;

            Assert.IsInstanceOfType(nestedList.Jobs.Single(), typeof(AlbumJob));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
