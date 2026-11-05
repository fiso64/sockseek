using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public sealed class TestEnvironment
{
    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        // Applies to every CLI test, including Program.Main calls without
        // --no-config. Config tests opt in by passing their own fixture path.
        ConfigManager.DefaultConfigSearchPathsOverride = [];
    }
}
