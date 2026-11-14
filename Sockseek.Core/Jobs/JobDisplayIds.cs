namespace Sockseek.Core.Jobs;

public static class JobDisplayIds
{
    private static readonly JobDisplayIdAllocator allocator = new();

    public static int Next() => allocator.Next();

    public static void ContinueAfter(long retainedMaximum) => allocator.ContinueAfter(retainedMaximum);
}

internal sealed class JobDisplayIdAllocator
{
    private long next;

    public int Next() => checked((int)Interlocked.Increment(ref next));

    public void ContinueAfter(long retainedMaximum)
    {
        if (retainedMaximum < 0 || retainedMaximum > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(retainedMaximum), "Retained display IDs must fit in Int32 for the current API contract.");

        long observed = Volatile.Read(ref next);
        while (observed < retainedMaximum)
        {
            long prior = Interlocked.CompareExchange(ref next, retainedMaximum, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }
}
