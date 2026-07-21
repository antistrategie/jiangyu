# Multiplayer Phase 2: Transport and Lobby

Date: 2026-07-20

Status: built and smoke-verified in-game. Phase 2 of the multiplayer framework design
([2026-07-19-multiplayer-framework-design.md](2026-07-19-multiplayer-framework-design.md)):
Steam messages transport, loopback transport, lobby create/join, and the mod-set
handshake, proven by an echo session over the real Steam stack.

## What exists now

### `Jiangyu.Shared.Net` (transport-agnostic, CI-tested)

- `INetTransport`: poll-driven connectionless reliable messaging with per-channel
  ordering. Channels: `control` (0), `commands` (1), `bulk` (2) per the design's
  channel plan (`NetProtocol`).
- `LoopbackTransport`: in-process endpoint pair for tests and the dev self-test.
- `NetWire`: control-message format, one discriminator byte + compact camelCase JSON
  (`Summary`, `Accept`, `Reject`, `Chat`).
- `ModSetSummary` / `ModSetSummaryBuilder`: a peer's replication identity: wire
  protocol version, game build, loader version, and the mod set in load order with a
  SHA-256 over each mod's manifest and compiled template program.
- `HandshakeComparer`: field-by-field comparison rendering player-actionable
  difference lines. Any difference rejects (protocol first and short-circuiting, then
  game build, loader version, per-mod presence/version/hash, and load order, which
  decides conflict winners).
- `NetSession`: the two-peer session state machine
  (Idle → Handshaking → Ready | Rejected | Lost). Symmetric handshake: both sides send
  `Summary` on connect, each compares and answers `Accept` or `Reject` (with the
  diff), ready when both accepted. Chat only flows in Ready. The peer may be the local
  peer itself, which loops a real transport end to end in one process.

Unit coverage in `tests/Jiangyu.Loader.Tests/Net/` (24 tests: transport semantics,
wire round-trips, every comparer verdict, session happy/reject/self/loss paths).

### `Jiangyu.Loader/Net/Steam/` (game-bound, live-smoke only)

Rides the game's own Steam API instance (`Il2Cppcom.rlabrecque.steamworks.net.dll`
interop wrapper, now referenced by the loader and diagnostics csproj): no second
`SteamAPI_Init`, no new native dependency.

- `SteamCallbackSink` + `SteamCallbacks`: a managed subclass of Steamworks.NET's
  abstract `Callback`, injected via `ClassInjector`, registered on the game's own
  `CallbackDispatcher`. One injected class serves every callback struct: it returns
  the target `Il2CppSystem.Type` from `GetCallbackType()` and hands the raw
  `pvParam` pointer to a managed handler. Fallback for a failed native identity
  lookup: direct insertion into `CallbackDispatcher.m_registeredCallbacks` under the
  struct's `k_iCallback`.
- `SteamApiCall<T>`: pending-call polling via `SteamUtils.IsAPICallCompleted` /
  `GetAPICallResult`, replacing `CallResult<T>` (whose generic il2cpp instances only
  exist for the callback types the game itself awaits). Buffer size from
  `il2cpp_class_value_size`; failure detail from `GetAPICallFailureReason`.
- `SteamMessagesTransport`: `INetTransport` over `ISteamNetworkingMessages`
  (`SendMessageToUser` reliable + auto-restart-broken-session,
  `ReceiveMessagesOnChannel` with direct blittable reads of
  `SteamNetworkingMessage_t` and native `Release`). Unsolicited session requests pass
  through a gate wired to lobby membership; the symmetric handshake also accepts
  sessions implicitly (each side's first send accepts the other's pending request).
- `SteamLobby`: create (friends-only) / join / leave, member-list diffing into
  joined/left events each pump, owner lookup, overlay `GameLobbyJoinRequested_t`
  handling.

### Dev harness

`net` bridge command (`Jiangyu.Loader.Diagnostics/Net/NetProbe.cs`), MCP tool
`jiangyu_net`. Ops: `loopback` (synchronous in-process pair: handshake, echo, and a
deliberate mismatch), `selftest` (the same protocol against the local SteamID over the
real Steam transport), `host` / `join` / `leave` / `send` / `echo` / `status`.
SteamID64 and lobby ids travel as decimal strings (they exceed JSON's safe integer
range).

## Interop findings (verified live, game v0.7.10+19931)

- **Injected `Callback` subclasses work end to end.** `CallbackDispatcher.Register`
  resolves the callback id natively from the injected sink's `GetCallbackType()`
  (il2cpp reflection over `CallbackIdentityAttribute` metadata survives for the lobby
  and messages structs), and the game's `SteamManager.Update` pump dispatches into the
  managed `OnRunCallback` override. The direct-insertion fallback was not needed.
- **Callback structs read directly.** The Il2CppInterop wrapper structs are
  explicit-layout mirrors of the native layouts; `*(T*)pvParam` reads are sound for
  `SteamNetworkingMessagesSessionRequest_t`, `GameLobbyJoinRequested_t`,
  `LobbyCreated_t`, `LobbyEnter_t`, and `SteamNetworkingMessage_t`.
- **Instance methods on wrapper structs work on managed copies**
  (`SteamNetworkingIdentity.SetSteamID64` / `GetSteamID64` invoke with a pointer to
  the managed struct as `this`).
- **Steam self-messaging works**: `SendMessageToUser` to the local identity delivers
  through the real messages stack in-process, including a session-request callback
  for one's own pending session. This makes a single-account transport smoke possible.
- **First `CreateLobby` after boot can fail transiently** (api-call io failure ~1
  minute after launch, succeeds on retry once the matchmaking connection is up).
  Callers should treat a failed create as retryable.
- The `unmanaged` constraint and nullability metadata need source-supplied
  `System.Runtime.CompilerServices` attribute shims in game-referencing projects
  (Il2Cppmscorlib shadows the BCL declarations; see `IsUnmanagedAttribute.cs` and
  `NullableAttributeShims.cs`).

## Smoke evidence (2026-07-20, headless rig)

- `net loopback`: matched pair Ready with chat delivered; phantom-mod pair rejected
  both sides with correct per-side diffs (`mod 'LoopbackPhantomMod' 9.9.9: only on
  remote` / `only on local`). Real mod set and game build flowed through the summary
  builder.
- `net selftest`: passed. Full Summary → Accept → Ready handshake and chat echo over
  `ISteamNetworkingMessages` against SteamID 76561198071645257, sessions accepted via
  the injected callback, session closed on completion.
- `net host`: friends-only lobby created (id 109775243802544499), owner and member
  list correct, `leave` tears down cleanly.

## Not yet proven

- Two-account lobby join, cross-machine handshake, and SDR relay traffic: needs a
  second Steam account and stays a manual smoke (as the design's testing strategy
  already assumes).
- The friends-overlay join path (`GameLobbyJoinRequested_t`) is wired but untested.

## CI

`beanpuppy/menace-ci-dependencies` gains `Il2Cppcom.rlabrecque.steamworks.net.dll`
(added to both update scripts' assembly lists, all assemblies re-stripped with
DeepStrip against the current install). The loader + diagnostics build was verified
against the stripped set locally.

## Phase 3 picks up from here

Command replication core: `[NetCommand]` journal, host-ordered stream on the
`commands` channel, barrier logic on `OnFinished` / `OnMovementFinished`, checksums
(promote the determinism probe's state projection into a shared checksum service),
driven over a scripted two-instance session (`nav` gets both processes into a
mission; the loopback and Steam transports both already speak `INetTransport`).
