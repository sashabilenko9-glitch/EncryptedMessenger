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
                _loggerFactory.CreateLogger<MessengerServer>());

            _discovery = new PeerDiscovery(
                _settings.UdpPort, _settings.UserId, _settings.DisplayName, _settings.TcpPort,
                _loggerFactory.CreateLogger<PeerDiscovery>());

            _pipeServer = new PipeServer(_loggerFactory.CreateLogger<PipeServer>());

            _server.MessageReceived += OnMessageReceived;
            _server.ContactConnected += OnContactConnected;
            _server.ContactDisconnected += OnContactDisconnected;
            _server.DeliveryAcknowledged += OnDeliveryAcknowledged;
            _discovery.PeerDiscovered += OnPeerDiscovered;
            _pipeServer.MessageReceived += OnPipeMessageReceived;
        }

        public async Task StartAsync()
        {
            _db.EnsureCreated();
            _ = Task.Run(_server.StartAsync);
            if (_settings.Discovery) _ = Task.Run(_discovery.StartAsync);
            _ = Task.Run(_pipeServer.StartAsync);
        }

        // ── Outbound ─────────────────────────────────────────────────────

        public async Task<Message?> SendMessageAsync(string recipientId, string plainText)
        {
            var contact = await _contactRepo.GetByIdAsync(recipientId);
            if (contact == null) return null;

            await _clientsLock.WaitAsync();
            try
            {
                var client = await GetOrCreateClientAsync(recipientId, contact.IpAddress, contact.Port);

                // After a merge the live client may be keyed under the real id.
                var targetId = client.ContactId is { Length: > 0 } realId && realId != recipientId
                               ? realId : recipientId;

                var msgId = Guid.NewGuid().ToString();
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
                await _pipeServer.BroadcastAsync(PipeMessage.Create(
                    PipeMessageType.ContactOnline, new ContactStatusPayload(id, true)));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connect failed for contact {RecipientId}", recipientId);
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

            var client = new MessengerClient(_settings.UserId, _crypto, _loggerFactory.CreateLogger<MessengerClient>());

            client.MessageReceived += OnMessageReceived;
            client.DeliveryAcknowledged += OnDeliveryAcknowledged;
            client.Disconnected += async (_, _) =>
            {
                await _clientsLock.WaitAsync();
                _clients.Remove(recipientId);
                _clientsLock.Release();
                try
                {
                    await _pipeServer.BroadcastAsync(PipeMessage.Create(
                        PipeMessageType.ContactOffline,
                        new ContactStatusPayload(recipientId, false)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to broadcast contact-offline for {ContactId}", recipientId);
                }
            };

            await client.ConnectAsync(ip, port);

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

                var refreshed = await _contactRepo.GetAllAsync();
                await _pipeServer.BroadcastAsync(
                    PipeMessage.Create(PipeMessageType.ContactList, refreshed));
            }

            await VerifyAndStorePeerKeyAsync(finalId, client.PeerPublicKeyXml);

            _clients[finalId] = client;
            return client;
        }

        /// <summary>
        /// Persists the peer's public key for this contact, computes its fingerprint,
        /// and warns (log + UI notification) if it differs from a previously known key
        /// for the same contact — likely a reinstall on their side, or a man-in-the-middle
        /// substituting a different key during the handshake.
        /// </summary>
        private async Task VerifyAndStorePeerKeyAsync(string contactId, string publicKeyXml)
        {
            if (string.IsNullOrEmpty(publicKeyXml)) return;

            var contact = await _contactRepo.GetByIdAsync(contactId);
            var previousKey = contact?.PublicKeyXml;
            var changed = !string.IsNullOrEmpty(previousKey) && previousKey != publicKeyXml;
            var fingerprint = RsaCryptoService.ComputeFingerprint(publicKeyXml);

            if (changed)
            {
                _logger.LogWarning(
                    "SECURITY: public key for contact {ContactId} changed since last time " +
                    "(possible reinstall, or a man-in-the-middle). New fingerprint: {Fingerprint}",
                    contactId, fingerprint);
            }

            if (contact != null)
                await _contactRepo.SetPublicKeyXmlAsync(contactId, publicKeyXml);

            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.KeyFingerprint,
                new KeyFingerprintPayload(contactId, fingerprint, changed)));
        }

        // ── Event handlers ────────────────────────────────────────────────

        private async void OnMessageReceived(object? _, MessageReceivedEventArgs e)
        {
            // Ensure the sender exists as a contact (keyed by their real UserId),
            // so the receiver sees the conversation and history matches.
            var existing = await _contactRepo.GetByIdAsync(e.SenderId);
            if (existing == null)
            {
                await _contactRepo.UpsertAsync(new Contact
                {
                    Id = e.SenderId,
                    DisplayName = e.SenderId[..Math.Min(8, e.SenderId.Length)],
                    LastSeen = DateTime.UtcNow
                });
                var list = await _contactRepo.GetAllAsync();
                await _pipeServer.BroadcastAsync(
                    PipeMessage.Create(PipeMessageType.ContactList, list));
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

        private async void OnContactConnected(object? _, ContactStatusEventArgs e)
        {
            await _contactRepo.UpdateLastSeenAsync(e.ContactId);
            if (e.PublicKeyXml != null)
                await VerifyAndStorePeerKeyAsync(e.ContactId, e.PublicKeyXml);
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.ContactOnline,
                new ContactStatusPayload(e.ContactId, true)));
        }

        private async void OnContactDisconnected(object? _, ContactStatusEventArgs e)
        {
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.ContactOffline,
                new ContactStatusPayload(e.ContactId, false)));
        }

        private async void OnDeliveryAcknowledged(object? _, DeliveryAckEventArgs e)
        {
            await _msgRepo.UpdateStatusAsync(e.MessageId, MessageStatus.Delivered);
            await _pipeServer.BroadcastAsync(
                PipeMessage.Create(PipeMessageType.DeliveryAck, e.MessageId));
        }

        private async void OnPeerDiscovered(object? _, PeerDiscoveredEventArgs e)
        {
            var contact = new Contact
            {
                Id = e.PeerId,
                DisplayName = e.DisplayName,
                IpAddress = e.IpAddress,
                Port = e.Port,
                LastSeen = DateTime.UtcNow
            };
            await _contactRepo.UpsertAsync(contact);
            await _pipeServer.BroadcastAsync(PipeMessage.Create(
                PipeMessageType.ContactOnline,
                new ContactStatusPayload(e.PeerId, true)));
        }

        private async void OnPipeMessageReceived(object? _, PipeMessage msg)
        {
            switch (msg.Type)
            {
                case PipeMessageType.SendMessage:
                    var send = msg.Deserialize<SendMessagePayload>();
                    await SendMessageAsync(send.RecipientId, send.PlainText);
                    break;

                case PipeMessageType.GetContacts:
                    var contacts = await _contactRepo.GetAllAsync();
                    await _pipeServer.BroadcastAsync(
                        PipeMessage.Create(PipeMessageType.ContactList, contacts));
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
                    await _pipeServer.BroadcastAsync(
                        PipeMessage.Create(PipeMessageType.MessageHistory, hist));
                    break;

                case PipeMessageType.MarkRead:
                    var mark = msg.Deserialize<MarkReadPayload>();
                    await _msgRepo.UpdateStatusAsync(mark.MessageId, MessageStatus.Read);
                    break;

                case PipeMessageType.Connect:
                    var conn = msg.Deserialize<ConnectPayload>();
                    await ConnectToContactAsync(conn.ContactId);
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
                    await _contactRepo.UpsertAsync(manual);

                    var updated = await _contactRepo.GetAllAsync();
                    await _pipeServer.BroadcastAsync(
                        PipeMessage.Create(PipeMessageType.ContactList, updated));
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
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