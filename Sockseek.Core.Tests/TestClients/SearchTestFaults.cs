using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;

namespace Tests.ClientTests;

internal static class SearchTestFaults
{
    // Capture the installed dependency's real cleanup failure deterministically.
    // Keep dependency-private reflection here, not in the behavioral assertions.
    public static SynchronizationLockException CreateDisposalFailure()
    {
        var type = typeof(SoulseekClient).Assembly.GetType("Soulseek.SearchInternal", throwOnError: true)!;
        var search = Activator.CreateInstance(type, SearchQuery.FromText("test"), new SearchScope(SearchScopeType.Network), 1, new SearchOptions())!;
        var gate = (ReaderWriterLockSlim)type.GetProperty("ReaderWriterLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!;
        gate.EnterReadLock();
        try
        {
            return Assert.ThrowsExactly<SynchronizationLockException>(() => ((IDisposable)search).Dispose());
        }
        finally
        {
            gate.ExitReadLock();
            ((IDisposable)search).Dispose();
        }
    }
}
