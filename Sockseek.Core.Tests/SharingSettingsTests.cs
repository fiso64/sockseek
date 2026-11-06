using System.Net;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Sharing;

namespace Tests.Core;

[TestClass]
public sealed class SharingSettingsTests
{
    [TestMethod]
    public void RemotePathKey_UsesOneNfcCaseInsensitiveBinaryRule()
    {
        var composed = RemotePathKey.Create(@"Music\Café\TRACK.FLAC");
        var decomposed = RemotePathKey.Create("music/cafe\u0301/track.flac");

        Assert.AreEqual(composed, decomposed);
        CollectionAssert.AreEqual(composed.ToArray(), decomposed.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(@"\rooted")]
    [DataRow(@"Alias\\empty")]
    [DataRow(@"Alias\.\file")]
    [DataRow(@"Alias\..\file")]
    [DataRow("Alias\0file")]
    public void RemotePathKey_RejectsAmbiguousOrUnsafePaths(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => RemotePathKey.Create(path));
    }

    [TestMethod]
    public void RemotePathKey_RejectsInvalidUtf16()
    {
        string invalid = "Alias\\" + '\uD800';
        Assert.ThrowsExactly<ArgumentException>(() => RemotePathKey.Create(invalid));
    }

    [TestMethod]
    public void RemotePathKey_PreservesNonNulControlCharacters()
    {
        const string path = "Alias\\line\n\tfile.flac";

        var key = RemotePathKey.Create(path);

        Assert.AreEqual(key, RemotePathKey.Create(path));
        Assert.AreNotEqual(key, RemotePathKey.Create("Alias\\line file.flac"));
    }

    [TestMethod]
    public void RemotePathKey_DoesNotImposeSockseekOnlyEncodedLength()
    {
        string longPath = "Alias\\" + new string('x', 32 * 1_024);

        Assert.IsNotNull(RemotePathKey.Create(longPath));
    }

    [TestMethod]
    public void ShareRootParser_SupportsExplicitAndDerivedAliasForms()
    {
        var explicitRoot = ShareRootParser.Parse("[Live Sets] C:\\media\\live");
        var derivedRoot = ShareRootParser.Parse("C:\\media\\music");

        Assert.AreEqual("Live Sets", explicitRoot.Alias);
        Assert.AreEqual(@"C:\media\live", explicitRoot.LocalPath);
        Assert.IsNull(derivedRoot.Alias);
        Assert.AreEqual(@"C:\media\music", derivedRoot.LocalPath);
    }

