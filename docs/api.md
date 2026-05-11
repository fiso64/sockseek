# API and Client integration

The daemon exposes an HTTP API for durable state plus a SignalR hub for live invalidation/progress events.

## Generated OpenAPI document

`slsk-batchdl.Server` generates `docs/openapi.json` during `dotnet build` using the ASP.NET Core OpenAPI build-time generator. Do not edit that file by hand; update the server endpoints, DTOs, or OpenAPI metadata instead, then rebuild. The OpenAPI `info.version` follows the server assembly informational version from `Directory.Build.props`.

The same document is also served by a running daemon at:

```text
GET /api/openapi.json
```

If you are not using .NET, use the OpenAPI document with your viewer or client generator of choice.

## Local mock daemon

For GUI/API development, start from mock files instead of a real Soulseek account:

```bash
python scripts/create_mock_music_library.py -o /tmp/sldl-fixture

dotnet run --project slsk-batchdl.Cli -- daemon \
  --mock-files-dir /tmp/sldl-fixture/mock-library \
  --mock-files-no-read-tags \
  --server-port 5030 \
  -p /tmp/sldl-out
```

## Client model

Use HTTP snapshots as the source of truth and SignalR events as refresh hints.

```text
GET /api/server/status
GET /api/profiles
GET /api/jobs?includeAll=true
GET /api/workflows
GET /api/events/catalog
SignalR /api/events
```

A .NET GUI should prefer `Sldl.Api.SldlApiClient` over hand-written endpoint calls.

## Source map

The API is still in flux. Prefer the generated OpenAPI document and source code over this file for endpoint-level details.

- `slsk-batchdl.Api/Client/SldlApiClient.cs` — .NET client wrapper and the most convenient reference for supported client flows.
- `slsk-batchdl.Api/Contracts/` — request/response DTOs shared by the server, CLI, and .NET clients.
- `slsk-batchdl.Api/Contracts/ServerEvents.cs` — SignalR event envelope and event payload DTOs.
- `slsk-batchdl.Api/Client/ServerEventPayloadConverter.cs` — typed event payload rehydration for .NET clients.
- `slsk-batchdl.Server/ServerHost.cs` — endpoint registration and OpenAPI metadata.
- `slsk-batchdl.Cli/Services/RemoteCliBackend.cs` — real remote client usage, including SignalR subscription behavior.
- `slsk-batchdl.Cli.Tests/RemoteCliBackendTests.cs` — executable examples of remote API flows.
