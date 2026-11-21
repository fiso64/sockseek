using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;

namespace Tests.Server;

[TestClass]
[DoNotParallelize] // This test temporarily owns the process console writers.
public class TestServerHostTests
{
    [TestMethod]
    public async Task Build_DoesNotWriteCoreOrHostLogsToConsole()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            await using (var app = TestServerHost.Build([]))
            {
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<DownloadEngine>()
                    .LogInformation("core log must not escape");
                app.Logger.LogInformation("host log must not escape");
            }

            Assert.AreEqual("", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Build_UsesAppDirectoryAsContentRootRegardlessOfCwd()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        var tempCwd = Directory.CreateTempSubdirectory("sockseek-cwd-");
        try
        {
            Directory.SetCurrentDirectory(tempCwd.FullName);
            await using var app = TestServerHost.Build([]);

            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Path.TrimEndingDirectorySeparator(app.Environment.ContentRootPath));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            tempCwd.Delete(true);
        }
    }
}
