namespace Sockseek.Core.Events;

internal static class CoreChangeSequencer
{
    private static long nextSequence;

    public static long Next()
        => Interlocked.Increment(ref nextSequence);
}
