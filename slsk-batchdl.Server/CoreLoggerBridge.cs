using Microsoft.Extensions.Logging;
using Sldl.Core;

namespace Sldl.Server;

public static class CoreLoggerBridge
{
    public static void Configure(IServiceProvider _, LogLevel minimumLevel)
    {
        SldlLog.RemoveNonFileOutputs();
        SldlLog.AddSink(
            (_, message) => Console.WriteLine(message),
            minimumLevel,
            prependDate: true,
            prependLogLevel: true);
    }
}
