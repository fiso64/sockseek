using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Sockseek.Server.Tests")]
[assembly: InternalsVisibleTo("Sockseek.Cli.Tests")]

// The browser fixture uses acknowledgement gates rather than timing waits.
[assembly: InternalsVisibleTo("Sockseek.WebUi.TestHost")]
