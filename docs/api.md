<!-- Maintainer note: Keep this document concise. Update existing content only when it is inaccurate; avoid adding implementation details or change history. -->

# API and Client integration

> [!WARNING]
> The API is experimental and has not yet been tested much. Expect bugs and breaking changes.

<!--
Maintainers: keep this file deliberately small while the API is in flux. It
should provide stable integration entry points, OpenAPI/mock-daemon pointers,
and a source map. Do not duplicate endpoint inventories, DTO fields, feature
semantics, or operational guidance here; use the generated OpenAPI and source
for contracts and docs/daemon.md for operator-facing behavior.
-->

The daemon exposes HTTP snapshots and durable history queries plus a SignalR hub
for ordered live-state deltas and compact activity.

## .NET clients

.NET consumers should use `Sockseek.Api.SockseekApiClient` for HTTP queries and
`Sockseek.Api.SockseekLiveClient` for live monitoring. The live client supports
daemon-wide and resource-scoped subscriptions, handles snapshot hydration and
recovery, and exposes current state through `DaemonClientStore`. Create the shared
HTTP client with `SockseekApiClient.CreateHttpClient(serverUrl, apiKey)` and pass it
to both clients. All API/hub access requires authentication by default; see
[credentials and deployment](daemon.md#network-security). Browser clients use
`/api/auth/session`, `/api/auth/login` and `/api/auth/logout`, with cookies and the
bootstrap `X-CSRF-Token` for mutations. Refresh the token after login/logout.

For a UI, use `includeTransferDetails: false` and observe the fetched transfer
timeline or displayed job IDs separately. Global transfer totals remain available.
Scoped observations end on cancellation or disconnect; reopen them after reconnect.

## OpenAPI

OpenAPI spec is in `docs/openapi.json` (auto-generated during build). The same document is also served by a running daemon at `GET /api/openapi.json`.

If you are not using .NET, use the OpenAPI document with your viewer or client generator of choice.

## Local mock daemon

For client development, start from mock files instead of a real Soulseek account:

```bash
python scripts/create_mock_music_library.py -o /tmp/sockseek-fixture

sockseek daemon --web-password-file /path/to/test-password --allow-http-auth \
  --mock-files-dir /tmp/sockseek-fixture/mock-library \
  --mock-files-no-read-tags \
  --mock-files-slow \
  --server-port 5030 \
  -o /tmp/sockseek-out
```

## Source map

- `Sockseek.Api/Client/SockseekApiClient.cs` — .NET client wrapper and the most convenient reference for supported client flows.
- `Sockseek.Api/Client/SockseekLiveClient.cs` — reusable SignalR subscription,
  buffering, and recovery coordinator.
- `Sockseek.Api/Client/DaemonClientStore.cs` — shared reducer and query store.
- `Sockseek.Api/Contracts/` — request/response DTOs shared by the server, CLI, and .NET clients.
- `Sockseek.Api/Contracts/Chats.cs` — chat, room, and notification contracts.
- `Sockseek.Api/Contracts/LiveState.cs` — versioned snapshot, typed delta,
  transfer, stream-position, and compact activity DTOs.
- `Sockseek.Server/ServerHost.cs` — endpoint registration and OpenAPI metadata.
- `Sockseek.Cli/Services/RemoteCliBackend.cs` — real remote client usage, including SignalR subscription behavior.
- `Sockseek.Cli/ChatCommandRunner.cs` — scriptable remote chat client usage.
- `Sockseek.Cli.Tests/RemoteCliBackendTests.cs` — executable examples of remote API flows.
- `Sockseek.Server.Tests/ChatApiTests.cs` — executable chat API client flows.