    [TestMethod]
    [DataRow("[]C:\\music")]
    [DataRow("[.]C:\\music")]
    [DataRow("[a/b]C:\\music")]
    [DataRow("[missing")]
    [DataRow("[alias]")]
    public void ShareRootParser_RejectsInvalidForms(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ShareRootParser.Parse(value));
    }

    [TestMethod]
    public void Validator_NormalizesAliasesExclusionsAndPeerRestrictions()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests");
        string rootPath = Path.Combine(parent, "MUSIC");
        string excludedPath = Path.Combine(rootPath, "private");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = rootPath }],
                ExcludedDirectories = [excludedPath],
                Filters = [@"\.part$"],
            },
            PeerRestrictions = new PeerRestrictionSettings
            {
                UploadAccess = new UploadAccessSettings
                {
                    BlockedUsernames = [" Alice "],
                    BlockedIpAddresses = ["::ffff:192.0.2.10"],
                },
                PrivateMessages = new PrivateMessageAccessSettings
                {
                    BlockedUsernames = [" Bob "],
                },
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual("MUSIC", settings.Sharing.Roots[0].EffectiveAlias);
        Assert.AreEqual(Path.GetFullPath(rootPath), settings.Sharing.Roots[0].LocalPath);
        Assert.AreEqual(Path.GetFullPath(excludedPath), settings.Sharing.ExcludedDirectories[0]);
        Assert.AreEqual(" Alice ", settings.PeerRestrictions.UploadAccess.BlockedUsernames[0]);
        Assert.AreEqual("192.0.2.10", settings.PeerRestrictions.UploadAccess.BlockedIpAddresses[0]);
        Assert.AreEqual(" Bob ", settings.PeerRestrictions.PrivateMessages.BlockedUsernames[0]);

        var policy = new PeerRestrictionPolicy(settings.PeerRestrictions);
        Assert.IsTrue(policy.IsUploadUsernameBlocked(" Alice "));
        Assert.IsFalse(policy.IsUploadUsernameBlocked("alice"));
        Assert.IsTrue(policy.IsUploadIpAddressBlocked(IPAddress.Parse("::ffff:192.0.2.10")));
        Assert.IsFalse(policy.IsPrivateMessageBlocked(" Alice "));
        Assert.IsTrue(policy.IsPrivateMessageBlocked(" Bob "));
    }

    [TestMethod]
    public void PeerRestrictionPolicyAtomicallyMergesIndependentBaselinesAndOverrides()
    {
        var policy = new PeerRestrictionPolicy(new PeerRestrictionSettings
        {
            UploadAccess = new UploadAccessSettings
            {
                BlockedUsernames = ["Configured", "ResetMe"],
                BlockedIpAddresses = ["192.0.2.7"],
            },
            PrivateMessages = new PrivateMessageAccessSettings
            {
                BlockedUsernames = ["MessageConfigured"],
            },
        });

        policy.ReplaceUsernameOverrides(new Dictionary<
            (PeerRestrictionKind Kind, string Username), PeerUsernameRestrictionOverride>
        {
            [(PeerRestrictionKind.UploadAccess, "Configured")] =
                PeerUsernameRestrictionOverride.Allowed,
            [(PeerRestrictionKind.UploadAccess, "OverrideOnly")] =
                PeerUsernameRestrictionOverride.Blocked,
            [(PeerRestrictionKind.PrivateMessages, "OverrideOnly")] =
                PeerUsernameRestrictionOverride.Allowed,
        });

        Assert.IsFalse(policy.IsUploadUsernameBlocked("Configured"));
        Assert.IsTrue(policy.IsUploadUsernameBlocked("OverrideOnly"));
        Assert.IsFalse(policy.IsPrivateMessageBlocked("OverrideOnly"));
        Assert.IsTrue(policy.IsPrivateMessageBlocked("MessageConfigured"));
        Assert.IsTrue(policy.IsUploadAccessBlocked(
            "Configured",
            new IPEndPoint(IPAddress.Parse("192.0.2.7"), 1)),
            "Configured IP denial must win over an allowed username override.");
        Assert.IsFalse(policy.IsUploadUsernameBlocked("configured"),
            "Soulseek username matching remains exact ordinal.");

        policy.ReloadConfigured(new PeerRestrictionSettings
        {
            UploadAccess = new UploadAccessSettings
            {
                BlockedUsernames = ["Configured", "NewConfigured"],
                BlockedIpAddresses = ["192.0.2.8"],
            },
            PrivateMessages = new PrivateMessageAccessSettings
            {
                BlockedUsernames = ["NewMessageConfigured"],
            },
        });
        Assert.IsFalse(policy.IsUploadUsernameBlocked("Configured"));
        Assert.IsTrue(policy.IsUploadUsernameBlocked("NewConfigured"));
        Assert.IsTrue(policy.IsUploadUsernameBlocked("OverrideOnly"));
        Assert.IsFalse(policy.IsUploadIpAddressBlocked(IPAddress.Parse("192.0.2.7")));
        Assert.IsTrue(policy.IsUploadIpAddressBlocked(IPAddress.Parse("192.0.2.8")));
        Assert.IsTrue(policy.IsPrivateMessageBlocked("NewMessageConfigured"));

        policy.SetUsernameOverride(PeerRestrictionKind.UploadAccess, "Configured", null);
        Assert.IsTrue(policy.IsUploadUsernameBlocked("Configured"),
            "Removing an override resets the username to configured policy.");
        policy.SetUsernameOverride(
            PeerRestrictionKind.UploadAccess,
            "OverrideOnly",
            PeerUsernameRestrictionOverride.Allowed);
        Assert.IsFalse(policy.IsUploadUsernameBlocked("OverrideOnly"));
        Assert.IsFalse(policy.IsPrivateMessageBlocked("OverrideOnly"),
            "Changing upload access must not change private-message policy.");
    }

    [TestMethod]
    public async Task PeerRestrictionConcurrentReadersOnlyObserveWholePublishedSnapshots()
    {
        var first = new PeerRestrictionSettings
        {
            UploadAccess = new UploadAccessSettings
            {
                BlockedUsernames = ["First"],
                BlockedIpAddresses = ["192.0.2.1"],
            },
            PrivateMessages = new PrivateMessageAccessSettings { BlockedUsernames = ["First"] },
        };
        var second = new PeerRestrictionSettings
        {
            UploadAccess = new UploadAccessSettings
            {
                BlockedUsernames = ["Second"],
                BlockedIpAddresses = ["192.0.2.2"],
            },
            PrivateMessages = new PrivateMessageAccessSettings { BlockedUsernames = ["Second"] },
        };
        var policy = new PeerRestrictionPolicy(first);
        var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task writer = Task.Run(async () =>
        {
            await begin.Task;
            for (int index = 0; index < 64; index++)
                policy.ReloadConfigured((index & 1) == 0 ? second : first);
        });
        Task reader = Task.Run(async () =>
        {
            await begin.Task;
            for (int index = 0; index < 64; index++)
            {
                PeerRestrictionSnapshot observed = policy.Snapshot;
                bool isFirst = observed.UploadAccess.ConfiguredBlockedUsernames.Contains("First");
                bool isSecond = observed.UploadAccess.ConfiguredBlockedUsernames.Contains("Second");
                Assert.AreNotEqual(isFirst, isSecond);
                Assert.AreEqual(
                    isFirst,
                    observed.ConfiguredUploadBlockedIpAddresses.Contains(
                        IPAddress.Parse("192.0.2.1")));
                Assert.AreEqual(
                    isSecond,
                    observed.ConfiguredUploadBlockedIpAddresses.Contains(
                        IPAddress.Parse("192.0.2.2")));
                Assert.AreEqual(isFirst,
                    observed.PrivateMessages.ConfiguredBlockedUsernames.Contains("First"));
                Assert.AreEqual(isSecond,
                    observed.PrivateMessages.ConfiguredBlockedUsernames.Contains("Second"));
            }
        });

        begin.SetResult();
        await Task.WhenAll(writer, reader);
    }

    [TestMethod]
    public void Validator_RejectsRemoteAliasCollisions()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(parent, "one"),
                        Alias = "Music",
                    },
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(parent, "two"),
                        Alias = "MUSIC",
                    },
                ],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void Validator_AllowsOverlappingRootsWithDistinctRemoteAliases()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests", "root");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings { LocalPath = root },
                    new ShareRootSettings { LocalPath = Path.Combine(root, "child") },
                ],
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual(2, settings.Sharing.Roots.Count);
        Assert.AreNotEqual(
            settings.Sharing.Roots[0].EffectiveAlias,
            settings.Sharing.Roots[1].EffectiveAlias);
    }

    [TestMethod]
    public void Validator_AllowsVolumeRootWithExplicitAlias()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = volumeRoot, Alias = "root" }],
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual("root", settings.Sharing.Roots.Single().EffectiveAlias);
    }

    [TestMethod]
    public void Validator_RejectsOversizedPublicAlias()
    {
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(
                            Path.GetTempPath(),
                            "sockseek-sharing-tests",
                            "oversized-alias"),
                        Alias = new string('a', SharingSettingsValidator.MaximumEncodedValueBytes + 1),
                    },
                ],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void Validator_CompilesEveryFilterWithFiniteTimeout()
    {
        var regex = SharingSettingsValidator.CompileFilter(@"^(a+)+\1$");

        Assert.AreEqual(SharingSettingsValidator.RegexTimeout, regex.MatchTimeout);
        Assert.ThrowsExactly<RegexParseException>(
            () => SharingSettingsValidator.CompileFilter("[invalid"));
    }

    [TestMethod]
    public void Validator_RequiresExplicitAliasForVolumeRoot()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = volumeRoot }],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void SettingsClone_DoesNotShareMutableDaemonCollections()
    {
        var original = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = "root", EffectiveAlias = "alias" }],
                Filters = ["one"],
            },
            PeerRestrictions = new PeerRestrictionSettings
            {
                UploadAccess = new UploadAccessSettings { BlockedUsernames = ["alice"] },
                PrivateMessages = new PrivateMessageAccessSettings { BlockedUsernames = ["bob"] },
            },
        };

        var clone = SettingsCloner.Clone(original);
        clone.Sharing.Roots[0].LocalPath = "changed";
        clone.Sharing.Filters.Add("two");
        clone.PeerRestrictions.UploadAccess.BlockedUsernames.Clear();
        clone.PeerRestrictions.PrivateMessages.BlockedUsernames.Clear();

        Assert.AreEqual("root", original.Sharing.Roots[0].LocalPath);
        CollectionAssert.AreEqual(new[] { "one" }, original.Sharing.Filters);
        CollectionAssert.AreEqual(
            new[] { "alice" },
            original.PeerRestrictions.UploadAccess.BlockedUsernames);
        CollectionAssert.AreEqual(
            new[] { "bob" },
            original.PeerRestrictions.PrivateMessages.BlockedUsernames);
    }

    [TestMethod]
    public void SettingsClone_DoesNotShareMutableDownloadState()
    {
        var original = new DownloadSettings
        {
            Output = new OutputSettings
            {
                OnComplete = ["first"],
                IncompleteAlbumAction = new IncompleteAlbumActionSettings
                {
                    Kind = IncompleteAlbumActionKind.Move,
                    Path = "incomplete",
                },
            },
            Search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["flac"] },
                NecessaryFolderCond = new FolderConditions { RequiredTrackTitles = ["intro"] },
            },
            Preprocess = new PreprocessSettings
            {
                Regex = [(new RegexFields { Title = "before" }, new RegexFields { Title = "after" })],
            },
            AppliedAutoProfiles = ["lossless"],
        };

        DownloadSettings clone = SettingsCloner.Clone(original);
        Assert.AreNotSame(
            original.Preprocess.Regex![0].Match,
            clone.Preprocess.Regex![0].Match);
        clone.Output.OnComplete!.Add("second");
        clone.Output.IncompleteAlbumAction.Path = "changed";
        clone.Search.NecessaryCond.Formats[0] = "mp3";
        clone.Search.NecessaryFolderCond.RequiredTrackTitles[0] = "outro";
        clone.Preprocess.Regex[0] = (
            new RegexFields { Title = "changed" },
            clone.Preprocess.Regex[0].Replace);
        clone.AppliedAutoProfiles.Add("portable");

        CollectionAssert.AreEqual(new[] { "first" }, original.Output.OnComplete);
        Assert.AreEqual("incomplete", original.Output.IncompleteAlbumAction.Path);
        CollectionAssert.AreEqual(new[] { "flac" }, original.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(
            new[] { "intro" }, original.Search.NecessaryFolderCond.RequiredTrackTitles);
        Assert.AreEqual("before", original.Preprocess.Regex[0].Match.Title);
        CollectionAssert.AreEquivalent(new[] { "lossless" }, original.AppliedAutoProfiles.ToArray());
    }
}
