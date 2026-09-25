using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Outbound TCP connection to a single peer.
    /// </summary>
    public sealed class MessengerClient : IDisposable
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<DeliveryAckEventArgs>? DeliveryAcknowledged;
        public event EventHandler<ContactControlEventArgs>? ContactControlReceived;
        public event EventHandler? Disconnected;

        private readonly string _ownId;
        private readonly CryptoManager _crypto;
        private readonly ILogger _logger;

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private CancellationTokenSource _cts = new();
        private string _sessionId = string.Empty;

        public string ContactId { get; private set; } = string.Empty;
        public string PeerPublicKeyXml { get; private set; } = string.Empty;
        public bool IsConnected => _tcp?.Connected ?? false;

        private readonly PeerKeyVerifier? _verifyPeerKey;

        /// <param name="verifyPeerKey">
        /// Consulted after the peer's KeyExchange and before our session key is sent.
        /// Null = accept any key (used only by tests/tools that have no contact store).
        /// </param>
        public MessengerClient(string ownId, CryptoManager crypto, ILogger? logger = null, PeerKeyVerifier? verifyPeerKey = null)
        {
            _ownId = ownId;
            _crypto = crypto;
            _logger = logger ?? NullLogger.Instance;
            _verifyPeerKey = verifyPeerKey;
        }

        public async Task ConnectAsync(string ip, int port, CancellationToken ct = default)
        {
            _logger.LogInformation("Connecting to {Ip}:{Port}", ip, port);
            _cts = new CancellationTokenSource();
            _tcp = new TcpClient { NoDelay = true };

            try
            {
                // Add a 5s connect timeout so a wrong IP fails fast instead of hanging
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await _tcp.ConnectAsync(ip, port, timeoutCts.Token);
                _logger.LogDebug("TCP connected to {Ip}:{Port}", ip, port);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connect timeout to {Ip}:{Port} (port closed / no listener / blocked)", ip, port);
                throw new IOException($"Connection to {ip}:{port} failed (timeout).");
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Socket error connecting to {Ip}:{Port}: {SocketError}", ip, port, ex.SocketErrorCode);
                throw;
            }

            _stream = _tcp.GetStream();

            // 1. Receive peer's public key
            _logger.LogDebug("Waiting for KeyExchange…");
            var kePkt = await PacketHelper.ReceiveAsync(_stream, ct)
                        ?? throw new IOException("Handshake failed: no KeyExchange packet.");
            if (kePkt.Type != PacketType.KeyExchange)
                throw new IOException($"Expected KeyExchange, got {kePkt.Type}");

            ContactId = kePkt.SenderId;
            var peerPublicKey = kePkt.Payload;
            PeerPublicKeyXml = peerPublicKey;
            _logger.LogDebug("Got peer key, contactId={ContactId}", ContactId);

            // 1b. Key pinning: is this the key we know for this contact? Checked BEFORE
            // step 3 — otherwise we'd already have handed a session key to whoever this is.
            if (_verifyPeerKey != null && !await _verifyPeerKey(ContactId, peerPublicKey))
            {
                _logger.LogWarning("Aborting handshake: untrusted key for {ContactId}", ContactId);
                throw new UntrustedPeerKeyException(ContactId);
            }

            // 2. Send own public key
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.KeyExchange,
                SenderId = _ownId,
                Payload = _crypto.PublicKeyXml,
                Timestamp = DateTime.UtcNow
            }, ct);

            // 3. Create AES session key and send it encrypted
            _sessionId = CryptoManager.NewSessionId(ContactId);
            var encryptedSessionKey = _crypto.CreateAndEncryptSessionKey(_sessionId, peerPublicKey);
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.SessionKey,
                SenderId = _ownId,
                Payload = encryptedSessionKey,
                Timestamp = DateTime.UtcNow
            }, ct);

            _logger.LogInformation("Handshake complete with {ContactId}", ContactId);
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }

        public async Task SendMessageAsync(string plainText, string messageId)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected.");

            var encrypted = _crypto.EncryptMessage(plainText, _sessionId);
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.Message,
                SenderId = _ownId,
                RecipientId = ContactId,
                Payload = encrypted,
                MessageId = messageId,
                Timestamp = DateTime.UtcNow
            });
            _logger.LogInformation("Message sent id={MessageId}", messageId);
        }

        /// <summary>
        /// Sends an unencrypted control packet (ReadAck, ContactRequest/Accept/Decline, NotAContact)
        /// over this outbound connection; the peer's MessengerServer handles it like one arriving
        /// on the connection it opened. The connection itself is key-pinned, which is what makes
        /// these packets trustworthy. Returns false if not connected or the write failed.
        /// Callers must not write concurrently with <see cref="SendMessageAsync"/>;
        /// MessengerService does both only while holding its clients lock.
        /// </summary>
        public async Task<bool> SendControlAsync(PacketType type, string? messageId = null, string payload = "")
        {
            if (_stream == null || !IsConnected) return false;
            try
            {
                await PacketHelper.SendAsync(_stream, new NetworkPacket
                {
                    Type = type,
                    SenderId = _ownId,
                    RecipientId = ContactId,
                    MessageId = messageId,
                    Payload = payload,
                    Timestamp = DateTime.UtcNow
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{PacketType} send failed for {ContactId}", type, ContactId);
                return false;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && (_tcp?.Connected ?? false))
                {
                    var pkt = await PacketHelper.ReceiveAsync(_stream!, ct);
                    if (pkt == null) break;

                    switch (pkt.Type)
                    {
                        case PacketType.Message:
                            // Attribute to the handshake identity, never to the self-declared SenderId.
                            var plain = _crypto.DecryptMessage(pkt.Payload, _sessionId);
                            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                                ContactId, plain, pkt.MessageId ?? string.Empty, pkt.Timestamp));
                            break;

                        case PacketType.DeliveryAck:
                        case PacketType.ReadAck:
                            if (pkt.MessageId != null)
                                DeliveryAcknowledged?.Invoke(this, new DeliveryAckEventArgs(
                                    pkt.MessageId, isRead: pkt.Type == PacketType.ReadAck));
                            break;

                        case PacketType.ContactRequest:
                        case PacketType.ContactAccept:
                        case PacketType.ContactDecline:
                        case PacketType.NotAContact:
                            ContactControlReceived?.Invoke(this, new ContactControlEventArgs(
                                ContactId, pkt.Type, pkt.Payload, pkt.MessageId));
                            break;

                        case PacketType.Disconnect:
                            goto exit;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Receive loop error for {ContactId}", ContactId); }

        exit:
            _crypto.RemoveSession(_sessionId);
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _stream?.Close();
            _tcp?.Close();
        }
    }
}
