// TODO [ARCHITECTURE]: Outside Transfers/Downloads, namespaces do not follow
// folders: about 95 files sit in Sockseek.Core.Services, .Models or the root
// namespace. Aligning them needs names that do not shadow the Soulseek library
// (for example .Searching and .Network, not .Search and .Soulseek), touches
// most files in the solution, and changes some log categories. Do it as one
// commit when no other branch is open.
global using Microsoft.Extensions.Logging;

