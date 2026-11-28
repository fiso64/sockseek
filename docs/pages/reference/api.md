<!-- Maintainer note: Keep this document concise. Update existing content only when it is inaccurate; avoid adding implementation details or change history. -->

# API and Client integration

> [!WARNING]
> The API is not stable. Breaking changes are common.

<!--
Maintainers: keep this file deliberately small while the API is in flux. It
should provide stable integration entry points, OpenAPI/mock-daemon pointers,
and the few source files a client author should read. Do not duplicate endpoint
inventories, DTO fields, feature semantics, or operational guidance here; use
the generated OpenAPI and source for contracts and the manual for
operator-facing behavior.
-->

The daemon exposes HTTP snapshots and durable history queries plus a SignalR hub
for ordered live-state deltas and compact activity.

## .NET clients

Use [SockseekApiClient](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Api/Client/SockseekApiClient.cs) for HTTP requests and [SockseekLiveClient](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Api/Client/SockseekLiveClient.cs) for live updates.

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

## Client source to read

<!-- Maintainers: list only files that help someone build a client against the
API: the reusable clients, the contracts, and working examples of client flows.
Do not list every file related to the API, such as server internals. -->

- [`Sockseek.Api/Client/SockseekApiClient.cs`](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Api/Client/SockseekApiClient.cs): the .NET client, and the most convenient reference for supported request flows.
- [`Sockseek.Api/Client/SockseekLiveClient.cs`](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Api/Client/SockseekLiveClient.cs): the SignalR subscription, with buffering and recovery.
- [`Sockseek.Api/Client/DaemonClientStore.cs`](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Api/Client/DaemonClientStore.cs): a store that applies snapshots and live deltas, for clients that keep state.
- [`Sockseek.Api/Contracts/`](https://github.com/fiso64/sockseek/tree/v4/Sockseek.Api/Contracts): every request and response type.
- [`Sockseek.Cli/Backends/RemoteCliBackend.cs`](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Cli/Backends/RemoteCliBackend.cs): a real client, including how it subscribes to live updates.
- [`Sockseek.Cli.Tests/RemoteCliBackendTests.cs`](https://github.com/fiso64/sockseek/blob/v4/Sockseek.Cli.Tests/RemoteCliBackendTests.cs): runnable examples of remote API flows.
