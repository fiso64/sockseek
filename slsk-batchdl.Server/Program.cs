using Sldl.Api;
using Sldl.Server;

Sldl.Core.SldlLog.SetupExceptionHandling();
Sldl.Core.SldlLog.AddConsole();

var app = ServerHost.Build(args);
app.Run();
