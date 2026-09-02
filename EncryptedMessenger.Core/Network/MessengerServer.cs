using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.Models;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Listens for incoming TCP connections from other messenger instances.
    /// </summary>
    public sealed class MessengerServer : IDisposable
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ContactStatusEventArgs>? ContactConnected;
        public event EventHandler<ContactStatusEventArgs>? ContactDisconnected;
        public event EventHandler<DeliveryAckEventArgs>? DeliveryAcknowledged;

        private readonly int _port;
        private readonly string _ownId;
        private readonly string _ownDisplayName;
        private readonly CryptoManager _crypto;

        private TcpListener? _listener;
        private CancellationTokenSource _cts = new();

        private readonly Dictionary<string, NetworkStream> _activeStreams = new();
        private readonly object _streamsLock = new();

        public MessengerServer(int port, string ownId, string ownDisplayName, CryptoManager crypto)
        {
            _port = port;
            _ownId = ownId;
            _ownDisplayName = ownDisplayName;
            _crypto = crypto;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                Debug.WriteLine($"[Server] LISTENING on 0.0.0.0:{_port}  (id={Short(_ownId)})");
                await AcceptLoopAsync(_cts.Token);
            }
            catch (SocketException ex)
            {
                // Most common: port already in use (another copy on same machine same port)
                Debug.WriteLine($"[Server] START FAILED on port {_port}: {ex.SocketErrorCode} – {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Server] START FAILED on port {_port}: {ex.GetType().Name} – {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            _cts.Cancel();
            _listener?.Stop();
            Debug.WriteLine("[Server] stopped");
        }

        // ── Accept loop ───────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
                    Debug.WriteLine($"[Server] INCOMING connection from {remote}");
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Server] accept error: {ex.GetType().Name} – {ex.Message}");
                }
            }
        }

        // ── Per-client handler ────────────────────────────────────────────

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            string? contactId = null;
            var stream = client.GetStream();

            try
            {
                // 1. Send own public key
                await PacketHelper.SendAsync(stream, new NetworkPacket
                {
                    Type = PacketType.KeyExchange,
                    SenderId = _ownId,
                    Payload = _crypto.PublicKeyXml,
                    Timestamp = DateTime.UtcNow
                }, ct);
                Debug.WriteLine("[Server] sent own public key");

                // 2. Receive peer's public key
                var kePkt = await PacketHelper.ReceiveAsync(stream, ct);
                if (kePkt?.Type != PacketType.KeyExchange)
                {
                    Debug.WriteLine($"[Server] handshake abort: expected KeyExchange, got {kePkt?.Type.ToString() ?? "null"}");
                    return;
                }
                contactId = kePkt.SenderId;
                Debug.WriteLine($"[Server] got peer key, contactId={Short(contactId)}");

                // 3. Receive encrypted AES session key
                var skPkt = await PacketHelper.ReceiveAsync(stream, ct);
                if (skPkt?.Type != PacketType.SessionKey)
                {
                    Debug.WriteLine($"[Server] handshake abort: expected SessionKey, got {skPkt?.Type.ToString() ?? "null"}");
                    return;
                }
                _crypto.DecryptAndStoreSessionKey(contactId, skPkt.Payload);
                Debug.WriteLine($"[Server] handshake complete ✓ with {Short(contactId)}");

                lock (_streamsLock) _activeStreams[contactId] = stream;
                ContactConnected?.Invoke(this, new ContactStatusEventArgs(contactId, isOnline: true));

                // 4. Receive loop
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var pkt = await PacketHelper.ReceiveAsync(stream, ct);
                    if (pkt == null) { Debug.WriteLine("[Server] peer closed stream"); break; }
                    await ProcessPacketAsync(pkt, stream, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Server] client handler error ({Short(contactId ?? "?")}): {ex.GetType().Name} – {ex.Message}");
            }
            finally
            {
                client.Close();
                if (contactId != null)
                {
                    lock (_streamsLock) _activeStreams.Remove(contactId);
                    _crypto.RemoveSession(contactId);
                    ContactDisconnected?.Invoke(this, new ContactStatusEventArgs(contactId, isOnline: false));
                    Debug.WriteLine($"[Server] disconnected {Short(contactId)}");
                }
            }
        }

        // ── Packet dispatch ───────────────────────────────────────────────

        private async Task ProcessPacketAsync(NetworkPacket pkt, NetworkStream stream, CancellationToken ct)
        {
            switch (pkt.Type)
            {
                case PacketType.Message:
                    var plain = _crypto.DecryptMessage(pkt.Payload, pkt.SenderId);
                    Debug.WriteLine($"[Server] MESSAGE received from {Short(pkt.SenderId)} id={pkt.MessageId}");
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                        pkt.SenderId, plain, pkt.MessageId ?? string.Empty, pkt.Timestamp));

                    await PacketHelper.SendAsync(stream, new NetworkPacket
                    {
                        Type = PacketType.DeliveryAck,
                        SenderId = _ownId,
                        MessageId = pkt.MessageId,
                        Timestamp = DateTime.UtcNow
                    }, ct);
                    break;

                case PacketType.DeliveryAck:
                case PacketType.ReadAck:
                    if (pkt.MessageId != null)
                        DeliveryAcknowledged?.Invoke(this, new DeliveryAckEventArgs(pkt.MessageId));
                    break;

                case PacketType.Disconnect:
                    break;
            }
        }

        // ── Outbound read-receipt ─────────────────────────────────────────

        public async Task SendReadAckAsync(string contactId, string messageId)
        {
            NetworkStream? stream;
            lock (_streamsLock) _activeStreams.TryGetValue(contactId, out stream);
            if (stream == null) return;

            try
            {
                await PacketHelper.SendAsync(stream, new NetworkPacket
                {
                    Type = PacketType.ReadAck,
                    SenderId = _ownId,
                    MessageId = messageId,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Server] read-ack send failed: {ex.Message}");
            }
        }

        private static string Short(string id) => id.Length > 8 ? id[..8] : id;

        public void Dispose() => Stop();
    }
}