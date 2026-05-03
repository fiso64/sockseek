using CoreLogger = Sldl.Core.Logger;

namespace Sldl.Server;

public static class CoreLoggerBridge
{
    public static void Configure(IServiceProvider _, CoreLogger.LogLevel minimumLevel)
    {
        CoreLogger.RemoveNonFileOutputs();
        CoreLogger.AddSink(
            (_, message) => Console.WriteLine(message),
            minimumLevel,
            prependDate: true,
            prependLogLevel: true);
    }
}
