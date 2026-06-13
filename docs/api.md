# API and Client integration

The daemon exposes an HTTP API for durable state plus a SignalR hub for live invalidation/progress events.

## .NET clients
.NET consumers should prefer `Sockseek.Api.SockseekApiClient` over hand-written endpoint calls.

## OpenAPI

OpenAPI spec is in `docs/openapi.json` (auto-generated during build). The same document is also served by a running daemon at `GET /api/openapi.json`.

If you are not using .NET, use the OpenAPI document with your viewer or client generator of choice.

## Local mock daemon

For client development, start from mock files instead of a real Soulseek account:

```bash
python scripts/create_mock_music_library.py -o /tmp/sockseek-fixture

dotnet run --project Sockseek.Cli -- daemon \
  --mock-files-dir /tmp/sockseek-fixture/mock-library \
  --mock-files-no-read-tags \
  --mock-files-slow \
  --server-port 5030 \
  -p /tmp/sockseek-out
```

## Source map

The API is still in flux. Prefer the generated OpenAPI document and source code over this file for endpoint-level details.

- `Sockseek.Api/Client/SockseekApiClient.cs` — .NET client wrapper and the most convenient reference for supported client flows.
- `Sockseek.Api/Contracts/` — request/response DTOs shared by the server, CLI, and .NET clients.
- `Sockseek.Api/Contracts/ServerEvents.cs` — SignalR event envelope and event payload DTOs.
- `Sockseek.Api/Client/ServerEventPayloadConverter.cs` — typed event payload rehydration for .NET clients.
- `Sockseek.Server/ServerHost.cs` — endpoint registration and OpenAPI metadata.
- `Sockseek.Cli/Services/RemoteCliBackend.cs` — real remote client usage, including SignalR subscription behavior.
- `Sockseek.Cli.Tests/RemoteCliBackendTests.cs` — executable examples of remote API flows.
