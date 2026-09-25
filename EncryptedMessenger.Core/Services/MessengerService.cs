using EncryptedMessenger.Core.Database;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncryptedMessenger.Core.Services
{
    /// <summary>
    /// Orchestrates all subsystems: encryption, TCP server, TCP clients,
    /// UDP peer discovery, SQLite persistence and Named-Pipe IPC with the UI.
    /// </summary>
    public sealed class MessengerService : IAsyncDisposable
    {
        private readonly AppSettings _settings;
        private readonly CryptoManager _crypto;
        private readonly AppDbContext _db;
        private readonly MessageRepository _msgRepo;
        private readonly ContactRepository _contactRepo;
        private readonly MessengerServer _server;
        private readonly PeerDiscovery _discovery;
        private readonly PipeServer _pipeServer;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        private readonly Dictionary<string, MessengerClient> _clients = [];
        private readonly SemaphoreSlim _clientsLock = new(1, 1);

        // A peer counts as online for three missed discovery rounds after its last announcement.
        private readonly PresenceTracker _presence =
            new(TimeSpan.FromSeconds(PeerDiscovery.BroadcastIntervalSeconds * 3));

        // Discovery re-announces every few seconds; LastSeen is only persisted this often.
        private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

        private readonly CancellationTokenSource _cts = new();

        public MessengerService(AppSettings? settings = null, ILoggerFactory? loggerFactory = null)
        {
            _settings = settings ?? AppSettings.Load();
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<MessengerService>();

            _crypto = new CryptoManager(AppSettings.PrivateKeyFile, AppSettings.StorageKeyFile);
            _db = new AppDbContext(AppSettings.DatabaseFileName);
            _msgRepo = new MessageRepository(_db);
            _contactRepo = new ContactRepository(_db);

            _server = new MessengerServer(
                _settings.TcpPort, _settings.UserId, _settings.DisplayName, _crypto,
                _loggerFactory.CreateLogger<MessengerServer>(), VerifyPeerKeyAsync, AcceptsMessagesFromAsync);

            _discovery = new PeerDiscovery(
                _settings.UdpPort, _settings.UserId, _settings.DisplayName, _settings.TcpPort,
                _loggerFactory.CreateLogger<PeerDiscovery>());

            _pipeServer = new PipeServer(_loggerFactory.CreateLogger<PipeServer>());

            _server.MessageReceived += OnMessageReceived;
            _server.ContactConnected += OnContactConnected;
            _server.ContactDisconnected += OnContactDisconnected;
            _server.DeliveryAcknowledged += OnDeliveryAcknowledged;
            _server.ContactControlReceived += OnContactControlReceived;
            _discovery.PeerDiscovered += OnPeerDiscovered;
            _pipeServer.MessageReceived += OnPipeMessageReceived;
        }

        public async Task StartAsync()
        {
            _db.EnsureCreated();
            _ = Task.Run(_server.StartAsync);
            if (_settings.Discovery) _ = Task.Run(_discovery.StartAsync);
            _ = Task.Run(_pipeServer.StartAsync);
            _ = Task.Run(() => PresenceSweepLoopAsync(_cts.Token));
        }

        // ── Presence ─────────────────────────────────────────────────────

        /// <summary>
        /// Periodically re-evaluates every tracked contact so that one whose discovery
        /// announcements stopped (and has no live connection) is reported offline.
        /// </summary>
        private async Task PresenceSweepLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PeerDiscovery.BroadcastIntervalSeconds), ct);
                    foreach (var id in _presence.KnownContacts())
                        await PublishPresenceAsync(id);
                    if (ExpireNearby()) await BroadcastNearbyAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Presence sweep failed"); }
            }
        }

        /// <summary>Tells the UI about <paramref name="contactId"/>'s presence, but only if it changed.</summary>
        private async Task PublishPresenceAsync(string contactId)
        {
            if (!_presence.TryChangeReported(contactId, out var online)) return;
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                online ? PipeMessageType.ContactOnline : PipeMessageType.ContactOffline,
                new ContactStatusPayload(contactId, online)));

            // Retry trigger: contact came (back) online. Not awaited: PublishPresenceAsync is
            // also called while _clientsLock is held (GetOrCreateClientAsync), and the flush
            // may need that lock — awaiting it here would deadlock.
            if (online) RunHandler(nameof(DeliverOwedAsync), () => DeliverOwedAsync(contactId));
        }

        /// <summary>
        /// Contact just came online. Flush what we owe over an existing connection — and if we
        /// have an unanswered request to them, actively connect: otherwise a request made while
        /// they were offline would wait until THEY happen to connect to us. Only runs on an
        /// offline→online transition, so an unreachable peer isn't dialled every 5 seconds.
        /// </summary>
        private async Task DeliverOwedAsync(string contactId)
        {
            var contact = await _contactRepo.GetByIdAsync(contactId);
            if (contact?.State == ContactState.OutgoingRequest)
                await TryConnectAsync(contact);   // a new connection triggers FlushOwedAsync itself
            else
                await FlushOwedAsync(contactId);
        }

        /// <summary>Opens (or reuses) an outbound connection without telling the UI; failures are only logged.</summary>
        private async Task<MessengerClient?> TryConnectAsync(Contact contact)
        {
            if (string.IsNullOrEmpty(contact.IpAddress)) return null;
            await _clientsLock.WaitAsync();
            try
            {
                return await GetOrCreateClientAsync(contact.Id, contact.IpAddress, contact.Port);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Background connect to {ContactId} failed", contact.Id);
                return null;
            }
            finally { _clientsLock.Release(); }
        }

        // ── Owed control packets (read receipts, contact requests) ───────

        /// <summary>
        /// Delivers a control packet over whichever connection to <paramref name="contactId"/>
        /// exists: the inbound one they opened to us, or our outbound one to them. Never opens a
        /// new connection by itself.
        /// </summary>
        private async Task<bool> TrySendControlAsync(string contactId, PacketType type, string? messageId = null, string payload = "")
        {
            if (await _server.SendControlAsync(contactId, type, messageId, payload)) return true;

            await _clientsLock.WaitAsync();
            try
            {
                return _clients.TryGetValue(contactId, out var client)
                       && await client.SendControlAsync(type, messageId, payload);
            }
            finally { _clientsLock.Release(); }
        }

        // Contacts whose owed packets are being sent right now. Several triggers
        // (inbound connect, outbound connect, going online) often fire together; this
        // keeps them from sending the same packets in parallel.
        private readonly HashSet<string> _flushing = [];

        /// <summary>
        /// Sends everything still owed to <paramref name="contactId"/>, all of it recorded in the
        /// database so it survives a restart:
        ///  • our contact request, while the contact is OutgoingRequest (re-sent on every new
        ///    connection until they answer — a lost Accept is healed because a repeated request
        ///    to someone who already accepted us is answered with Accept again);
        ///  • read receipts (ReadAckPending), oldest first, stopping at the first failure.
        /// </summary>
        private async Task FlushOwedAsync(string contactId)
        {
            lock (_flushing)
                if (!_flushing.Add(contactId)) return;

            try
            {
                var contact = await _contactRepo.GetByIdAsync(contactId);
                if (contact?.State == ContactState.OutgoingRequest)
                    await TrySendControlAsync(contactId, PacketType.ContactRequest, payload: _settings.DisplayName);

                foreach (var m in await _msgRepo.GetPendingReadAcksAsync(contactId))
                {
                    if (!await TrySendControlAsync(contactId, PacketType.ReadAck, m.MessageId)) break;
                    await _msgRepo.UpdateStatusAsync(m.MessageId, MessageStatus.Read);
                }
            }
            finally
            {
                lock (_flushing) _flushing.Remove(contactId);
            }
        }

        /// <summary>
        /// Sends the full contact list with the current online flags filled in, so a
        /// list refresh in the UI doesn't reset every contact to offline.
        /// </summary>
        private async Task BroadcastContactListAsync()
        {
            var contacts = await _contactRepo.GetAcceptedAsync();
            foreach (var c in contacts)
            {
                c.IsOnline = _presence.IsOnline(c.Id);
                _presence.SetReported(c.Id, c.IsOnline);
            }
            await _pipeServer.BroadcastAsync(PipeMessage.Create(PipeMessageType.ContactList, contacts));
        }

        // ── Outbound ─────────────────────────────────────────────────────

        /// <param name="messageId">
        /// Id chosen by the UI for its optimistic bubble. Reusing it end-to-end is what lets
        /// delivery/read acks find that bubble again. A new id is generated when omitted.
        /// </param>
        public async Task<Message?> SendMessageAsync(string recipientId, string plainText, string? messageId = null)
        {
            var contact = await _contactRepo.GetByIdAsync(recipientId);
            if (contact == null) return null;
            if (contact.State != ContactState.Accepted)
            {
                _logger.LogWarning("Not sending to {RecipientId}: not an accepted contact ({State})", recipientId, contact.State);
                return null;
            }

            await _clientsLock.WaitAsync();
            try
            {
                var client = await GetOrCreateClientAsync(recipientId, contact.IpAddress, contact.Port);

                // After a merge the live client may be keyed under the real id.
                var targetId = client.ContactId is { Length: > 0 } realId && realId != recipientId
                               ? realId : recipientId;

                var msgId = string.IsNullOrEmpty(messageId) ? Guid.NewGuid().ToString() : messageId;
                await client.SendMessageAsync(plainText, msgId);

                var message = new Message
                {
                    ConversationId = MessageRepository.ConversationId(_settings.UserId, targetId),
                    SenderId = _settings.UserId,
                    RecipientId = targetId,
                    EncryptedContent = _crypto.EncryptForStorage(plainText),
                    DecryptedContent = plainText,
                    MessageId = msgId,
                    IsOutgoing = true,
                    Status = MessageStatus.Sent
                };
                await _msgRepo.SaveAsync(message);
                await _contactRepo.UpdateLastSeenAsync(targetId);
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessage failed for recipient {RecipientId}", recipientId);
                if (ex is UntrustedPeerKeyException untrusted)
                    await AnnounceRejectedKeyViaAsync(untrusted.ContactId, recipientId);
                return null;
            }
            finally { _clientsLock.Release(); }
        }

        public async Task<bool> ConnectToContactAsync(string recipientId)
        {
            var contact = await _contactRepo.GetByIdAsync(recipientId);
            if (contact == null) return false;

            await _clientsLock.WaitAsync();
            try
            {
                var client = await GetOrCreateClientAsync(recipientId, contact.IpAddress, contact.Port);
                var id = client.ContactId is { Length: > 0 } realId ? realId : recipientId;
                // Always answer an explicit connect request (even without a presence change):
                // the open chat shows "Verbinde…" until it gets a status for this contact.
                var online = _presence.IsOnline(id);
                _presence.SetReported(id, online);
                await _pipeServer.BroadcastAsync(PipeMessage.Create(
                    online ? PipeMessageType.ContactOnline : PipeMessageType.ContactOffline,
                    new ContactStatusPayload(id, online)));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connect failed for contact {RecipientId}", recipientId);
                if (ex is UntrustedPeerKeyException untrusted)
                    await AnnounceRejectedKeyViaAsync(untrusted.ContactId, recipientId);
                _presence.SetReported(recipientId, false);
                await _pipeServer.BroadcastAsync(PipeMessage.Create(
                    PipeMessageType.ContactOffline, new ContactStatusPayload(recipientId, false)));
                return false;
            }
            finally { _clientsLock.Release(); }
        }

        /// <summary>
        /// Returns a live outbound client, connecting and wiring events on first use.
        /// After the handshake, relinks a manual contact to the peer's real UserId.
        /// MUST be called while holding _clientsLock.
        /// </summary>
        private async Task<MessengerClient> GetOrCreateClientAsync(string recipientId, string ip, int port)
        {
            if (_clients.TryGetValue(recipientId, out var existing) && existing.IsConnected)
                return existing;

            var client = new MessengerClient(_settings.UserId, _crypto, _loggerFactory.CreateLogger<MessengerClient>(), VerifyPeerKeyAsync);

            // Which contact this connection counts towards in the presence tracker. Set only
            // once the handshake is done (and, for a manual contact, merged to the real id).
            // Guarded by `link` so a disconnect racing the end of the handshake can't leave
            // the connection counted forever.
            var link = new object();
            string? trackedId = null;
            var closed = false;

            client.MessageReceived += OnMessageReceived;
            client.DeliveryAcknowledged += OnDeliveryAcknowledged;
            client.ContactControlReceived += OnContactControlReceived;
            client.Disconnected += async (_, _) =>
            {
                string? id;
                lock (link)
                {
                    closed = true;
                    id = trackedId;
                    if (id != null) _presence.ConnectionClosed(id);
                }
                if (id == null) return;

                try
                {
                    await _clientsLock.WaitAsync();
                    try
                    {
                        // A newer client for the same contact may already be registered.
                        if (_clients.TryGetValue(id, out var current) && current == client)
                            _clients.Remove(id);
                    }
                    finally { _clientsLock.Release(); }

                    // Uses the real id, not the manual_ip_port id the connection started with.
                    await PublishPresenceAsync(id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to handle disconnect for {ContactId}", id);
                }
            };

            try
            {
                await client.ConnectAsync(ip, port);
            }
            catch
            {
                client.Dispose(); // don't leak the socket of a failed connect/handshake
                throw;
            }

            // After the handshake we know the peer's REAL UserId (client.ContactId).
            // If we connected via a manual "manual_ip_port" contact, relink it.
            var realId = client.ContactId;
            var finalId = recipientId;
            if (!string.IsNullOrEmpty(realId) && realId != recipientId)
            {
                finalId = realId;
                await _contactRepo.MergeManualContactAsync(recipientId, realId);

                await _pipeServer.BroadcastAsync(PipeMessage.Create(
                    PipeMessageType.ContactIdChanged,
                    new ContactIdChangedPayload(recipientId, realId)));

                // All lists: the merged row may be a contact OR a pending request (adding by IP
                // is a request), so the requests list must drop the manual_ip_port entry too.
                await BroadcastRelationshipsAsync();
            }

            await AnnounceTrustedKeyAsync(finalId, client.PeerPublicKeyXml);

            _clients[finalId] = client;

            lock (link)
            {
                if (!closed)
                {
                    trackedId = finalId;
                    _presence.ConnectionOpened(finalId);
                }
            }
            await PublishPresenceAsync(finalId);

            // Retry trigger: we connected to them. Deliberately NOT awaited: we still hold
            // _clientsLock here and the flush needs it for the outbound channel — awaiting
            // would deadlock (SemaphoreSlim isn't re-entrant). RunHandler lets it run once
            // our caller releases the lock.
            RunHandler(nameof(FlushOwedAsync), () => FlushOwedAsync(finalId));
            return client;
        }

        // ── Key pinning ──────────────────────────────────────────────────

        /// <summary>
        /// Keys presented by peers that did NOT match the pinned key, per contact — held
        /// until the user accepts one (AcceptPeerKey) after comparing its fingerprint.
        /// In memory only: after a restart the next attempt by the peer re-populates it.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rejectedKeys = new();

        /// <summary>
        /// <see cref="PeerKeyVerifier"/> handed to the TCP server and clients; runs inside
        /// every handshake before a session key is exchanged.
        /// First contact: pins the key (trust on first use). Known contact: the key must be
        /// identical to the pinned one, otherwise the handshake is aborted and the UI is told
        /// the new key's fingerprint so the user can decide.
        /// </summary>
        private async Task<bool> VerifyPeerKeyAsync(string contactId, string publicKeyXml)
        {
            try
            {
                if (string.IsNullOrEmpty(publicKeyXml)) return false;
                if (await _contactRepo.PinOrVerifyKeyAsync(contactId, publicKeyXml)) return true;

                _rejectedKeys[contactId] = publicKeyXml;
                var fingerprint = RsaCryptoService.ComputeFingerprint(publicKeyXml);
                _logger.LogWarning(
                    "SECURITY: contact {ContactId} presented a different public key than the pinned one " +
                    "(reinstall, or a man-in-the-middle). Connection refused. New fingerprint: {Fingerprint}",
                    contactId, fingerprint);

                await _pipeServer.BroadcastAsync(PipeMessage.Create(
                    PipeMessageType.KeyFingerprint,
                    new KeyFingerprintPayload(contactId, fingerprint, Changed: true)));
                return false;
            }
            catch (Exception ex)
            {
                // Fail closed: if we can't check the key (DB error…), we don't trust it.
                _logger.LogError(ex, "Key verification failed for {ContactId}; refusing connection", contactId);
                return false;
            }
        }

        /// <summary>
        /// A connection opened for local contact <paramref name="viaContactId"/> (e.g. a manual
        /// "manual_ip_port" contact) was answered by <paramref name="peerId"/> with an untrusted
        /// key. VerifyPeerKeyAsync already warned under peerId; repeat it tagged with the chat's
        /// id, otherwise the UI — which only knows the manual id — would never show it.
        /// </summary>
        private async Task AnnounceRejectedKeyViaAsync(string peerId, string viaContactId)
        {
            if (peerId == viaContactId || !_rejectedKeys.TryGetValue(peerId, out var key)) return;
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.KeyFingerprint,
                new KeyFingerprintPayload(peerId, RsaCryptoService.ComputeFingerprint(key), Changed: true, ViaContactId: viaContactId)));
        }

        /// <summary>Tells the UI the (pinned, verified) fingerprint of a contact after a successful handshake.</summary>
        private async Task AnnounceTrustedKeyAsync(string contactId, string publicKeyXml)
        {
            if (string.IsNullOrEmpty(publicKeyXml)) return;
            _rejectedKeys.TryRemove(contactId, out _);   // the real key just worked; drop any stale candidate
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.KeyFingerprint,
                new KeyFingerprintPayload(contactId, RsaCryptoService.ComputeFingerprint(publicKeyXml), Changed: false)));
        }

        /// <summary>
        /// The user compared <paramref name="fingerprint"/> with the contact out-of-band and
        /// trusts it: re-pin to the rejected key — but only if that key still has exactly this
        /// fingerprint — then reconnect.
        /// </summary>
        private async Task AcceptPeerKeyAsync(string contactId, string fingerprint, string? reconnectContactId)
        {
            if (!_rejectedKeys.TryGetValue(contactId, out var candidate)
                || RsaCryptoService.ComputeFingerprint(candidate) != fingerprint)
            {
                _logger.LogWarning("AcceptPeerKey for {ContactId} ignored: no pending key with fingerprint {Fingerprint}",
                    contactId, fingerprint);
                return;
            }

            await _contactRepo.SetPublicKeyXmlAsync(contactId, candidate);
            _rejectedKeys.TryRemove(contactId, out _);
            _logger.LogWarning("SECURITY: user accepted new public key for {ContactId}, fingerprint {Fingerprint}",
                contactId, fingerprint);

            await ConnectToContactAsync(reconnectContactId ?? contactId);
        }

        // ── Event handlers ────────────────────────────────────────────────
        //
        // These are async void (event handlers), so an exception escaping them cannot be
        // observed by anyone and would terminate the whole process — in standalone mode
        // that includes the UI. Each one therefore delegates to a Task-returning method
        // through RunHandler, which logs instead of crashing.

        private async void RunHandler(string name, Func<Task> handler)
        {
            try { await handler(); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled error in {Handler}", name); }
        }

        private void OnMessageReceived(object? _, MessageReceivedEventArgs e)
            => RunHandler(nameof(OnMessageReceived), () => HandleMessageReceivedAsync(e));

        private void OnContactConnected(object? _, ContactStatusEventArgs e)
        {
            // Count the connection synchronously so a quick disconnect can't be processed first.
            _presence.ConnectionOpened(e.ContactId);
            RunHandler(nameof(OnContactConnected), () => HandleContactConnectedAsync(e));
        }

        private void OnContactDisconnected(object? _, ContactStatusEventArgs e)
        {
            _presence.ConnectionClosed(e.ContactId);
            RunHandler(nameof(OnContactDisconnected), () => PublishPresenceAsync(e.ContactId));
        }

        private void OnDeliveryAcknowledged(object? _, DeliveryAckEventArgs e)
            => RunHandler(nameof(OnDeliveryAcknowledged), () => HandleDeliveryAcknowledgedAsync(e));

        private void OnContactControlReceived(object? _, ContactControlEventArgs e)
            => RunHandler($"{nameof(OnContactControlReceived)}({e.Type})", () => HandleContactControlAsync(e));

        private void OnPeerDiscovered(object? _, PeerDiscoveredEventArgs e)
            => RunHandler(nameof(OnPeerDiscovered), () => HandlePeerDiscoveredAsync(e));

        private void OnPipeMessageReceived(object? _, PipeMessage msg)
            => RunHandler($"{nameof(OnPipeMessageReceived)}({msg.Type})", () => HandlePipeMessageAsync(msg));

        private async Task HandleMessageReceivedAsync(MessageReceivedEventArgs e)
        {
            // Ensure the sender exists as a contact (keyed by their real UserId),
            // so the receiver sees the conversation and history matches.
            // The server already refuses messages from non-contacts before acking them
            // (AcceptsMessagesFromAsync). Checking again here — the only place messages are
            // stored — keeps that guarantee even for a path that forgets to check (e.g. a
            // Message arriving over an outbound connection).
            if (!await AcceptsMessagesFromAsync(e.SenderId))
            {
                _logger.LogWarning("Dropped message {MessageId} from non-contact {ContactId}", e.MessageId, e.SenderId);
                return;
            }

            var msg = new Message
            {
                ConversationId = MessageRepository.ConversationId(_settings.UserId, e.SenderId),
                SenderId = e.SenderId,
                RecipientId = _settings.UserId,
                EncryptedContent = _crypto.EncryptForStorage(e.PlainText),
                DecryptedContent = e.PlainText,
                MessageId = e.MessageId,
                Timestamp = e.Timestamp,
                IsOutgoing = false,
                Status = MessageStatus.Delivered
            };
            await _msgRepo.SaveAsync(msg);
            await _contactRepo.UpdateLastSeenAsync(e.SenderId);

            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.NewIncomingMessage,
                new NewMessagePayload(e.SenderId, e.PlainText, e.MessageId, e.Timestamp)));
        }

        private async Task HandleContactConnectedAsync(ContactStatusEventArgs e)
        {
            // Remember where an unknown peer connected from, so we can reply even with
            // discovery off. Their listening port is not part of the handshake, so the
            // default port is assumed; a later discovery announcement corrects it.
            var listChanged = e.IpAddress != null
                && await _contactRepo.EnsureWithAddressAsync(e.ContactId, e.IpAddress, AppSettings.DefaultTcpPort);

            await _contactRepo.UpdateLastSeenAsync(e.ContactId);
            if (e.PublicKeyXml != null)
                await AnnounceTrustedKeyAsync(e.ContactId, e.PublicKeyXml);

            if (listChanged) await BroadcastContactListAsync();
            await PublishPresenceAsync(e.ContactId);

            // Retry trigger: they connected to us — a channel for owed receipts now exists.
            await FlushOwedAsync(e.ContactId);
        }

        private async Task HandleDeliveryAcknowledgedAsync(DeliveryAckEventArgs e)
        {
            // DeliveryAck and ReadAck travel over the same TCP stream in that order,
            // so a later Delivered can never overwrite an earlier Read here.
            await _msgRepo.UpdateStatusAsync(e.MessageId, e.IsRead ? MessageStatus.Read : MessageStatus.Delivered);
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                e.IsRead ? PipeMessageType.MessageRead : PipeMessageType.DeliveryAck, e.MessageId));
        }

        private async Task HandlePeerDiscoveredAsync(PeerDiscoveredEventArgs e)
        {
            _presence.Heard(e.PeerId);

            var announced = new Contact
            {
                Id = e.PeerId,
                DisplayName = e.DisplayName,
                IpAddress = e.IpAddress,
                Port = e.Port,
                LastSeen = DateTime.UtcNow
            };
            var (state, changed) = await _contactRepo.ApplyDiscoveryAsync(announced, LastSeenWriteInterval);

            switch (state)
            {
                case ContactState.Accepted:
                    if (changed) await BroadcastContactListAsync();
                    await PublishPresenceAsync(e.PeerId);
                    break;

                case ContactState.OutgoingRequest:
                case ContactState.IncomingRequest:
                    // Shown under "requests", not "nearby". Presence still matters: going online
                    // is what triggers re-sending our request (DeliverOwedAsync).
                    if (changed) await BroadcastRequestsAsync();
                    await PublishPresenceAsync(e.PeerId);
                    break;

                default:
                    // Not a contact (unknown, or only known by a pinned key): offer it in "nearby".
                    if (UpdateNearby(new NearbyPeerPayload(e.PeerId, e.DisplayName, e.IpAddress, e.Port)))
                        await BroadcastNearbyAsync();
                    break;
            }
        }

        // ── Contact requests ─────────────────────────────────────────────

        private async Task BroadcastRequestsAsync()
        {
            var requests = (await _contactRepo.GetRequestsAsync())
                .Select(c => new ContactRequestPayload(c.Id, c.DisplayName, c.IpAddress,
                                                       Incoming: c.State == ContactState.IncomingRequest))
                .ToList();
            await _pipeServer.BroadcastAsync(PipeMessage.Create(PipeMessageType.RequestList, requests));
        }

        /// <summary>Everything the UI shows about relationships changed: contacts, requests, nearby.</summary>
        private async Task BroadcastRelationshipsAsync()
        {
            await BroadcastContactListAsync();
            await BroadcastRequestsAsync();
            await BroadcastNearbyAsync();
        }

        /// <summary>
        /// The user asks <paramref name="target"/> to become a contact (from "nearby" or by IP).
        /// Records the request first, then tries to deliver it right away over a new or existing
        /// connection; if that fails it stays OutgoingRequest and FlushOwedAsync retries later.
        /// </summary>
        private async Task SendContactRequestAsync(Contact target)
        {
            var state = await _contactRepo.MarkRequestSentAsync(target);
            RemoveNearby(target.Id);
            await BroadcastRelationshipsAsync();
            _logger.LogInformation("Contact request to {ContactId}: state {State}", target.Id, state);

            if (state == ContactState.Accepted)
            {
                // They had already asked us — our request is the answer.
                await TryConnectAsync(target);
                await TrySendControlAsync(target.Id, PacketType.ContactAccept);
                await PublishPresenceAsync(target.Id);
                return;
            }

            // Connecting triggers FlushOwedAsync, which sends the request. For a manual
            // "manual_ip_port" target the handshake also merges it into the peer's real id
            // (which inherits OutgoingRequest), and the flush then runs for that id.
            var client = await TryConnectAsync(target);
            if (client != null && client.ContactId == target.Id)
                await FlushOwedAsync(target.Id);   // already-open connection: no new-connection trigger
        }

        /// <summary>A ContactRequest/Accept/Decline/NotAContact packet arrived over a key-pinned connection.</summary>
        private async Task HandleContactControlAsync(ContactControlEventArgs e)
        {
            switch (e.Type)
            {
                case PacketType.ContactRequest:
                    var (old, now) = await _contactRepo.MarkRequestReceivedAsync(e.ContactId, e.Payload);
                    _logger.LogInformation("Contact request from {ContactId}: {Old} -> {New}", e.ContactId, old, now);
                    // Already contacts (their earlier Accept from us got lost, or a mutual request):
                    // answer Accept again. Repeating it is harmless — that's what makes retries safe.
                    if (now == ContactState.Accepted)
                        await TrySendControlAsync(e.ContactId, PacketType.ContactAccept);
                    if (old != now)
                    {
                        RemoveNearby(e.ContactId);
                        await BroadcastRelationshipsAsync();
                        if (now == ContactState.Accepted) await PublishPresenceAsync(e.ContactId);
                    }
                    break;

                case PacketType.ContactAccept:
                    // Only honoured if WE asked — nobody can add themselves to our contacts.
                    if (await _contactRepo.TransitionAsync(e.ContactId, ContactState.OutgoingRequest, ContactState.Accepted))
                    {
                        _logger.LogInformation("{ContactId} accepted our contact request", e.ContactId);
                        await BroadcastRelationshipsAsync();
                        await PublishPresenceAsync(e.ContactId);
                    }
                    break;

                case PacketType.ContactDecline:
                    if (await _contactRepo.TransitionAsync(e.ContactId, ContactState.OutgoingRequest, ContactState.Stranger))
                    {
                        _logger.LogInformation("{ContactId} declined our contact request", e.ContactId);
                        await BroadcastRelationshipsAsync();
                    }
                    break;

                case PacketType.NotAContact:
                    // We think we're contacts, they don't (their data was reset, or an old one-sided
                    // contact). The message was dropped on their side: mark it failed, and turn the
                    // relationship back into a request so they get asked instead of silently ignored.
                    if (e.MessageId != null)
                    {
                        await _msgRepo.UpdateStatusAsync(e.MessageId, MessageStatus.Failed);
                        await _pipeServer.BroadcastAsync(PipeMessage.Create(PipeMessageType.SendFailed, e.MessageId));
                    }
                    if (await _contactRepo.TransitionAsync(e.ContactId, ContactState.Accepted, ContactState.OutgoingRequest))
                    {
                        _logger.LogWarning("{ContactId} doesn't have us as a contact; re-sending a contact request", e.ContactId);
                        await BroadcastRelationshipsAsync();
                        await TrySendControlAsync(e.ContactId, PacketType.ContactRequest, payload: _settings.DisplayName);
                    }
                    break;
            }
        }

        /// <summary>The user answered an incoming request.</summary>
        private async Task AnswerContactRequestAsync(string contactId, bool accept)
        {
            var to = accept ? ContactState.Accepted : ContactState.Stranger;
            if (!await _contactRepo.TransitionAsync(contactId, ContactState.IncomingRequest, to)) return;
            await BroadcastRelationshipsAsync();

            // Best effort. If the answer doesn't arrive, the requester is still OutgoingRequest
            // and re-sends; a repeated request to an Accepted contact is answered with Accept.
            // (A lost Decline just means they may ask again.)
            var type = accept ? PacketType.ContactAccept : PacketType.ContactDecline;
            if (!await TrySendControlAsync(contactId, type) && accept)
            {
                var contact = await _contactRepo.GetByIdAsync(contactId);
                if (contact != null && await TryConnectAsync(contact) != null)
                    await TrySendControlAsync(contactId, type);
            }
            if (accept) await PublishPresenceAsync(contactId);
        }

        // ── Nearby (discovered peers that aren't contacts) ───────────────

        // In memory only: it describes who is announcing right now, not a relationship.
        private readonly Dictionary<string, (NearbyPeerPayload Peer, DateTime LastHeard)> _nearby = [];

        /// <summary>Records an announcement; returns true if the list visibly changed (new peer, new name/address).</summary>
        private bool UpdateNearby(NearbyPeerPayload peer)
        {
            lock (_nearby)
            {
                var visibleChange = !_nearby.TryGetValue(peer.PeerId, out var old) || old.Peer != peer;
                _nearby[peer.PeerId] = (peer, DateTime.UtcNow);
                return visibleChange;
            }
        }

        /// <summary>Drops peers not heard within the presence timeout; returns true if any were removed.</summary>
        private bool ExpireNearby()
        {
            var cutoff = DateTime.UtcNow - _presence.DiscoveryTimeout;
            lock (_nearby)
            {
                var stale = _nearby.Where(kv => kv.Value.LastHeard < cutoff).Select(kv => kv.Key).ToList();
                foreach (var id in stale) _nearby.Remove(id);
                return stale.Count > 0;
            }
        }

        private bool RemoveNearby(string peerId)
        {
            lock (_nearby) return _nearby.Remove(peerId);
        }

        private Task BroadcastNearbyAsync()
        {
            List<NearbyPeerPayload> list;
            lock (_nearby) list = [.. _nearby.Values.Select(v => v.Peer).OrderBy(p => p.DisplayName)];
            return _pipeServer.BroadcastAsync(PipeMessage.Create(PipeMessageType.NearbyList, list));
        }

        /// <summary>The user picked a nearby peer: send them a contact request.</summary>
        private async Task AddNearbyPeerAsync(string peerId)
        {
            NearbyPeerPayload? peer;
            lock (_nearby) peer = _nearby.TryGetValue(peerId, out var entry) ? entry.Peer : null;
            if (peer == null)
            {
                _logger.LogWarning("AddNearbyPeer: {PeerId} is not in the nearby list (expired?)", peerId);
                return;
            }

            await SendContactRequestAsync(new Contact
            {
                Id = peer.PeerId,
                DisplayName = peer.DisplayName,
                IpAddress = peer.IpAddress,
                Port = peer.Port,
                LastSeen = DateTime.UtcNow
            });
        }

        /// <summary>Consent check used by the TCP server (before acking) and before storing a message.</summary>
        private async Task<bool> AcceptsMessagesFromAsync(string contactId)
        {
            try
            {
                return (await _contactRepo.GetByIdAsync(contactId))?.State == ContactState.Accepted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consent check failed for {ContactId}; rejecting message", contactId);
                return false;   // fail closed
            }
        }

        private async Task HandlePipeMessageAsync(PipeMessage msg)
        {
            switch (msg.Type)
            {
                case PipeMessageType.SendMessage:
                    var send = msg.Deserialize<SendMessagePayload>();
                    var sent = await SendMessageAsync(send.RecipientId, send.PlainText, send.MessageId);
                    if (sent == null)
                        await _pipeServer.BroadcastAsync(
                            PipeMessage.Create(PipeMessageType.SendFailed, send.MessageId));
                    break;

                case PipeMessageType.GetContacts:
                case PipeMessageType.GetNearby:
                    // Contacts, requests (for the badge) and nearby always travel together.
                    await BroadcastRelationshipsAsync();
                    break;

                case PipeMessageType.AddNearbyPeer:
                    await AddNearbyPeerAsync(msg.Deserialize<AddNearbyPeerPayload>().PeerId);
                    break;

                case PipeMessageType.AcceptContactRequest:
                    await AnswerContactRequestAsync(msg.Deserialize<ContactIdPayload>().ContactId, accept: true);
                    break;

                case PipeMessageType.DeclineContactRequest:
                    await AnswerContactRequestAsync(msg.Deserialize<ContactIdPayload>().ContactId, accept: false);
                    break;

                case PipeMessageType.CancelContactRequest:
                    // Local only: if they accept later, their Accept is ignored (we're no longer OutgoingRequest).
                    if (await _contactRepo.TransitionAsync(msg.Deserialize<ContactIdPayload>().ContactId,
                                                           ContactState.OutgoingRequest, ContactState.Stranger))
                        await BroadcastRelationshipsAsync();
                    break;

                case PipeMessageType.GetHistory:
                    var req = msg.Deserialize<GetHistoryPayload>();
                    var hist = await _msgRepo.GetByConversationAsync(
                        req.ConversationId, req.Skip, req.Take);
                    foreach (var m in hist)
                    {
                        try { m.DecryptedContent = _crypto.DecryptForStorage(m.EncryptedContent); }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Message {MessageId} not in storage-encrypted format, treating as legacy plaintext", m.MessageId);
                            m.DecryptedContent = m.EncryptedContent;
                        }
                    }
                    await _pipeServer.BroadcastAsync(PipeMessage.Create(
                        PipeMessageType.MessageHistory, new MessageHistoryPayload(req.ConversationId, hist)));
                    break;

                case PipeMessageType.MarkRead:
                    var mark = msg.Deserialize<MarkReadPayload>();
                    var readMsg = await _msgRepo.GetByMessageIdAsync(mark.MessageId);
                    if (readMsg == null || readMsg.IsOutgoing || readMsg.Status != MessageStatus.Delivered)
                        break;

                    // Record "read, receipt owed" first, THEN try to send. If sending fails (or
                    // the process dies in between) the receipt is still in the database and
                    // FlushOwedAsync delivers it on the next connection.
                    await _msgRepo.UpdateStatusAsync(mark.MessageId, MessageStatus.ReadAckPending);
                    await FlushOwedAsync(readMsg.SenderId);
                    break;

                case PipeMessageType.Connect:
                    var conn = msg.Deserialize<ConnectPayload>();
                    await ConnectToContactAsync(conn.ContactId);
                    break;

                case PipeMessageType.AcceptPeerKey:
                    var accept = msg.Deserialize<AcceptPeerKeyPayload>();
                    await AcceptPeerKeyAsync(accept.ContactId, accept.Fingerprint, accept.ReconnectContactId);
                    break;

                case PipeMessageType.AddContact:
                    var add = msg.Deserialize<AddContactPayload>();
                    var manual = new Contact
                    {
                        Id = $"manual_{add.IpAddress}_{add.Port}",
                        DisplayName = string.IsNullOrWhiteSpace(add.DisplayName)
                                        ? $"{add.IpAddress}:{add.Port}"
                                        : add.DisplayName,
                        IpAddress = add.IpAddress,
                        Port = add.Port,
                        LastSeen = DateTime.UtcNow
                    };
                    // Adding by IP is a contact request too; the handshake reveals the real id.
                    await SendContactRequestAsync(manual);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _discovery.Dispose();
            _server.Dispose();
            _pipeServer.Dispose();

            await _clientsLock.WaitAsync();
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
            _clientsLock.Release();

            _crypto.Dispose();
            await _db.DisposeAsync();
        }
    }
}