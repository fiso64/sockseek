using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Core;

[TestClass]
public sealed class PlacementPlannerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptyFormat_PreservesSelectionTreeBelowOutputParent(bool directlyUnderParent)
    {
        string parent = TestParent();
        var plan = new DirectoryTransferPlan(directlyUnderParent ? "" : "Collection", [
            Entry(@"Share\Collection\Disc 2\02.flac", ["Disc 2"]),
            Entry(@"Share\Collection\01.flac", []),
        ], representsDirectorySelection: !directlyUnderParent);

        var placements = new PlacementPlanner().PlanDirectory(
            plan,
            new OutputSettings { ParentDir = parent });

        CollectionAssert.AreEqual(
            directlyUnderParent ? ["01.flac"] : new[] { "Collection", "01.flac" },
            placements[0].RelativePath.Components.ToArray());
        CollectionAssert.AreEqual(
            directlyUnderParent ? ["Disc 2", "02.flac"] : new[] { "Collection", "Disc 2", "02.flac" },
            placements[1].RelativePath.Components.ToArray());
        Assert.IsTrue(placements.All(item => Utils.IsInDirectory(item.OutputPath, parent, strict: true)));
    }

    [TestMethod]
    public void ConfiguredFormat_RendersStructuralVariablesAndPreservesExtension()
    {
        string parent = TestParent();
        var plan = new DirectoryTransferPlan("Collection", [
            Entry(@"Share\Collection\Disc 2\02.flac", ["Disc 2"], extension: "flac"),
        ]);
        var output = new OutputSettings
        {
            ParentDir = parent,
            NameFormat = "{peer-username}/{relative-path}",
        };

        var placement = new PlacementPlanner().PlanDirectory(plan, output).Single();

        CollectionAssert.AreEqual(
            new[] { "Peer", "Disc 2", "02.flac" },
            placement.RelativePath.Components.ToArray());
    }

    [TestMethod]
    public void ConfiguredFormat_OutputExtensionVariableDoesNotDuplicateExtension()
    {
        string parent = TestParent();
        var plan = new DirectoryTransferPlan("Collection", [
            Entry(@"Share\Collection\02.flac", [], extension: ".flac"),
        ]);

        var placement = new PlacementPlanner().PlanDirectory(
            plan,
            new OutputSettings
            {
                ParentDir = parent,
                NameFormat = "{filename}{ext}",
            }).Single();

        CollectionAssert.AreEqual(
            new[] { "02.flac" },
            placement.RelativePath.Components.ToArray());
    }

    [TestMethod]
    public void AmbiguousExtensionAlias_IsNotPartOfStructuralVocabulary()
    {
        Assert.IsFalse(NameFormatVariableProvider.Supported.Contains("extension"));
        Assert.ThrowsExactly<UnsupportedNameFormatVariableException>(() =>
            new PlacementPlanner().PlanFile(
                Target(@"Share\notes.txt"),
                RelativeOutputPath.FromRemoteFile(Target(@"Share\notes.txt")),
                new OutputSettings
                {
                    ParentDir = TestParent(),
                    NameFormat = "{filename}{extension}",
                }));
    }

    [TestMethod]
    public void RemovedStructuralAliases_AreRejected()
    {
        var target = Target(@"Share\notes.txt", extension: ".txt");
        foreach (string variable in new[] { "selection-root", "relative-directory", "remote-file", "remote-folder", "remote-ext" })
        {
            Assert.ThrowsExactly<UnsupportedNameFormatVariableException>(() =>
                new PlacementPlanner().PlanFile(
                    target,
                    RelativeOutputPath.FromRemoteFile(target),
                    new OutputSettings
                    {
                        ParentDir = TestParent(),
                        NameFormat = $"{{{variable}}}",
                    }), variable);
        }
    }

    [TestMethod]
    public void DirectFileFormat_FolderNameMeansImmediateRemoteParent()
    {
        string parent = TestParent();
        var target = Target(@"Share\Documents\notes.txt", extension: ".txt");
        var placement = new PlacementPlanner().PlanFile(
            target,
            RelativeOutputPath.FromRemoteFile(target),
            new OutputSettings
            {
                ParentDir = parent,
                NameFormat = "{foldername}/{filename}",
            });

        CollectionAssert.AreEqual(
            new[] { "Documents", "notes.txt" },
            placement.RelativePath.Components.ToArray());
    }

    [TestMethod]
    public void SanitizedCaseAndUnicodeCollisions_GetStableSuffixes()
    {
        string parent = TestParent();
        var plan = new DirectoryTransferPlan("Root", [
            Entry(@"Root\A?.txt", []),
            Entry(@"Root\a*.txt", []),
            Entry("Root\\caf\u00E9.txt", []),
            Entry("Root\\cafe\u0301.txt", []),
        ]);

        var placements = new PlacementPlanner().PlanDirectory(
            plan,
            new OutputSettings { ParentDir = parent, InvalidReplaceStr = "_" });
        var leaves = placements.Select(item => Path.GetFileName(item.OutputPath)).ToArray();

        CollectionAssert.Contains(leaves, "A_.txt");
        CollectionAssert.Contains(leaves, "a_ (2).txt");
        CollectionAssert.Contains(leaves, "cafe\u0301.txt");
        CollectionAssert.Contains(leaves, "caf\u00E9 (2).txt");
    }

    [TestMethod]
    public void ControlBearingRemoteTree_IsSanitizedOnlyAtLocalPlacement()
    {
        string parent = TestParent();
        var plan = new DirectoryTransferPlan("Ro\not", [
            Entry("Root\\Di\tsc\\track\u001B.txt", ["Di\tsc"]),
        ]);

        FilePlacement placement = new PlacementPlanner().PlanDirectory(
            plan,
            new OutputSettings { ParentDir = parent, InvalidReplaceStr = "_" }).Single();

        CollectionAssert.AreEqual(
            new[] { "Ro_ot", "Di_sc", "track_.txt" },
            placement.RelativePath.Components.ToArray());
        Assert.AreEqual("Root\\Di\tsc\\track\u001B.txt", placement.Target.Filename);
    }

    [TestMethod]
    public void MusicOnlyVariable_IsRejectedBeforePlacement()
    {
        var output = new OutputSettings
        {
            ParentDir = TestParent(),
            NameFormat = "{artist}/{filename}",
        };

        var exception = Assert.ThrowsExactly<UnsupportedNameFormatVariableException>(() =>
            new PlacementPlanner().PlanDirectory(
                new DirectoryTransferPlan("Root", [Entry(@"Root\file.bin", [])]),
                output));

        Assert.AreEqual("artist", exception.Variable);
    }

    [TestMethod]
    public void SharedSourceVariable_IsAvailableToOrdinaryPlacement()
    {
        var target = Target(@"Share\notes.txt", extension: ".txt");
        var settings = new DownloadSettings
        {
            Output =
            {
                ParentDir = TestParent(),
                NameFormat = "{extractor}/{filename}",
            },
            Extraction =
            {
                InputType = InputType.Soulseek,
                Input = "slsk://Peer/Share/notes.txt",
            },
        };

        var placement = new PlacementPlanner().PlanFile(
            target,
            RelativeOutputPath.FromRemoteFile(target),
            settings);

        CollectionAssert.AreEqual(
            new[] { "Soulseek", "notes.txt" },
            placement.RelativePath.Components.ToArray());
    }

    [TestMethod]
    public void CompletionOnlyVariable_IsRejectedByOrdinaryPlacement()
    {
        var target = Target(@"Share\notes.txt", extension: ".txt");

        var exception = Assert.ThrowsExactly<UnsupportedNameFormatVariableException>(() =>
            new PlacementPlanner().PlanFile(
                target,
                RelativeOutputPath.FromRemoteFile(target),
                new OutputSettings
                {
                    ParentDir = TestParent(),
                    NameFormat = "{path}",
                }));

        Assert.AreEqual("path", exception.Variable);
    }

    [TestMethod]
    public void VariableProvider_DeclaresStructuralPlacementCapabilities()
    {
        Assert.AreEqual(NameFormatVariableProvider.Supported.Count, NameFormatVariableProvider.Capabilities.Count);
        Assert.IsTrue(NameFormatVariableProvider.Capabilities.All(capability =>
            capability.Applicability == NameFormatVariableApplicability.Shared
            && capability.Phase == NameFormatEvaluationPhase.Placement));
        Assert.IsTrue(NameFormatVariableProvider.Capabilities.Any(capability =>
            capability.Name == "relative-path"));
        Assert.IsFalse(NameFormatVariableProvider.Capabilities.Any(capability =>
            capability.Name == "artist"));
    }

    [TestMethod]
    public void TraversalLiteralInFormat_CannotEscapeOutputParent()
    {
        var output = new OutputSettings
        {
            ParentDir = TestParent(),
            NameFormat = "{(..)}/{filename}",
        };

        var placement = new PlacementPlanner().PlanDirectory(
                new DirectoryTransferPlan("Root", [Entry(@"Root\file.bin", [])]),
                output).Single();

        Assert.IsTrue(Utils.IsInDirectory(placement.OutputPath, output.ParentDir!, strict: true));
    }

    private static DirectoryTransferEntry Entry(
        string filename,
        IReadOnlyList<string> components,
        string? extension = null)
        => new(Target(filename, extension), components);

    private static PeerFileTarget Target(string filename, string? extension = null)
        => new(
            new PeerFileIdentity("Peer", filename),
            size: 10,
            extension ?? Path.GetExtension(filename));

    private static string TestParent()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sockseek-remote-placement"));
}
