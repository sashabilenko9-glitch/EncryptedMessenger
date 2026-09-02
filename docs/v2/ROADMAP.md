# EncryptedMessenger v2 — Roadmap

## Why

v1.0 is a working, end-to-end tested baseline: real RSA/AES handshake, working TCP messaging, peer discovery, persistent (now encrypted-at-rest) history. The point of v2 is not to rebuild it — it's to take it from "demonstrates the required technologies" to "holds up under actual scrutiny": errors are visible instead of silently swallowed, the crypto claims are backed by verifiable properties (key fingerprints, authenticated encryption), and the codebase is testable. That difference is what separates a checklist project from an engineering one, and it pays for itself the moment the project grows any new feature.

## Status check — already solid, don't redo

- P2P over TCP, hybrid RSA-2048 + AES-256 encryption, full async/await, WPF + MVVM, EF Core + SQLite.
- TCP length-prefixed framing is implemented correctly (`PacketHelper.cs`) — no fragmentation bug to fix.
- Message content is encrypted at rest with a persistent local key (fixed post-v1.0.0).

## Priority 1 — do these first

Grounded in actual counts from the codebase, not guesses:

1. **Structured logging.** 36 `Debug.WriteLine` calls across the network layer — all invisible outside an attached debugger, i.e. invisible in the self-contained `.exe` you actually hand out. Replace with file-based logging (`ILogger<T>` + a lightweight provider, e.g. Serilog with daily rotation). Log connection lifecycle, network errors, crypto operations (no sensitive data).
2. **Remove silent `catch { }` blocks.** 12 of them across 6 files (`PipeServer.cs`, `PipeClient.cs`, `MessengerClient.cs`, `MessengerService.cs`, `ChatViewModel.cs`, `AppSettings.cs`). A failed connect or decrypt currently vanishes with zero trace. Introduce specific exception types (`ConnectionFailedException`, `HandshakeException`, `DecryptionException`), handle them deliberately, surface user-facing messages in the UI, add timeouts to every network call that lacks one.
3. **Self-contained / single-file publish config.** Not present in any `.csproj` today. Add `PublishSingleFile`, `SelfContained`, `RuntimeIdentifier=win-x64`, `IncludeNativeLibrariesForSelfExtract` (required for WPF's native DirectX dependencies), `-c Release`. Cheap, and it's the actual deployment story ("copy one .exe to a friend's laptop").
4. **Peer key fingerprint verification.** No protection today against an active MITM substituting its own RSA key during the handshake. Show a SHA-256 fingerprint of the peer's public key so the user can verify it out-of-band (verbally, or later via QR — see ideas below).
5. **AES-GCM instead of AES-CBC.** GCM is authenticated encryption — it detects ciphertext tampering, which plain CBC does not. `AesGcm` is built into .NET; this is a targeted change to `AesCryptoService`/`CryptoManager`, not an architecture rewrite.
6. **Encrypt the RSA private key at rest.** Currently written to disk as a plaintext XML file (`private.key`). Wrap it with Windows DPAPI (`ProtectedData`, `CurrentUser` scope) — a small, high-value change.
7. **Unit tests (xUnit).** Zero tests exist today. Cover: RSA/AES round-trip, packet serialization, `MessageRepository.ConversationId`, and an integration test of a full client↔server handshake + message on localhost. Do this once Priority 2's DI/interfaces land — it's much easier to test with mockable repositories.

## Priority 2 — valuable, not urgent

- **DI + interfaces** (`IMessageRepository`, `IContactRepository`, `ICryptoService`, `IMessengerService` via `Microsoft.Extensions.DependencyInjection`, one `ServiceCollection` shared between WPF and the Windows Service). Mainly valuable as groundwork for testing (P1.7) and for reconnect logic below — not an end in itself.
- **Auto-reconnect with exponential backoff, heartbeat/ping, outbound message retry queue.** Real robustness win for a flaky Wi-Fi LAN, but a meaningful chunk of new state-machine logic. Worth doing, not blocking.
- **Protocol versioning field** (`ProtocolVersion` in `NetworkPacket`, checked on handshake). Nearly free to add — do it while already touching the protocol for GCM/fingerprints (P1.4–5).
- **CI + polish:** `.editorconfig`, GitHub Actions (build + test on push), README architecture diagram, `docs/` protocol description. Cheap, good for a public repo, not functionally blocking.

## Explicitly skipped — and why

- **Migrating to SQLCipher (`sqlite-net-sqlcipher`) instead of EF Core + SQLite.** Would mean replacing the whole DB layer. The actual problem it solves (message content readable on disk) is already fixed with the app-level AES-at-rest key. The remaining gap — contact metadata (IP, display name) still stored in plaintext — is a minor exposure for a LAN-only threat model. Not worth the rewrite.
- **Avatars/`PersonPicture`, Away/DoNotDisturb/Sleeping presence states, custom `ScrollViewer` template, bouncing-dots typing animation, GIF sending (`WpfAnimatedGif`).** Pure UI polish. None of it strengthens correctness, security, or reliability — it's feature-surface, not depth. Worth doing only once Priority 1 is done, and only if visual richness is an actual goal (e.g. for a demo).
- **`TreatWarningsAsErrors` / `Nullable` as hard errors right now.** Good hygiene, bad timing — turning this on before the Priority 1 changes land just produces churn. Turn on once the code has settled.
- **A separate `IntegrityHash` (SHA-256) field on top of the packet.** Redundant once AES-GCM (P1.5) is in place — GCM's authentication tag already gives tamper detection. Doing both duplicates the same guarantee.
- **Perfect Forward Secrecy via ECDH.** Real cryptographic value, but a genuine handshake redesign — bigger and riskier than the fingerprint check it would sit next to. Defer until Priority 1/2 are done.

## Future ideas (not committed — brainstorm backlog)

- **Cross-platform client (MAUI or Avalonia) reusing `EncryptedMessenger.Core` as-is.** The most promising idea here — `Core` has zero WPF dependencies already, so a phone/second-platform client is "add a UI head", not a rewrite.
- **QR-code contact exchange.** Encodes `IP:port:fingerprint` for scanning — turns the manual fingerprint comparison (P1.4) into a one-tap UX and doubles as a security feature.
- **File / image transfer**, streamed through `CryptoStream`, using the existing typed-packet protocol.
- **Full-text search** over message history (SQLite FTS5) — cheap on top of the current EF Core/SQLite setup.
- **Encrypted backup export/import** — reuses the existing at-rest AES key.
- **Group chats.** The hard one: breaks the current "one AES session key per contact" model. Needs either pairwise per-member encryption (simple, doesn't scale) or a real group key with rotation on membership change (correct, non-trivial). Do this last, after Priority 1/2.
- **Optional relay/rendezvous server for off-LAN use.** Interesting (NAT traversal, hole punching), but works against the project's current "serverless" identity — keep as a maybe, not a priority.
