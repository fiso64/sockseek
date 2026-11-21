using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class JobDisplayIdAllocatorTests
{
    [TestMethod]
    public void ContinueAfter_AllocatesUniqueIdsConcurrentlyPastRetainedMaximum()
    {
        var allocator = new JobDisplayIdAllocator();
        allocator.ContinueAfter(10_000);
        var ids = new int[10_000];
        Parallel.For(0, ids.Length, index => ids[index] = allocator.Next());
        Assert.AreEqual(ids.Length, ids.Distinct().Count());
        Assert.AreEqual(10_001, ids.Min());
        Assert.AreEqual(20_000, ids.Max());
    }

    [TestMethod]
    public void Next_ThrowsCheckedOverflow_AfterMaximumRetainedInt()
    {
        var allocator = new JobDisplayIdAllocator();
        allocator.ContinueAfter(int.MaxValue);
        Assert.ThrowsExactly<OverflowException>(() => allocator.Next());
    }

    [TestMethod]
    public void ContinueAfter_RejectsRetainedIdsOutsideCurrentApiRange()
    {
        var allocator = new JobDisplayIdAllocator();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocator.ContinueAfter(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocator.ContinueAfter((long)int.MaxValue + 1));
    }
}
