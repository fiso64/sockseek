namespace Soulseek.Network.Tcp;

internal static class UnobservedFaultFactory
{
    public static Exception NullReferenceFromConnectionLoop()
    {
        try
        {
            ThrowNullReference();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void ThrowNullReference()
        => throw new NullReferenceException("Object reference not set to an instance of an object.");
}
