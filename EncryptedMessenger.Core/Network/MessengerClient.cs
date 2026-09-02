using System.Diagnostics;
using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.Models;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Outbound TCP connection to a single peer.
    /// </summary>
    public sealed class MessengerClient : IDisposable
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<DeliveryAckEventArgs>? DeliveryAcknowledged;
        public event EventHandler? Disconnected;

        private readonly string _ownId;
        private readonly CryptoManager _crypto;

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private CancellationTokenSource _cts = new();

        public string ContactId { get; private set; } = string.Empty;
        public bool IsConnected => _tcp?.Connected ?? false;

        public MessengerClient(string ownId, CryptoManager crypto)
        {
            _ownId = ownId;
            _crypto = crypto;
        }

        public async Task ConnectAsync(string ip, int port, CancellationToken ct = default)
        {
            Debug.WriteLine($"[Client] ConnectAsync → {ip}:{port}");
            _cts = new CancellationTokenSource();
            _tcp = new TcpClient { NoDelay = true };

            try
            {
                // Add a 5s connect timeout so a wrong IP fails fast instead of hanging
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await _tcp.ConnectAsync(ip, port, timeoutCts.Token);
                Debug.WriteLine($"[Client] TCP connected to {ip}:{port}");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[Client] CONNECT TIMEOUT → {ip}:{port} (порт закрыт / нет приёмника / блокировка)");
                throw new IOException($"Verbindung zu {ip}:{port} fehlgeschlagen (Timeout).");
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"[Client] SOCKET ERROR → {ip}:{port}: {ex.SocketErrorCode} – {ex.Message}");
                throw;
            }

            _stream = _tcp.GetStream();

            // 1. Receive peer's public key
            Debug.WriteLine("[Client] waiting for KeyExchange…");
            var kePkt = await PacketHelper.ReceiveAsync(_stream, ct)
                        ?? throw new IOException("Handshake failed: no KeyExchange packet.");
            if (kePkt.Type != PacketType.KeyExchange)
                throw new IOException($"Expected KeyExchange, got {kePkt.Type}");

            ContactId = kePkt.SenderId;
            var peerPublicKey = kePkt.Payload;
            Debug.WriteLine($"[Client] got peer key, contactId={ContactId}");

            // 2. Send own public key
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.KeyExchange,
                SenderId = _ownId,
                Payload = _crypto.PublicKeyXml,
                Timestamp = DateTime.UtcNow
            }, ct);

            // 3. Create AES session key and send it encrypted
            var encryptedSessionKey = _crypto.CreateAndEncryptSessionKey(ContactId, peerPublicKey);
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.SessionKey,
                SenderId = _ownId,
                Payload = encryptedSessionKey,
                Timestamp = DateTime.UtcNow
            }, ct);

            Debug.WriteLine("[Client] handshake complete ✓");
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }

        public async Task SendMessageAsync(string plainText, string messageId)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected.");

            var encrypted = _crypto.EncryptMessage(plainText, ContactId);
            await PacketHelper.SendAsync(_stream, new NetworkPacket
            {
                Type = PacketType.Message,
                SenderId = _ownId,
                RecipientId = ContactId,
                Payload = encrypted,
                MessageId = messageId,
                Timestamp = DateTime.UtcNow
            });
            Debug.WriteLine($"[Client] message sent id={messageId}");
        }

        public async Task DisconnectAsync()
        {
            if (_stream != null)
            {
                try
                {
                    await PacketHelper.SendAsync(_stream, new NetworkPacket
                    {
                        Type = PacketType.Disconnect,
                        SenderId = _ownId,
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch { }
            }
            Dispose();
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
                            var plain = _crypto.DecryptMessage(pkt.Payload, pkt.SenderId);
                            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                                pkt.SenderId, plain, pkt.MessageId ?? string.Empty, pkt.Timestamp));
                            break;

                        case PacketType.DeliveryAck:
                        case PacketType.ReadAck:
                            if (pkt.MessageId != null)
                                DeliveryAcknowledged?.Invoke(this, new DeliveryAckEventArgs(pkt.MessageId));
                            break;

                        case PacketType.Disconnect:
                            goto exit;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[Client] recv loop error: {ex.Message}"); }

        exit:
            _crypto.RemoveSession(ContactId);
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