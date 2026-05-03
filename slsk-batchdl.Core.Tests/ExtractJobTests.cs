using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.Unit;

[TestClass]
public class ExtractJobTests
{
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

            Assert.AreEqual(JobState.Done, extractJob.State);
            Assert.IsNotNull(extractJob.Result);
            Assert.IsInstanceOfType(extractJob.Result, typeof(JobList));

            var extractedList = (JobList)extractJob.Result;
            var childExtract = extractedList.Jobs.OfType<ExtractJob>().Single();
            Assert.IsNull(childExtract.Result, "Detached extraction should not recurse into the extracted result.");
            Assert.AreEqual(JobState.Pending, childExtract.State);
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
}
