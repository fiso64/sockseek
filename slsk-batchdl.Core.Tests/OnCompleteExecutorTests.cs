using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core;
using System.Reflection;

namespace Tests.OnCompleteExecutorTests
{
    // OnCompleteExecutor has private methods tested via reflection,
    // following the established pattern in this test suite.

    [TestClass]
    public class ParseCommandFlagsTests
    {
        private static dynamic InvokeParseCommandFlags(string rawCommand)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ParseCommandFlags", BindingFlags.NonPublic | BindingFlags.Static)!;
            return method.Invoke(null, new object[] { rawCommand })!;
        }

        private static T Get<T>(object obj, string prop) =>
            (T)obj.GetType().GetProperty(prop)!.GetValue(obj)!;

        [TestMethod]
        public void ParseCommandFlags_NoFlags_ReturnsCommandUnchanged()
        {
            var result = InvokeParseCommandFlags("mycommand arg1");
            Assert.AreEqual("mycommand arg1", Get<string>(result, "Command"));
            Assert.IsFalse(Get<bool>(result, "UseShellExecute"));
        }

        [TestMethod]
        public void ParseCommandFlags_ShellExecuteFlag_SetsUseShellExecute()
        {
            var result = InvokeParseCommandFlags("s:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_TrackOnlyFlag_SetsOnlyTrack()
        {
            var result = InvokeParseCommandFlags("t:mycommand");
            Assert.IsTrue(Get<bool>(result, "OnlyTrackOnComplete"));
            Assert.IsFalse(Get<bool>(result, "OnlyAlbumOnComplete"));
        }

        [TestMethod]
        public void ParseCommandFlags_AlbumOnlyFlag_SetsOnlyAlbum()
        {
            var result = InvokeParseCommandFlags("a:mycommand");
            Assert.IsTrue(Get<bool>(result, "OnlyAlbumOnComplete"));
        }

        [TestMethod]
        public void ParseCommandFlags_MultipleFlags_AllParsed()
        {
            var result = InvokeParseCommandFlags("s:h:t:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseShellExecute"));
            Assert.IsTrue(Get<bool>(result, "CreateNoWindow"));
            Assert.IsTrue(Get<bool>(result, "OnlyTrackOnComplete"));
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_StateFlag_SetsRequiredJobState()
        {
            var result = InvokeParseCommandFlags("1:mycommand");
            var state = (int?)obj_get(result, "RequiredState");
            Assert.AreEqual(1, state);
            Assert.AreEqual("mycommand", Get<string>(result, "Command"));
        }

        [TestMethod]
        public void ParseCommandFlags_UpdateIndexFlag_SetsUseOutputToUpdateIndex()
        {
            var result = InvokeParseCommandFlags("u:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseOutputToUpdateIndex"));
        }

        [TestMethod]
        public void ParseCommandFlags_ReadOutputFlag_SetsReadOutput()
        {
            var result = InvokeParseCommandFlags("r:mycommand");
            Assert.IsTrue(Get<bool>(result, "ReadOutput"));
        }

        [TestMethod]
        public void ParseCommandFlags_LockFlag_SetsUseLocking()
        {
            var result = InvokeParseCommandFlags("l:mycommand");
            Assert.IsTrue(Get<bool>(result, "UseLocking"));
        }

        // Helper for nullable property
        private static object? obj_get(object obj, string prop) =>
            obj.GetType().GetProperty(prop)!.GetValue(obj);
    }

    [TestClass]
    public class ShouldExecuteCommandTests
    {
        private static bool InvokeShouldExecute(bool onlyTrack, bool onlyAlbum, int? requiredState, JobState currentState, bool isAlbum)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ShouldExecuteCommand", BindingFlags.NonPublic | BindingFlags.Static)!;

            // Build a CommandConfig struct via reflection
            var configType = typeof(OnCompleteExecutor).GetNestedType("CommandConfig", BindingFlags.NonPublic)!;
            var config = Activator.CreateInstance(configType)!;
            configType.GetProperty("OnlyTrackOnComplete")!.SetValue(config, onlyTrack);
            configType.GetProperty("OnlyAlbumOnComplete")!.SetValue(config, onlyAlbum);
            configType.GetProperty("RequiredState")!.SetValue(config, requiredState);

            return (bool)method.Invoke(null, new object[] { config, currentState, isAlbum })!;
        }

        [TestMethod]
        public void ShouldExecute_NoFlags_AlwaysTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, null, JobState.Done, false));
            Assert.IsTrue(InvokeShouldExecute(false, false, null, JobState.Done, true));
        }

        [TestMethod]
        public void ShouldExecute_TrackOnly_OnAlbum_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(true, false, null, JobState.Done, true));
        }

        [TestMethod]
        public void ShouldExecute_TrackOnly_OnTrack_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(true, false, null, JobState.Done, false));
        }

        [TestMethod]
        public void ShouldExecute_AlbumOnly_OnTrack_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, true, null, JobState.Done, false));
        }

        [TestMethod]
        public void ShouldExecute_AlbumOnly_OnAlbum_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, true, null, JobState.Done, true));
        }

        [TestMethod]
        public void ShouldExecute_RequiredState_Matches_ReturnsTrue()
        {
            Assert.IsTrue(InvokeShouldExecute(false, false, (int)JobState.Done, JobState.Done, false));
        }

        [TestMethod]
        public void ShouldExecute_RequiredState_Mismatch_ReturnsFalse()
        {
            Assert.IsFalse(InvokeShouldExecute(false, false, (int)JobState.Done, JobState.Failed, false));
        }
    }

    [TestClass]
    public class ParseFileNameAndArgumentsTests
    {
        private static (string FileName, string Arguments) InvokeParse(string command)
        {
            var method = typeof(OnCompleteExecutor).GetMethod("ParseFileNameAndArguments", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, new object[] { command })!;
            // ValueTuple named fields aren't accessible via dynamic; use positional fields
            var fields = result.GetType().GetFields();
            return ((string)fields[0].GetValue(result)!, (string)fields[2].GetValue(result)!);
        }

        [TestMethod]
        public void ParseFileName_SimpleCommand_SplitsOnFirstSpace()
        {
            var (file, args) = InvokeParse("myprogram arg1 arg2");
            Assert.AreEqual("myprogram", file);
            Assert.AreEqual("arg1 arg2", args);
        }

        [TestMethod]
        public void ParseFileName_QuotedPath_ParsedCorrectly()
        {
            var (file, args) = InvokeParse("\"C:\\Program Files\\tool.exe\" --flag value");
            Assert.AreEqual("C:\\Program Files\\tool.exe", file);
            Assert.AreEqual("--flag value", args);
        }

        [TestMethod]
        public void ParseFileName_NoArgs_ReturnsEmptyArguments()
        {
            var (file, args) = InvokeParse("singlecommand");
            Assert.AreEqual("singlecommand", file);
            Assert.AreEqual("", args);
        }

        [TestMethod]
        public void ParseFileName_EmptyCommand_ReturnsEmpty()
        {
            var (file, args) = InvokeParse("");
            Assert.AreEqual("", file);
            Assert.AreEqual("", args);
        }
    }
}
