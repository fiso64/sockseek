using Sldl.Server;

Sldl.Core.Logger.SetupExceptionHandling();
Sldl.Core.Logger.AddConsole();

var app = ServerHost.Build(args);
app.Run();
